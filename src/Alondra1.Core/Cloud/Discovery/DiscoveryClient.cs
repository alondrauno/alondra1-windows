using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alondra1.Core.Cloud.Discovery;

/// <summary>Where the current endpoint list came from.</summary>
public enum EndpointSource { Primary, Mirror, Cache, Bootstrap }

public sealed record EndpointSet(IReadOnlyList<DiscoveryEndpoint> Endpoints, EndpointSource Source, long Version, DateTimeOffset ValidUntil);

/// <summary>
/// Finds the API endpoints of this build's environment (alondra1-infra discovery/README.md, client behaviour):
/// primary, then the independent mirror, then the last verified manifest cached in the config folder, then the
/// bundled bootstrap list while it is valid. Downloads follow no redirects and use conditional GET. The cache holds no
/// secrets and is written atomically; it is verified again every time it is used. Only called once the user turned the
/// cloud on: a local-only app never contacts discovery.
/// </summary>
public sealed class DiscoveryClient
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly string _environment;
    private readonly IReadOnlyList<TrustedKey> _trust;
    private readonly IReadOnlyList<Uri> _sources;
    private readonly string _cachePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly IReadOnlyList<DiscoveryEndpoint> _bootstrap;
    private readonly DateTimeOffset _bootstrapValidUntil;

    public DiscoveryClient(HttpMessageHandler handler, string environment, IReadOnlyList<TrustedKey> trust, IReadOnlyList<Uri> sources,
        string cachePath, IReadOnlyList<DiscoveryEndpoint> bootstrap, DateTimeOffset bootstrapValidUntil, Func<DateTimeOffset>? now = null)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _environment = environment;
        _trust = trust;
        _sources = sources;
        _cachePath = cachePath;
        _bootstrap = bootstrap;
        _bootstrapValidUntil = bootstrapValidUntil;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The default handler: no redirects (a redirect could point anywhere), no cookies.</summary>
    public static HttpMessageHandler DefaultHandler() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>Next refresh: 15 minutes with ±20 % jitter, so clients do not refresh in lock-step.</summary>
    public static TimeSpan NextRefresh(Random random) => RefreshInterval * (0.8 + random.NextDouble() * 0.4);

    /// <summary>What to use right now without the network (cache, then bootstrap).</summary>
    public EndpointSet? Offline()
    {
        var cache = LoadCache();
        if (cache is not null)
        {
            try
            {
                var verified = DiscoveryManifest.Verify(cache.Manifest, cache.Signature, _environment, _trust, _now(), null);
                return new EndpointSet(verified.Endpoints, EndpointSource.Cache, verified.Version, verified.ExpiresAt);
            }
            catch (DiscoveryException)
            {
                // expired or no longer trusted: fall through
            }
        }
        return _now() < _bootstrapValidUntil ? new EndpointSet(_bootstrap, EndpointSource.Bootstrap, 0, _bootstrapValidUntil) : null;
    }

    /// <summary>Refreshes from the network; falls back to <see cref="Offline"/>. Errors of each source are reported in
    /// <paramref name="problems"/> (codes only, never content).</summary>
    public async Task<EndpointSet?> ResolveAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (_trust.Count == 0)
        {
            problems.Add("no discovery key trusted by this build");
            return Offline();
        }
        var cache = LoadCache();
        (long, byte[])? cached = cache is null ? null : (DiscoveryManifest.PeekVersion(cache.Manifest), cache.Manifest);
        for (var i = 0; i < _sources.Count; i++)
        {
            var source = i == 0 ? EndpointSource.Primary : EndpointSource.Mirror;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _sources[i]);
                if (cache?.ETag is { Length: > 0 } etag && cache.Source == _sources[i].ToString())
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
                {
                    var verified = DiscoveryManifest.Verify(cache.Manifest, cache.Signature, _environment, _trust, _now(), null);
                    return new EndpointSet(verified.Endpoints, source, verified.Version, verified.ExpiresAt);
                }
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    problems.Add($"{source}: HTTP {(int)response.StatusCode}");
                    continue;
                }
                var manifest = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
                using var sigResponse = await _http.GetAsync(new Uri(_sources[i] + ".sig"), cancellationToken).ConfigureAwait(false);
                if (sigResponse.StatusCode != HttpStatusCode.OK)
                {
                    problems.Add($"{source}: signature HTTP {(int)sigResponse.StatusCode}");
                    continue;
                }
                var signature = await ReadBoundedAsync(sigResponse, cancellationToken).ConfigureAwait(false);
                var result = DiscoveryManifest.Verify(manifest, signature, _environment, _trust, _now(), cached);
                SaveCache(new CacheEntry(manifest, signature, response.Headers.ETag?.ToString(), _sources[i].ToString()));
                return new EndpointSet(result.Endpoints, source, result.Version, result.ExpiresAt);
            }
            catch (DiscoveryException e)
            {
                problems.Add($"{source}: {e.Code}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                problems.Add($"{source}: unreachable");
            }
        }
        return Offline();
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > DiscoveryManifest.MaxBytes)
            throw new DiscoveryException("TOO_LARGE", "response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > DiscoveryManifest.MaxBytes)
                throw new DiscoveryException("TOO_LARGE", "response too large");
        }
        return buffer.ToArray();
    }

    private sealed record CacheEntry(byte[] Manifest, byte[] Signature, string? ETag, string Source);

    private CacheEntry? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;
            var node = JsonNode.Parse(File.ReadAllText(_cachePath)) as JsonObject;
            if (node is null)
                return null;
            return new CacheEntry(Convert.FromBase64String(node["manifest"]!.GetValue<string>()), Convert.FromBase64String(node["signature"]!.GetValue<string>()),
                node["etag"]?.GetValue<string>(), node["source"]?.GetValue<string>() ?? "");
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException or NullReferenceException)
        {
            return null; // a damaged cache is ignored, never trusted
        }
    }

    private void SaveCache(CacheEntry entry)
    {
        var json = new JsonObject
        {
            ["manifest"] = Convert.ToBase64String(entry.Manifest),
            ["signature"] = Convert.ToBase64String(entry.Signature),
            ["etag"] = entry.ETag,
            ["source"] = entry.Source,
            ["saved_at"] = _now().ToString("O"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temp = _cachePath + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _cachePath, overwrite: true);
    }
}
