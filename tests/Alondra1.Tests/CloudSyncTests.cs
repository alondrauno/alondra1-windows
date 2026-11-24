using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Alondra1.Core.Cloud;
using Alondra1.Core.Ports;
using Alondra1.Core.Tasks;
using Alondra1.Persistence;

namespace Alondra1.Tests;

/// <summary>Session held in memory (the app uses DPAPI through <see cref="SecretStoreTokens"/>).</summary>
public sealed class MemoryTokens : ICloudTokens
{
    public string? Access { get; set; }
    public string? Refresh { get; set; }
    public Task<string?> LoadRefreshAsync(CancellationToken cancellationToken) => Task.FromResult(Refresh);
    public Task SaveRefreshAsync(string? refreshToken, CancellationToken cancellationToken) { Refresh = refreshToken; return Task.CompletedTask; }
}

/// <summary>A scripted server speaking the sync contract (alondra1-backend contract/openapi.yaml).</summary>
public sealed class ScriptedCloud : HttpMessageHandler
{
    public List<JsonObject> Pushes { get; } = [];
    public List<string> Paths { get; } = [];
    public string BootstrapItems { get; set; } = "[]";
    public string Changes { get; set; } = "[]";
    public bool Offline { get; set; }
    public Func<JsonObject, HttpResponseMessage>? PushResponder { get; set; }

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage AppliedAll(JsonObject request)
    {
        var results = new JsonArray();
        foreach (var m in request["mutations"]!.AsArray().OfType<JsonObject>())
        {
            var type = m["resource_type"]!.GetValue<string>();
            var result = new JsonObject { ["mutation_id"] = m["mutation_id"]!.GetValue<string>(), ["status"] = "applied" };
            var action = m["action"]!.GetValue<string>();
            if (action != "delete")
            {
                result["record"] = new JsonObject
                {
                    ["id"] = m["resource_id"]!.GetValue<string>(),
                    ["version"] = action == "create" ? 1 : m["base_version"]!.GetValue<long>() + 1,
                    ["schema_version"] = 1, ["updated_at"] = "2026-11-23T10:00:00Z",
                    [type] = (action == "create" ? m["resource"] : m["changes"])!.DeepClone(),
                };
            }

            results.Add(result);
        }

        return Json(new JsonObject { ["results"] = results }.ToJsonString());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
            throw new HttpRequestException("offline");
        var path = request.RequestUri!.PathAndQuery;
        Paths.Add(path);
        if (path.StartsWith("/v1/sync/bootstrap", StringComparison.Ordinal))
            return Json($$"""{"items":{{BootstrapItems}},"cursor":"c1"}""");
        if (path.StartsWith("/v1/sync/push", StringComparison.Ordinal))
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Pushes.Add(body);
            return (PushResponder ?? AppliedAll)(body);
        }

        if (path.StartsWith("/v1/sync/changes", StringComparison.Ordinal))
        {
            var changes = Changes;
            Changes = "[]";
            return Json($$"""{"changes":{{changes}},"cursor":"c2","has_more":false}""");
        }

        return Json("""{"error":{"code":"NOT_FOUND","message":"no"}}""", HttpStatusCode.NotFound);
    }
}

public sealed class CloudSyncTests : IAsyncDisposable
{
    private const string Tenant = "01a209c9-4fe9-7666-a5f5-145014a54198";
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private readonly ScriptedCloud _cloud = new();
    private readonly MemoryTokens _tokens = new() { Access = "token", Refresh = "refresh" };
    private SqliteTaskStore _store = null!;
    private TaskBoard _board = null!;
    private CloudSyncEngine _engine = null!;
    private string _path = null!;
    private int _backups;

    private async Task<CloudSyncTests> Start_()
    {
        _path = Path.Combine(Directory.CreateTempSubdirectory("alondra1-cloud-").FullName, "todo.db");
        _store = new SqliteTaskStore(_path);
        await _store.InitializeAsync();
        _board = new TaskBoard(_store, new FixedClock(Start), TimeZoneInfo.Utc);
        await _board.LoadAsync();
        var api = new CloudApi(new Uri("http://127.0.0.1:9/"), _tokens, _cloud);
        _engine = new CloudSyncEngine(_board, _store, api, new FixedClock(Start), () => { _backups++; return Task.CompletedTask; });
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
            await _store.DisposeAsync();
    }

    private static string ListRecord(string id, long version, bool isDefault, string name = "Tasks") =>
        Feed("list", id, version, new JsonObject
        {
            ["id"] = id, ["name"] = name, ["isDefault"] = isDefault, ["position"] = 0, ["createdUtc"] = "2026-11-01T00:00:00Z",
        });

    private static string TaskRecord(string id, long version, string title, string listId) =>
        Feed("task", id, version, new JsonObject
        {
            ["id"] = id, ["listId"] = listId, ["title"] = title, ["notes"] = "", ["completed"] = false, ["important"] = false, ["position"] = 1,
            ["createdUtc"] = "2026-11-01T00:00:00Z", ["modifiedUtc"] = "2026-11-01T00:00:00Z",
        });

    private static string Feed(string type, string id, long version, JsonObject resource) => new JsonObject
    {
        ["resource_type"] = type, ["action"] = "upsert", ["resource_id"] = id,
        ["record"] = new JsonObject { ["id"] = id, ["version"] = version, ["schema_version"] = 1, ["updated_at"] = "2026-11-20T00:00:00Z", [type] = resource },
    }.ToJsonString();

    private IEnumerable<JsonObject> Sent => _cloud.Pushes.SelectMany(p => p["mutations"]!.AsArray().OfType<JsonObject>());

    [Fact]
    public async Task Nothing_is_queued_or_sent_before_sync_is_turned_on()
    {
        await Start_();
        await _board.AddTaskAsync(_board.DefaultList.Id, "Local only");
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));
        Assert.Equal(SyncState.ReadyOff, (await _engine.SyncNowAsync()).State);
        Assert.Empty(_cloud.Paths);
    }

    [Fact]
    public async Task Enabling_backs_up_merges_both_sides_and_adopts_the_cloud_default_list()
    {
        await Start_();
        var local = await _board.AddTaskAsync(_board.DefaultList.Id, "From Windows");
        var localDefault = _board.DefaultList.Id;
        _cloud.BootstrapItems = $"[{ListRecord("phone-default", 1, true)},{TaskRecord("phone-task", 2, "From the phone", "phone-default")}]";

        var status = await _engine.EnableAsync(Tenant);

        Assert.Equal(SyncState.Synced, status.State);
        Assert.Equal(1, _backups);
        Assert.Equal("From the phone", _board.FindTask("phone-task")!.Title);
        Assert.Equal("phone-default", _board.FindTask(local.Id)!.ListId);
        Assert.Single(_board.Lists, l => l.IsDefault);
        Assert.Null(_board.FindList(localDefault));
        var created = Sent.Single(m => m["resource_id"]!.GetValue<string>() == local.Id);
        Assert.Equal("create", created["action"]!.GetValue<string>());
        Assert.Equal("phone-default", created["resource"]!["listId"]!.GetValue<string>());
        Assert.DoesNotContain(Sent, m => m["resource_type"]!.GetValue<string>() == "list");
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Edits_are_sent_as_merge_patches_from_the_last_server_copy()
    {
        await Start_();
        _cloud.BootstrapItems = $"[{ListRecord("d", 1, true)},{TaskRecord("t1", 3, "Shared", "d")}]";
        await _engine.EnableAsync(Tenant);
        await _board.UpdateTaskAsync("t1", t => t.Title = "Renamed");
        await _engine.SyncNowAsync();
        var patch = Sent.Last();
        Assert.Equal("patch", patch["action"]!.GetValue<string>());
        Assert.Equal(3, patch["base_version"]!.GetValue<long>());
        Assert.Equal("Renamed", patch["changes"]!["title"]!.GetValue<string>());

        var diff = CloudSyncEngine.Diff("""{"a":1,"gone":2}""", new JsonObject { ["a"] = 1 });
        Assert.True(diff.ContainsKey("gone"));
        Assert.Null(diff["gone"]);
    }

    [Fact]
    public async Task A_conflict_keeps_both_versions_and_reports_an_issue()
    {
        await Start_();
        _cloud.BootstrapItems = $"[{ListRecord("d", 1, true)},{TaskRecord("t1", 3, "Shared task", "d")}]";
        await _engine.EnableAsync(Tenant);
        await _board.UpdateTaskAsync("t1", t => t.Title = "Edited on Windows");
        _cloud.PushResponder = request =>
        {
            var m = request["mutations"]!.AsArray()[0]!;
            var current = JsonNode.Parse(TaskRecord("t1", 4, "Edited on the phone", "d"))!["record"]!.ToJsonString();
            _cloud.PushResponder = null;
            return ScriptedCloud.Json($$"""{"results":[{"mutation_id":"{{m["mutation_id"]}}","status":"conflict","code":"VERSION_CONFLICT","current":{{current}}}]}""");
        };

        await _engine.SyncNowAsync();

        Assert.Equal("Edited on the phone", _board.FindTask("t1")!.Title);
        var copy = Assert.Single(_board.Tasks, t => t.Title == "Edited on Windows (conflict copy)");
        Assert.Single(await _store.IssuesAsync(CancellationToken.None));
        Assert.Contains(Sent, m => m["resource_id"]!.GetValue<string>() == copy.Id && m["action"]!.GetValue<string>() == "create");
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_pull_never_overwrites_unsent_edits()
    {
        await Start_();
        _cloud.BootstrapItems = $"[{ListRecord("d", 1, true)},{TaskRecord("t1", 1, "Original", "d")}]";
        await _engine.EnableAsync(Tenant);
        _cloud.PushResponder = _ => ScriptedCloud.Json("""{"error":{"code":"INTERNAL","message":"down"}}""", HttpStatusCode.InternalServerError);
        await _board.UpdateTaskAsync("t1", t => t.Title = "Unsent edit");
        _cloud.Changes = $"[{TaskRecord("t1", 2, "Remote edit", "d")}]";

        Assert.Equal(SyncState.ErrorRetryable, (await _engine.SyncNowAsync()).State);
        Assert.Equal("Unsent edit", _board.FindTask("t1")!.Title);
        Assert.Equal(1, await _store.PendingCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Changes_from_other_devices_arrive_and_survive_a_restart()
    {
        await Start_();
        _cloud.BootstrapItems = $"[{ListRecord("d", 1, true)}]";
        await _engine.EnableAsync(Tenant);
        _cloud.Changes = $"[{TaskRecord("t9", 1, "Added on the phone", "d")}]";
        await _engine.SyncNowAsync();
        Assert.Equal("Added on the phone", _board.FindTask("t9")!.Title);
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));

        await _store.DisposeAsync();
        _store = new SqliteTaskStore(_path);
        await _store.InitializeAsync();
        Assert.True(_store.Recording);
        Assert.Equal("c2", await _store.GetMetaAsync("cursor", CancellationToken.None));
        var board = new TaskBoard(_store, new FixedClock(Start), TimeZoneInfo.Utc);
        await board.LoadAsync();
        Assert.Equal("Added on the phone", board.FindTask("t9")!.Title);
    }

    [Fact]
    public async Task Plan_access_and_network_errors_never_touch_local_data()
    {
        await Start_();
        _cloud.BootstrapItems = $"[{ListRecord("d", 1, true)}]";
        await _engine.EnableAsync(Tenant);
        await _board.AddTaskAsync("d", "Made after the plan ended");

        _cloud.PushResponder = _ => ScriptedCloud.Json("""{"error":{"code":"ENTITLEMENT_REQUIRED","message":"paid","entitlement_state":"read_only"}}""", HttpStatusCode.Forbidden);
        Assert.Equal(SyncState.ReadOnlyPlan, (await _engine.SyncNowAsync()).State);
        Assert.Equal(1, await _store.PendingCountAsync(CancellationToken.None));

        _cloud.Offline = true;
        Assert.Equal(SyncState.Offline, (await _engine.SyncNowAsync()).State);
        _cloud.Offline = false;

        _cloud.PushResponder = _ => ScriptedCloud.Json("""{"error":{"code":"TENANT_NOT_FOUND","message":"gone"}}""", HttpStatusCode.NotFound);
        Assert.Equal(SyncState.ErrorPermanent, (await _engine.SyncNowAsync()).State);
        Assert.False(await _engine.EnabledAsync());
        Assert.Contains(_board.Tasks, t => t.Title == "Made after the plan ended");
    }

    [Fact]
    public async Task Deleting_a_synced_task_sends_a_delete_and_a_never_synced_one_sends_nothing()
    {
        await Start_();
        _cloud.BootstrapItems = $"[{ListRecord("d", 1, true)},{TaskRecord("t1", 5, "Synced", "d")}]";
        await _engine.EnableAsync(Tenant);
        var draft = await _board.AddTaskAsync("d", "Draft");
        await _board.DeleteTasksAsync(["t1", draft.Id]);
        _cloud.Pushes.Clear();
        await _engine.SyncNowAsync();
        var delete = Assert.Single(Sent);
        Assert.Equal("delete", delete["action"]!.GetValue<string>());
        Assert.Equal("t1", delete["resource_id"]!.GetValue<string>());
        Assert.Equal(5, delete["base_version"]!.GetValue<long>());
    }

    [Fact]
    public void Cloud_address_must_be_https_except_on_this_pc()
    {
        Assert.Throws<ArgumentException>(() => new CloudApi(new Uri("http://api.example.com/"), new MemoryTokens()));
        _ = new CloudApi(new Uri("https://api.example.com/"), new MemoryTokens());
        _ = new CloudApi(new Uri("http://127.0.0.1:8080/"), new MemoryTokens());
    }

    [Fact]
    public void Entitlements_are_read_as_the_server_reports_them()
    {
        var e = CloudEntitlements.From((JsonObject)JsonNode.Parse("""
            {"plan":"personal","plan_name":"Personal","entitlement_state":"read_only","features":{"sync":true,"sync_write":false,"mail_archive":true,"archive_search":true},
             "entitlements":["billing.manage"],"usage":{"archive_messages":3,"archive_bytes":2048}}
            """)!);
        Assert.Equal("read_only", e.State);
        Assert.True(e.Sync);
        Assert.False(e.SyncWrite);
        Assert.True(e.ManageBilling);
        Assert.Equal(3, e.ArchiveMessages);
        Assert.Contains("read-only", e.Describe());
    }
}
