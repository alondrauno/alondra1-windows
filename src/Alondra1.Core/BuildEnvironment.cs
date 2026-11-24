using Alondra1.Core.Cloud.Discovery;

namespace Alondra1.Core;

/// <summary>
/// The environment this build belongs to, fixed at compile time (spec v1.1 §8.2, §10.3). A dev build
/// (<c>-p:Alondra1Environment=dev</c>, constant ALONDRA1_DEV) has its own installer identity, data folder, DPAPI entropy
/// and window badge, talks only to dev endpoints and trusts only dev discovery keys; a prod build never names a dev
/// host. Native clients contain public configuration only.
/// </summary>
public static class BuildEnvironment
{
#if ALONDRA1_DEV
    public const string Name = "dev";

    public const string DisplayName = "Alondra1 Dev";
    /// <summary>Folder below %LOCALAPPDATA%: dev data never mixes with an installed prod app.</summary>
    public const string DataFolder = "alondra1-dev";
    public const string ApiOrigin = "https://api.dev.alondra1.com";
    public static readonly Uri[] DiscoveryUrls =
        [new("https://config.dev.alondra1.com/manifest.json"), new("https://config2.dev.alondra1.com/manifest.json")];
    /// <summary>Public keys of the dev discovery signer (alondra1-infra discovery/trust/dev.json). Empty until created.</summary>
    private const string TrustJson = """{"keys": []}""";
#else
    public const string Name = "prod";

    public const string DisplayName = "Alondra1";
    public const string DataFolder = "alondra1";
    public const string ApiOrigin = "https://api.alondra1.com";
    public static readonly Uri[] DiscoveryUrls =
        [new("https://config.alondra1.com/manifest.json"), new("https://config2.alondra1.com/manifest.json")];
    /// <summary>Public keys of the prod discovery signer (alondra1-infra discovery/trust/prod.json). Empty until created.</summary>
    private const string TrustJson = """{"keys": []}""";
#endif

    public static bool IsDev => Name == "dev";

    /// <summary>The bundled endpoint list used before any manifest was verified. It expires: an old build that never
    /// reached discovery stops authorising this route rather than trusting it forever.</summary>
    public static readonly DateTimeOffset BootstrapValidUntil = new(2027, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<DiscoveryEndpoint> BootstrapEndpoints { get; } =
        [new DiscoveryEndpoint("bootstrap", new Uri(ApiOrigin + "/"), "eu1", 0)];

    public static IReadOnlyList<TrustedKey> TrustedKeys { get; } = DiscoveryManifest.ParseTrust(TrustJson);

    /// <summary>
    /// Whether a manually configured cloud address may be used by this build: loopback for local development, otherwise
    /// only hosts of this build's environment (a dev build cannot reach prod and the reverse).
    /// </summary>
    public static bool AllowsCloudAddress(Uri address)
    {
        if (address.IsLoopback)
            return true;
        if (address.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = address.Host.ToLowerInvariant();
        var isDevHost = host == "dev.alondra1.com" || host.EndsWith(".dev.alondra1.com", StringComparison.Ordinal);
        var isProdHost = !isDevHost && (host == "alondra1.com" || host.EndsWith(".alondra1.com", StringComparison.Ordinal));
        return IsDev ? !isProdHost : !isDevHost;
    }
}
