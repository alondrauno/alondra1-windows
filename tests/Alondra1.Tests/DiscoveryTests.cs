using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Alondra1.Core;
using Alondra1.Core.Cloud.Discovery;
using Xunit;

namespace Alondra1.Tests;

/// <summary>
/// Signed cloud discovery (completion spec v1.1 §6): the shared fixture cases (contract/fixtures/discovery-v1, the same
/// cases the Go reference, Android and Web run), Ed25519, the client's source order and cache, and safe failover.
/// </summary>
public class DiscoveryTests
{
    private static string FixtureDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contract", "fixtures", "discovery-v1")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("contract/ not found"), "contract", "fixtures", "discovery-v1");
        }
    }

    private static byte[] Fixture(string relative) => File.ReadAllBytes(Path.Combine(FixtureDir, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static IReadOnlyList<TrustedKey> FixtureTrust() => DiscoveryManifest.ParseTrust(Encoding.UTF8.GetString(Fixture("trust.json")));

    private static readonly DateTimeOffset FixtureNow = DateTimeOffset.Parse("2026-12-01T00:00:00Z");

    [Fact]
    public void Ed25519_RFC8032_vectors()
    {
        (string pub, string msg, string sig)[] vectors =
        [
            ("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
             "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
            ("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
             "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
            ("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
             "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
        ];
        foreach (var (pub, msg, sig) in vectors)
        {
            var publicKey = Convert.FromHexString(pub);
            var message = Convert.FromHexString(msg);
            var signature = Convert.FromHexString(sig);
            Assert.True(Ed25519.Verify(publicKey, message, signature));
            var tampered = (byte[])signature.Clone();
            tampered[10] ^= 1;
            Assert.False(Ed25519.Verify(publicKey, message, tampered));
            Assert.False(Ed25519.Verify(publicKey, [.. message, 0], signature));
        }
    }

    [Fact]
    public void Every_shared_fixture_case_gives_the_same_result()
    {
        using var doc = JsonDocument.Parse(Fixture("cases.json"));
        var trust = FixtureTrust();
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 25, "the full case list");
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString();
            var now = DateTimeOffset.Parse(c.GetProperty("now").GetString()!);
            (long, byte[])? cached = null;
            if (c.GetProperty("cached").ValueKind == JsonValueKind.String)
            {
                var old = Fixture(c.GetProperty("cached").GetString()!);
                cached = (DiscoveryManifest.PeekVersion(old), old);
            }
            var expect = c.GetProperty("expect");
            try
            {
                var result = DiscoveryManifest.Verify(Fixture(c.GetProperty("manifest").GetString()!), Fixture(c.GetProperty("signature").GetString()!),
                    c.GetProperty("environment").GetString()!, trust, now, cached);
                Assert.True(expect.GetProperty("ok").GetBoolean(), $"{name}: expected {expect}");
                var ids = expect.GetProperty("endpoint_ids").EnumerateArray().Select(e => e.GetString()!).ToList();
                Assert.Equal(ids, result.Endpoints.Select(e => e.Id).ToList());
            }
            catch (DiscoveryException e)
            {
                Assert.False(expect.GetProperty("ok").GetBoolean(), $"{name}: unexpected {e.Message}");
                Assert.Equal(expect.GetProperty("error").GetString(), e.Code);
            }
        }
    }

    [Theory]
    [InlineData("api.alondra1.com", true, false)]
    [InlineData("eu1.api.alondra1.com", true, false)]
    [InlineData("api.alondra1.dev", false, true)]
    [InlineData("eu1.api.alondra1.dev", false, true)]
    [InlineData("a.b.api.alondra1.com", false, false)]
    [InlineData("api.alondra1.com.evil.test", false, false)]
    [InlineData("xapi.alondra1.com", false, false)]
    public void Host_rules_separate_environments(string host, bool prod, bool dev)
    {
        Assert.Equal(prod, DiscoveryManifest.HostAllowed("prod", host));
        Assert.Equal(dev, DiscoveryManifest.HostAllowed("dev", host));
    }

    [Fact]
    public void A_prod_build_refuses_dev_hosts_and_plain_http()
    {
        Assert.False(BuildEnvironment.IsDev); // the test project builds prod
        Assert.True(BuildEnvironment.AllowsCloudAddress(new Uri("https://api.alondra1.com/")));
        Assert.True(BuildEnvironment.AllowsCloudAddress(new Uri("http://127.0.0.1:8080/")));
        Assert.False(BuildEnvironment.AllowsCloudAddress(new Uri("https://api.alondra1.dev/")));
        Assert.False(BuildEnvironment.AllowsCloudAddress(new Uri("http://api.alondra1.com/")));
        Assert.Equal("alondra1", BuildEnvironment.DataFolder);
        Assert.DoesNotContain(".dev.", BuildEnvironment.ApiOrigin);
        Assert.All(BuildEnvironment.DiscoveryUrls, u => Assert.DoesNotContain(".dev.", u.Host));
    }

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return respond(request);
        }
    }

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static DiscoveryClient Client(FakeServer server, string cache, DateTimeOffset? bootstrapUntil = null) =>
        new(server, "prod", FixtureTrust(),
            [new Uri("https://config.alondra1.com/manifest.json"), new Uri("https://config2.alondra1.com/manifest.json")],
            cache, [new DiscoveryEndpoint("bootstrap", new Uri("https://api.alondra1.com/"), "eu1", 0)],
            bootstrapUntil ?? FixtureNow.AddYears(1), () => FixtureNow);

    [Fact]
    public async Task Client_uses_the_mirror_when_the_primary_fails_then_caches()
    {
        var cache = Path.Combine(Path.GetTempPath(), "alondra1-discovery-" + Guid.NewGuid().ToString("N"), "cache.json");
        var server = new FakeServer(r => r.RequestUri!.Host switch
        {
            "config.alondra1.com" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => Bytes(Fixture(r.RequestUri.AbsolutePath.EndsWith(".sig") ? "valid-prod/manifest.json.sig" : "valid-prod/manifest.json")),
        });
        var problems = new List<string>();
        var set = await Client(server, cache).ResolveAsync(problems, CancellationToken.None);
        Assert.NotNull(set);
        Assert.Equal(EndpointSource.Mirror, set!.Source);
        Assert.Equal(["eu1", "api"], set.Endpoints.Select(e => e.Id));
        Assert.Contains(problems, p => p.StartsWith("Primary"));

        // Offline afterwards: the verified cache, not the bundled list.
        var offline = Client(new FakeServer(_ => throw new HttpRequestException("down")), cache).Offline();
        Assert.Equal(EndpointSource.Cache, offline!.Source);
    }

    [Fact]
    public async Task Client_refuses_forged_and_rolled_back_manifests()
    {
        var cache = Path.Combine(Path.GetTempPath(), "alondra1-discovery-" + Guid.NewGuid().ToString("N"), "cache.json");
        // Cache version 5 first (a valid signed manifest).
        var good = new FakeServer(r => Bytes(Fixture(r.RequestUri!.AbsolutePath.EndsWith(".sig") ? "rollback/manifest.json.sig" : "rollback/manifest.json")));
        Assert.NotNull(await Client(good, cache).ResolveAsync([], CancellationToken.None));
        // Forged on the primary, the older version 3 (rollback) on the mirror: neither replaces the cache.
        var bad = new FakeServer(r => r.RequestUri!.Host == "config.alondra1.com"
            ? Bytes(Fixture(r.RequestUri.AbsolutePath.EndsWith(".sig") ? "forged-content/manifest.json.sig" : "forged-content/manifest.json"))
            : Bytes(Fixture(r.RequestUri.AbsolutePath.EndsWith(".sig") ? "valid-prod/manifest.json.sig" : "valid-prod/manifest.json")));
        var problems = new List<string>();
        var set = await Client(bad, cache).ResolveAsync(problems, CancellationToken.None);
        Assert.Equal(EndpointSource.Cache, set!.Source);
        Assert.Equal(5, set.Version);
        Assert.Contains("Primary: BAD_SIGNATURE", problems);
        Assert.Contains("Mirror: ROLLBACK", problems);
    }

    [Fact]
    public void Bootstrap_list_expires()
    {
        var cache = Path.Combine(Path.GetTempPath(), "alondra1-discovery-" + Guid.NewGuid().ToString("N"), "cache.json");
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal(EndpointSource.Bootstrap, Client(server, cache).Offline()!.Source);
        Assert.Null(Client(server, cache, FixtureNow.AddDays(-1)).Offline());
    }

    private static (HttpClient, FakeServer, EndpointSelector) Failover(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var selector = new EndpointSelector([
            new DiscoveryEndpoint("eu1", new Uri("https://eu1.api.alondra1.com/"), "eu1", 10),
            new DiscoveryEndpoint("api", new Uri("https://api.alondra1.com/"), "eu1", 20)]);
        var server = new FakeServer(respond);
        return (new HttpClient(new EndpointFailoverHandler(selector, server)) { BaseAddress = new Uri("https://eu1.api.alondra1.com/") }, server, selector);
    }

    [Fact]
    public async Task Failover_on_transport_errors_and_gateway_answers_with_the_same_body()
    {
        var (http, server, selector) = Failover(r => r.RequestUri!.Host == "eu1.api.alondra1.com"
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.OK));
        var response = await http.PostAsync("/v1/sync/push", new StringContent("""{"mutations":[{"mutation_id":"m1"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["eu1.api.alondra1.com", "api.alondra1.com"], server.Requests.Select(r => r.RequestUri!.Host));
        Assert.Equal(server.Bodies[0], server.Bodies[1]);
        Assert.Equal("api", selector.Candidates()[0].Id); // eu1 is paused now
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task No_failover_for_answers_that_another_endpoint_would_give_too(HttpStatusCode status)
    {
        var (http, server, _) = Failover(_ => new HttpResponseMessage(status));
        var response = await http.GetAsync("/v1/me");
        Assert.Equal(status, response.StatusCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Retry_After_pauses_the_endpoint()
    {
        var (http, server, selector) = Failover(r =>
        {
            if (r.RequestUri!.Host != "eu1.api.alondra1.com")
                return new HttpResponseMessage(HttpStatusCode.OK);
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return busy;
        });
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/v1/sync/changes")).StatusCode);
        Assert.Equal(2, server.Requests.Count);
        await http.GetAsync("/v1/sync/changes");
        Assert.Equal("api.alondra1.com", server.Requests[^1].RequestUri!.Host); // eu1 skipped while paused
    }

    [Fact]
    public async Task A_refresh_is_never_replayed_on_another_endpoint_after_a_transport_failure()
    {
        var (http, server, _) = Failover(_ => throw new HttpRequestException("reset after send"));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync("/v1/auth/refresh", new StringContent("{}")));
        Assert.Single(server.Requests);
    }
}
