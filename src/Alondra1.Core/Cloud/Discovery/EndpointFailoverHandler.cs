using System.Net;

namespace Alondra1.Core.Cloud.Discovery;

/// <summary>The endpoints in use and their recent health. Thread-safe; holds no secrets.</summary>
public sealed class EndpointSelector
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<DiscoveryEndpoint> _endpoints;
    private readonly Dictionary<string, DateTimeOffset> _downUntil = new(StringComparer.Ordinal);

    public EndpointSelector(IReadOnlyList<DiscoveryEndpoint> endpoints, Func<DateTimeOffset>? now = null)
    {
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is needed.", nameof(endpoints));
        _endpoints = endpoints;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Default pause for an endpoint after a transport failure or a 502/503/504 without Retry-After.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(30);

    public IReadOnlyList<DiscoveryEndpoint> Endpoints { get { lock (_gate) return _endpoints; } }

    /// <summary>Replaces the list after a discovery refresh; health of endpoints that stay is kept.</summary>
    public void Update(IReadOnlyList<DiscoveryEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
            return;
        lock (_gate)
            _endpoints = endpoints;
    }

    /// <summary>Endpoints to try for one request: healthy ones in priority order, then paused ones (better than none).</summary>
    public IReadOnlyList<DiscoveryEndpoint> Candidates()
    {
        lock (_gate)
        {
            var now = _now();
            return [.. _endpoints.Where(e => !_downUntil.TryGetValue(e.Id, out var until) || until <= now),
                    .. _endpoints.Where(e => _downUntil.TryGetValue(e.Id, out var until) && until > now)];
        }
    }

    public void MarkDown(DiscoveryEndpoint endpoint, TimeSpan pause)
    {
        lock (_gate)
            _downUntil[endpoint.Id] = _now() + pause;
    }

    public void MarkUp(DiscoveryEndpoint endpoint)
    {
        lock (_gate)
            _downUntil.Remove(endpoint.Id);
    }
}

/// <summary>
/// Sends each API request to the best endpoint and moves to the next one only when that is safe (discovery/README.md,
/// failover): on transport errors, timeouts and HTTP 502/503/504 (honouring Retry-After as the pause of that endpoint).
/// 401/403/404/429 and every other answer are returned as they are. Requests are re-sent unchanged (same body, same
/// mutation ids), so the server's idempotency keeps retries safe. A token refresh is never sent to a second endpoint
/// after a transport failure: whether the first one rotated the token is unknown, and a replay would revoke the session.
/// </summary>
public sealed class EndpointFailoverHandler(EndpointSelector selector, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly HashSet<HttpStatusCode> Gateway = [HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

    public static bool IsRefresh(Uri uri) => uri.AbsolutePath.EndsWith("/v1/auth/refresh", StringComparison.Ordinal) ||
                                             uri.AbsolutePath.EndsWith("/v1/browser/session/refresh", StringComparison.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var refresh = IsRefresh(request.RequestUri!);
        var candidates = selector.Candidates();
        HttpResponseMessage? last = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var endpoint = candidates[i];
            var attempt = Rewrite(request, endpoint, body);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when ((e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested)))
            {
                selector.MarkDown(endpoint, EndpointSelector.DefaultPause);
                if (refresh || i == candidates.Count - 1)
                    throw;
                continue;
            }
            if (!Gateway.Contains(response.StatusCode))
            {
                selector.MarkUp(endpoint);
                last?.Dispose();
                return response;
            }
            selector.MarkDown(endpoint, RetryAfter(response) ?? EndpointSelector.DefaultPause);
            if (refresh || i == candidates.Count - 1)
            {
                last?.Dispose();
                return response;
            }
            last?.Dispose();
            last = response;
        }
        return last ?? throw new HttpRequestException("No cloud endpoint is available.");
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static HttpRequestMessage Rewrite(HttpRequestMessage original, DiscoveryEndpoint endpoint, byte[]? body)
    {
        var target = new UriBuilder(original.RequestUri!)
        {
            Scheme = endpoint.Url.Scheme,
            Host = endpoint.Url.Host,
            Port = endpoint.Url.IsDefaultPort ? -1 : endpoint.Url.Port,
        }.Uri;
        var copy = new HttpRequestMessage(original.Method, target) { Version = original.Version };
        foreach (var header in original.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }
}
