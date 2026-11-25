using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alondra1.Core.Cloud.Discovery;

/// <summary>One API entry point of a verified manifest.</summary>
public sealed record DiscoveryEndpoint(string Id, Uri Url, string Region, int Priority);

/// <summary>A public key this build trusts for one environment.</summary>
public sealed record TrustedKey(string KeyId, string Environment, byte[] PublicKey);

/// <summary>A verified manifest: its bytes (for the cache and the rollback check) and the endpoints to try, in order.</summary>
public sealed record VerifiedManifest(long Version, DateTimeOffset ExpiresAt, IReadOnlyList<DiscoveryEndpoint> Endpoints, byte[] Bytes, byte[] Signature);

/// <summary>A refused manifest with the contract error code (alondra1-infra discovery/README.md).</summary>
public sealed class DiscoveryException(string code, string detail) : Exception($"{code}: {detail}")
{
    public string Code { get; } = code;
}

/// <summary>
/// Verification of the signed cloud discovery manifest (<c>alondra1.discovery/v1</c>). The rules, their order and the
/// error codes are normative and shared by every client; <c>contract/fixtures/discovery-v1/cases.json</c> holds the
/// cases every client must pass. Nothing in the manifest is read before its signature is verified.
/// </summary>
public static partial class DiscoveryManifest
{
    public const string Schema = "alondra1.discovery/v1";
    public const int MaxBytes = 64 * 1024;
    public const string Service = "alondra1-api";
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(90);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    [GeneratedRegex("^[a-z0-9-]{1,32}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[a-z0-9]{2,16}$")] private static partial Regex RegionPattern();
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?-)?api\.alondra1\.com$")] private static partial Regex ProdHost();
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?-)?api\.alondra1\.dev$")] private static partial Regex DevHost();
    [GeneratedRegex("^[A-Za-z0-9+/]*={0,2}$")] private static partial Regex Base64Chars();

    /// <summary>Whether a host may appear in a manifest of the environment (a dev host never matches prod and the reverse).</summary>
    public static bool HostAllowed(string environment, string host) => environment switch
    {
        "prod" => ProdHost().IsMatch(host),
        "dev" => DevHost().IsMatch(host),
        _ => false,
    };

    public static VerifiedManifest Verify(byte[] manifest, byte[] signature, string environment, IReadOnlyList<TrustedKey> trusted,
        DateTimeOffset now, (long Version, byte[] Bytes)? cached)
    {
        if (manifest.Length > MaxBytes || signature.Length > MaxBytes)
            throw new DiscoveryException("TOO_LARGE", "manifest or signature too large");

        // 1. Signature over the exact bytes.
        string alg, keyId, sig;
        try
        {
            using var doc = JsonDocument.Parse(signature);
            alg = Str(doc.RootElement, "alg");
            keyId = Str(doc.RootElement, "key_id");
            sig = Str(doc.RootElement, "signature");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DiscoveryException("BAD_SIGNATURE", "signature file is not valid");
        }
        if (alg != "Ed25519")
            throw new DiscoveryException("BAD_SIGNATURE", "unsupported algorithm");
        var key = trusted.FirstOrDefault(k => k.KeyId == keyId && k.Environment == environment)
            ?? throw new DiscoveryException("UNTRUSTED_KEY", $"key {keyId} is not trusted for {environment}");
        var raw = StrictBase64(sig);
        if (raw is null || raw.Length != 64)
            throw new DiscoveryException("BAD_SIGNATURE", "signature is not a base64 Ed25519 signature");
        if (!Ed25519.Verify(key.PublicKey, manifest, raw))
            throw new DiscoveryException("BAD_SIGNATURE", "signature does not match the manifest");

        // 2. Structure.
        JsonElement root;
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(manifest);
            root = parsed.RootElement;
        }
        catch (JsonException e)
        {
            throw new DiscoveryException("MALFORMED", e.Message);
        }
        using (parsed)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new DiscoveryException("MALFORMED", "manifest is not an object");
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema)
                throw new DiscoveryException("UNSUPPORTED_SCHEMA", "schema");

            // 3. Environment.
            var env = root.TryGetProperty("environment", out var e1) && e1.ValueKind == JsonValueKind.String ? e1.GetString() : "";
            if (env != environment)
                throw new DiscoveryException("WRONG_ENVIRONMENT", $"manifest is for {env}");

            // 4. Validity window.
            var issued = Time(root, "issued_at");
            var expires = Time(root, "expires_at");
            if (issued is null || expires is null || expires <= issued || expires - issued > MaxValidity)
                throw new DiscoveryException("BAD_VALIDITY", "validity");
            if (issued > now + ClockSkew)
                throw new DiscoveryException("NOT_YET_VALID", "issued in the future");
            if (now >= expires)
                throw new DiscoveryException("EXPIRED", "expired");

            // 5. Monotonic version.
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var version) || version < 1)
                throw new DiscoveryException("MALFORMED", "version must be a positive integer");
            if (cached is { } c)
            {
                if (version < c.Version)
                    throw new DiscoveryException("ROLLBACK", "older than the cached manifest");
                if (version == c.Version && !manifest.AsSpan().SequenceEqual(c.Bytes))
                    throw new DiscoveryException("ROLLBACK", "same version, different content");
            }

            // 6. Endpoints.
            if (!root.TryGetProperty("endpoints", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new DiscoveryException("NO_ENDPOINTS", "no endpoints");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var usable = new List<DiscoveryEndpoint>();
            foreach (var e in list.EnumerateArray())
            {
                var endpoint = CheckEndpoint(environment, e, out var enabled, out var v1);
                if (!seen.Add(endpoint.Id))
                    throw new DiscoveryException("BAD_ENDPOINT", $"duplicate endpoint id {endpoint.Id}");
                if (enabled && v1)
                    usable.Add(endpoint);
            }
            if (usable.Count == 0)
                throw new DiscoveryException("NO_ENDPOINTS", "no enabled endpoint supports API v1");
            var ordered = usable.OrderBy(e => e.Priority).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            return new VerifiedManifest(version, expires.Value, ordered, manifest, signature);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString()! : throw new InvalidOperationException(name);

    private static DateTimeOffset? Time(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
    }

    /// <summary>Standard base64 with padding, canonical (re-encodes to the same text), no whitespace.</summary>
    private static byte[]? StrictBase64(string text)
    {
        if (text.Length % 4 != 0 || !Base64Chars().IsMatch(text))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(text);
            return Convert.ToBase64String(bytes) == text ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DiscoveryEndpoint CheckEndpoint(string environment, JsonElement e, out bool enabled, out bool v1)
    {
        string S(string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        var id = S("id");
        if (!IdPattern().IsMatch(id))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint id {id}");
        if (!RegionPattern().IsMatch(S("region")))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: region");
        var priority = e.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : -1;
        if (priority is < 0 or > 1000)
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: priority");
        if (S("service") != Service)
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: service");
        var url = CheckUrl(environment, id, S("url"));
        enabled = e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        v1 = e.TryGetProperty("api_versions", out var versions) && versions.ValueKind == JsonValueKind.Array &&
             versions.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "v1");
        return new DiscoveryEndpoint(id, url, S("region"), priority);
    }

    /// <summary>https + lowercase allowed host only: no credentials, port, path (other than "/"), query or fragment.</summary>
    private static Uri CheckUrl(string environment, string id, string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: scheme must be https");
        var rest = url["https://".Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var tail = end < 0 ? "" : rest[end..];
        if (authority.Contains('@'))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: credentials in the url");
        if (authority.Contains(':'))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: port in the url");
        if (tail is not ("" or "/"))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: the url must be an origin");
        if (!HostAllowed(environment, authority))
            throw new DiscoveryException("BAD_ENDPOINT", $"endpoint {id}: host {authority} is not allowed for {environment}");
        return new Uri("https://" + authority + "/");
    }

    /// <summary>Parses a trust file (<c>{"keys": [{"key_id", "environment", "public_key"}]}</c>).</summary>
    public static IReadOnlyList<TrustedKey> ParseTrust(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var keys = new List<TrustedKey>();
        foreach (var k in doc.RootElement.GetProperty("keys").EnumerateArray())
            keys.Add(new TrustedKey(k.GetProperty("key_id").GetString()!, k.GetProperty("environment").GetString()!,
                Convert.FromBase64String(k.GetProperty("public_key").GetString()!)));
        return keys;
    }

    /// <summary>The cached manifest's version, read without trusting it (the bytes are verified again before use).</summary>
    public static long PeekVersion(byte[] manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            return doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt64(out var n) ? n : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
