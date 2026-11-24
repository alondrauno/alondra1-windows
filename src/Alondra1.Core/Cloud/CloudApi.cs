using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Alondra1.Core.Ports;
using Alondra1.Core.Security;

namespace Alondra1.Core.Cloud;

/// <summary>A failed cloud call with the server's stable error code, or NETWORK when the server could not be reached.</summary>
public sealed class CloudApiException(int status, string code, string message, JsonObject? error = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public JsonObject? Error { get; } = error;
    public bool IsNetwork => Code == "NETWORK";
}

/// <summary>Where the session lives: the refresh token in the Windows secret store (DPAPI), the access token in memory.</summary>
public interface ICloudTokens
{
    string? Access { get; set; }
    Task<string?> LoadRefreshAsync(CancellationToken cancellationToken);
    Task SaveRefreshAsync(string? refreshToken, CancellationToken cancellationToken);
}

/// <summary>Refresh token in <see cref="ISecretStore"/> (DPAPI, current Windows user), never in todo.db, config or logs.</summary>
public sealed class SecretStoreTokens(ISecretStore secrets) : ICloudTokens
{
    public const string AccountId = "alondra1-cloud";

    public string? Access { get; set; }

    public async Task<string?> LoadRefreshAsync(CancellationToken cancellationToken) =>
        (await secrets.LoadAsync(AccountId, cancellationToken).ConfigureAwait(false)).Password;

    public Task SaveRefreshAsync(string? refreshToken, CancellationToken cancellationToken) => refreshToken is null
        ? secrets.DeleteAsync(AccountId, cancellationToken)
        : secrets.SaveAsync(AccountId, new SecretBundle { Password = refreshToken }, cancellationToken);
}

/// <summary>
/// Minimal client of the Alondra1 API (alondra1-backend contract/openapi.yaml). Bearer auth with one refresh at a time,
/// X-Request-ID on every request, HTTPS only except for a local development server. Bodies are never logged.
/// </summary>
public sealed class CloudApi
{
    private readonly HttpClient _http;
    private readonly ICloudTokens _tokens;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public CloudApi(Uri baseUri, ICloudTokens tokens, HttpMessageHandler? handler = null)
    {
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
            throw new ArgumentException("The cloud address must use HTTPS (plain HTTP only for a local development server).", nameof(baseUri));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _tokens = tokens;
    }

    public ICloudTokens Tokens => _tokens;

    private static HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content, string? tenant, string? token)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Request-ID", Guid.NewGuid().ToString("N")[..24]);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Tenant-ID", tenant);
        return request;
    }

    private static HttpContent? Json(JsonNode? body) =>
        body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(build(), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new CloudApiException(0, "NETWORK", "The Alondra1 cloud could not be reached.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudApiException(0, "NETWORK", "The Alondra1 cloud did not answer in time.");
        }
    }

    private static async Task<JsonObject?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static CloudApiException Failure(HttpStatusCode status, JsonObject? body)
    {
        var error = body?["error"] as JsonObject;
        return new CloudApiException((int)status, error?["code"]?.GetValue<string>() ?? $"HTTP_{(int)status}",
            error?["message"]?.GetValue<string>() ?? $"The request failed ({(int)status}).", error);
    }

    public async Task<JsonObject> AnonymousAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Request(method, path, Json(body), null, null), cancellationToken).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, json);
        return json ?? [];
    }

    /// <summary>Authenticated call; refreshes once on 401. Content is created per attempt (it cannot be resent).</summary>
    public async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, Func<HttpContent?> content, string? tenant, CancellationToken cancellationToken)
    {
        var token = _tokens.Access ?? await RefreshAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CloudApiException(401, "UNAUTHENTICATED", "Sign in again.");
        var response = await SendAsync(() => Request(method, path, content(), tenant, token), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;
        response.Dispose();
        var fresh = await RefreshAsync(cancellationToken).ConfigureAwait(false) ?? throw new CloudApiException(401, "UNAUTHENTICATED", "Sign in again.");
        return await SendAsync(() => Request(method, path, content(), tenant, fresh), cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonObject> CallAsync(HttpMethod method, string path, JsonNode? body = null, string? tenant = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(method, path, () => Json(body), tenant, cancellationToken).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, json);
        return json ?? [];
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, await ReadAsync(response, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Rotates the refresh token (one at a time). Null = signed out (revoked, reused or expired).</summary>
    public async Task<string?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await _tokens.LoadRefreshAsync(cancellationToken).ConfigureAwait(false);
            if (refresh is null)
                return null;
            using var response = await SendAsync(() => Request(HttpMethod.Post, "/v1/auth/refresh", Json(new JsonObject { ["refresh_token"] = refresh }), null, null), cancellationToken)
                .ConfigureAwait(false);
            var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && json is not null)
            {
                await SaveSessionAsync(json, cancellationToken).ConfigureAwait(false);
                return _tokens.Access;
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _tokens.Access = null;
                await _tokens.SaveRefreshAsync(null, cancellationToken).ConfigureAwait(false);
                return null;
            }

            throw new CloudApiException((int)response.StatusCode, "NETWORK", "The Alondra1 cloud is not available right now.");
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task SaveSessionAsync(JsonObject tokenResponse, CancellationToken cancellationToken)
    {
        _tokens.Access = tokenResponse["access_token"]?.GetValue<string>();
        await _tokens.SaveRefreshAsync(tokenResponse["refresh_token"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
    }
}
