using System.Text.Json.Nodes;
using Alondra1.Core.Cloud;
using Alondra1.Core.Ports;
using Alondra1.Core.Tasks;
using Alondra1.Persistence;
using Xunit.Abstractions;

namespace Alondra1.Tests;

/// <summary>
/// Cross-device check against a running backend (alondra1-backend cmd/devserver), driven step by step so another
/// device (the Android app) can act between steps. It uses an app data folder exactly like the Windows app
/// (todo.db and DPAPI secrets under ALONDRA1_E2E_HOME), so the real app can be started on the same folder with
/// <c>--home=</c> afterwards. Opt-in; see docs/CLOUD.md.
///
///   ALONDRA1_CLOUD_URL   http://127.0.0.1:8080
///   ALONDRA1_DEVCODES    path of the devserver's codes.json (sign-in codes)
///   ALONDRA1_E2E_HOME    app data folder of this simulated PC
///   ALONDRA1_E2E_ACTIONS steps separated by ';':
///       signin:&lt;email&gt;  add:&lt;title&gt;  rename:&lt;old&gt;=&gt;&lt;new&gt;  complete:&lt;title&gt;  delete:&lt;title&gt;
///       enable  sync  expect:&lt;title&gt;  expect-not:&lt;title&gt;  expect-issues:&lt;n&gt;  archive:&lt;subject&gt;  logout
/// </summary>
public sealed class CloudLiveTests(ITestOutputHelper output)
{
    [CloudLiveFact]
    public async Task Run_cloud_steps()
    {
        var home = Environment.GetEnvironmentVariable("ALONDRA1_E2E_HOME")!;
        Directory.CreateDirectory(home);
        var uri = new Alondra1.Core.Config.CloudSettings().EffectiveUri();
        await using var store = new SqliteTaskStore(Path.Combine(home, "todo.db"));
        await store.InitializeAsync();
        var board = new TaskBoard(store, new SystemClock());
        await board.LoadAsync();
        var api = new CloudApi(uri, new SecretStoreTokens(new DpapiSecretStore(Path.Combine(home, "secrets"))));
        var account = new CloudAccount(api, store);
        var notes = new Alondra1.Core.Notes.NotesBoard(store, new SystemClock());
        await notes.LoadAsync();
        var assets = new Alondra1.Core.Notes.NoteAssets(Path.Combine(home, "notes-assets"), store, () => DateTimeOffset.UtcNow);
        var engine = new CloudSyncEngine(board, store, api, new SystemClock(),
            () => store.BackupToAsync(Path.Combine(home, $"todo-before-cloud-{DateTime.Now:HHmmss}.db")), notes, assets);

        TodoTask Find(string title) => board.Tasks.SingleOrDefault(t => t.Title == title) ?? throw new Xunit.Sdk.XunitException($"No task \"{title}\". Tasks: {string.Join(" | ", board.Tasks.Select(t => t.Title))}");

        foreach (var step in Environment.GetEnvironmentVariable("ALONDRA1_E2E_ACTIONS")!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (verb, arg) = step.IndexOf(':') is var i and >= 0 ? (step[..i], step[(i + 1)..]) : (step, "");
            output.WriteLine("> " + step);
            switch (verb)
            {
                case "signin":
                    var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                    var challenge = await account.StartAsync(arg, CancellationToken.None);
                    await account.VerifyAsync(arg, challenge, await CodeAsync(arg, since), CancellationToken.None);
                    break;
                case "enable":
                    var space = await account.PersonalSpaceAsync(CancellationToken.None);
                    output.WriteLine("  plan: " + (await account.EntitlementsAsync(space.Id, CancellationToken.None)).Describe());
                    var enabled = await engine.EnableAsync(space.Id);
                    Assert.True(enabled.State == SyncState.Synced, $"{enabled.State}: {enabled.Message}");
                    break;
                case "sync":
                    var status = await engine.SyncNowAsync();
                    output.WriteLine($"  {status.State} {status.Message}");
                    Assert.True(status.State == SyncState.Synced, $"{status.State}: {status.Message}");
                    break;
                case "add":
                    await board.AddTaskAsync(board.DefaultList.Id, arg);
                    break;
                case "rename":
                    var parts = arg.Split("=>");
                    await board.UpdateTaskAsync(Find(parts[0]).Id, t => t.Title = parts[1]);
                    break;
                case "complete":
                    await board.SetCompletedAsync([Find(arg).Id], true);
                    break;
                case "delete":
                    await board.DeleteTasksAsync([Find(arg).Id]);
                    break;
                case "expect":
                    Find(arg);
                    break;
                case "expect-not":
                    Assert.DoesNotContain(board.Tasks, t => t.Title == arg);
                    break;
                case "expect-issues":
                    Assert.Equal(int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture), (await store.IssuesAsync(CancellationToken.None)).Count);
                    break;
                case "archive":
                    var tenant = (await account.PersonalSpaceAsync(CancellationToken.None)).Id;
                    var mime = $"From: Ana <ana@example.com>\r\nTo: e2e@example.com\r\nSubject: {arg}\r\nDate: Mon, 23 Nov 2026 09:00:00 +0000\r\nMessage-ID: <{Guid.NewGuid():N}@example.com>\r\nContent-Type: text/plain\r\n\r\nInvoice attached for the {arg} test.\r\n";
                    var client = new ArchiveClient(api);
                    var (archived, _) = await client.ArchiveAsync(tenant, System.Text.Encoding.UTF8.GetBytes(mime), "e2e@example.com", CancellationToken.None);
                    Assert.Equal(arg, archived.Subject);
                    var (found, _) = await client.SearchAsync(tenant, new ArchiveQuery(Text: "invoice"), CancellationToken.None);
                    Assert.Contains(found, m => m.Id == archived.Id);
                    break;
                case "rich-note":
                    // Every feature of the document format plus a picture: what the other device must show unchanged.
                    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
                    var picture = await assets.AddAsync(png);
                    var doc = (JsonObject)JsonNode.Parse(RichDocument.Replace("ASSET", picture.Sha256, StringComparison.Ordinal))!;
                    var folder = notes.Folders.FirstOrDefault(f => f.Name == "Trips") ?? await notes.AddFolderAsync("Trips");
                    var created = await notes.CreateAsync(folder.Id, arg, doc, text: "Itinerary Flights Book hotel", assets: [picture.Sha256]);
                    await notes.SetFavoriteAsync([created.Id], true);
                    output.WriteLine("  note " + created.Id);
                    break;
                case "expect-note":
                    var parts2 = arg.Split("::");
                    var match = notes.Notes.SingleOrDefault(n => n.Title == parts2[0]) ?? throw new Xunit.Sdk.XunitException("No note " + parts2[0] + ". Notes: " + string.Join(" | ", notes.Notes.Select(n => n.Title)));
                    var full = (await notes.GetAsync(match.Id))!;
                    var json = full.Doc!.ToJsonString();
                    foreach (var needle in new[] { "#2563EB", "tableHeader", "taskItem", "futureWidget", "\"assetId\"" }.Concat(parts2.Skip(1)))
                        Assert.True(json.Contains(needle, StringComparison.Ordinal), $"document lost {needle}: {json}");
                    foreach (var sha in full.Assets)
                        Assert.True(assets.HasFile(sha), "picture missing " + sha);
                    output.WriteLine($"  note ok: favorite={full.Favorite} folder={notes.FindFolder(full.FolderId ?? "")?.Name} pictures={full.Assets.Count}");
                    break;
                case "logout":
                    await account.LogoutAsync(CancellationToken.None);
                    break;
                default:
                    throw new ArgumentException("Unknown step " + step);
            }
        }

        foreach (var list in board.Lists)
            output.WriteLine($"  [{list.Name}{(list.IsDefault ? ", default" : "")}] " + string.Join(" | ", board.Tasks.Where(t => t.ListId == list.Id).Select(t => (t.Completed ? "✓ " : "") + t.Title)));
        foreach (var issue in await store.IssuesAsync(CancellationToken.None))
            output.WriteLine($"  issue: {issue.Title}: {issue.Message}");
    }

    private const string RichDocument = """
        {"schemaVersion":1,"type":"doc","content":[
          {"type":"heading","attrs":{"level":1},"content":[{"type":"text","text":"Itinerary"}]},
          {"type":"paragraph","attrs":{"textAlign":"center"},"content":[{"type":"text","text":"Flights","marks":[{"type":"bold"},{"type":"textStyle","attrs":{"color":"#2563EB","fontSize":"18pt"}}]},{"type":"text","text":" confirmed","marks":[{"type":"highlight","attrs":{"color":"#FDE047"}}]}]},
          {"type":"taskList","content":[{"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"Book hotel"}]}]},{"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"Passport"}]}]}]},
          {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableHeader","attrs":{"colspan":1,"rowspan":1,"backgroundColor":"#FEF3C7"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Day"}]}]},{"type":"tableHeader","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"City"}]}]}]},{"type":"tableRow","content":[{"type":"tableCell","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Mon"}]}]},{"type":"tableCell","attrs":{"colspan":1,"rowspan":1,"backgroundColor":"#DCFCE7"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Lisbon"}]}]}]}]},
          {"type":"image","attrs":{"assetId":"ASSET","width":160,"alignment":"center","caption":"Map"}},
          {"type":"futureWidget","attrs":{"x":1}},
          {"type":"paragraph","content":[{"type":"text","text":"End of plan"}]}
        ]}
        """;

    private static async Task<string> CodeAsync(string email, DateTimeOffset since)
    {
        var path = Environment.GetEnvironmentVariable("ALONDRA1_DEVCODES")!;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(path))?[email.ToLowerInvariant()] is JsonObject entry
                    && DateTimeOffset.Parse(entry["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) > since)
                    return entry["code"]!.GetValue<string>();
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                // The devserver is writing the file.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("No sign-in code for " + email);
    }
}

public sealed class CloudLiveFactAttribute : FactAttribute
{
    public CloudLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALONDRA1_E2E_ACTIONS")))
            Skip = "Set ALONDRA1_CLOUD_URL, ALONDRA1_DEVCODES, ALONDRA1_E2E_HOME and ALONDRA1_E2E_ACTIONS to run against a dev server.";
    }
}
