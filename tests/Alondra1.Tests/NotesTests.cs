using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Alondra1.Core.Cloud;
using Alondra1.Core.Notes;
using Alondra1.Core.Tasks;
using Alondra1.Persistence;
using Microsoft.Data.Sqlite;

namespace Alondra1.Tests;

public sealed class NotesTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private readonly FixedClock _clock = new(Start);
    private string _dir = null!;
    private SqliteTaskStore _store = null!;
    private NotesBoard _notes = null!;
    private NoteAssets _assets = null!;

    private async Task OpenAsync(string? dir = null)
    {
        _dir = dir ?? Directory.CreateTempSubdirectory("alondra1-notes-").FullName;
        _store = new SqliteTaskStore(Path.Combine(_dir, "todo.db"));
        await _store.InitializeAsync();
        _notes = new NotesBoard(_store, _clock);
        await _notes.LoadAsync();
        _assets = new NoteAssets(Path.Combine(_dir, "notes-assets"), _store, () => _clock.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
            await _store.DisposeAsync();
    }

    private static JsonObject Doc(string text) => (JsonObject)JsonNode.Parse("""
        {"schemaVersion":1,"type":"doc","content":[
          {"type":"heading","attrs":{"level":1},"content":[{"type":"text","text":"TEXT"}]},
          {"type":"paragraph","attrs":{"textAlign":"center","indent":1},"content":[{"type":"text","text":"Colored","marks":[{"type":"textStyle","attrs":{"color":"#2563EB","fontSize":"18pt"}},{"type":"bold"}]}]},
          {"type":"taskList","content":[{"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"Done"}]}]}]},
          {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableHeader","attrs":{"colspan":1,"rowspan":1,"backgroundColor":"#FEF3C7"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Cell"}]}]}]}]},
          {"type":"futureNode","attrs":{"x":1}}
        ]}
        """.Replace("TEXT", text, StringComparison.Ordinal))!;

    private static string Feed(string type, string id, long version, JsonObject obj) => new JsonObject
    {
        ["resource_type"] = type, ["action"] = "upsert", ["resource_id"] = id,
        ["record"] = new JsonObject { ["id"] = id, ["version"] = version, ["schema_version"] = 1, ["updated_at"] = "2026-11-24T08:00:00Z", [type] = obj },
    }.ToJsonString();

    private static byte[] Png(byte red = 200)
    {
        // A valid 1x1 PNG with a chosen red value (different bytes = different picture).
        var bitmap = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        bitmap[^20] = red;
        return bitmap;
    }

    [Fact]
    public async Task Notes_keep_document_title_and_state_across_a_restart()
    {
        await OpenAsync();
        var folder = await _notes.AddFolderAsync("Work");
        var note = await _notes.CreateAsync(folder.Id, "Plan");
        Assert.True(await _notes.SaveContentAsync(note.Id, "Plan", Doc("Project ideas"), "Project ideas\nColored\n[x] Done", ["a".PadLeft(64, 'a')]));
        Assert.False(await _notes.SaveContentAsync(note.Id, "Plan", Doc("Project ideas"), "same", []), "unchanged content is not written");
        await _notes.SetFavoriteAsync([note.Id], true);
        await _notes.AddLinkAsync(note.Id, new NoteLink(NoteLink.Event, "evt-1", "Standup"));

        await _store.DisposeAsync();
        await OpenAsync(_dir);
        var again = await _notes.GetAsync(note.Id);
        Assert.NotNull(again);
        Assert.Equal(Doc("Project ideas").ToJsonString(), again!.Doc!.ToJsonString());
        Assert.True(again.Favorite);
        Assert.Equal(folder.Id, again.FolderId);
        Assert.Equal("Standup", again.Links.Single().Label);
        Assert.Single(_notes.Query(NoteView.Favorites));
        Assert.Single(_notes.Query(NoteView.Folder, folder.Id));
        Assert.Equal("Work", _notes.Folders.Single().Name);
    }

    [Fact]
    public async Task Views_trash_restore_archive_and_retention()
    {
        await OpenAsync();
        var a = await _notes.CreateAsync(title: "A");
        var b = await _notes.CreateAsync(title: "B");
        var c = await _notes.CreateAsync(title: "C");
        await _notes.ArchiveAsync([b.Id], true);
        await _notes.TrashAsync([c.Id]);
        Assert.Equal(["A"], _notes.Query(NoteView.All).Select(n => n.Title));
        Assert.Equal(["B"], _notes.Query(NoteView.Archive).Select(n => n.Title));
        Assert.Equal(["C"], _notes.Query(NoteView.Trash).Select(n => n.Title));
        await _notes.RestoreAsync([c.Id]);
        Assert.Equal(2, _notes.Count(NoteView.All));
        await _notes.TrashAsync([c.Id]);
        _clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(0, await _notes.PurgeTrashAsync(NotesBoard.DefaultTrashRetention));
        _clock.Advance(TimeSpan.FromDays(21));
        Assert.Equal(1, await _notes.PurgeTrashAsync(NotesBoard.DefaultTrashRetention));
        Assert.Null(await _notes.GetAsync(c.Id));
        Assert.NotNull(await _notes.GetAsync(a.Id));
    }

    [Fact]
    public async Task Sorting_pinning_duplicate_and_move()
    {
        await OpenAsync();
        var first = await _notes.CreateAsync(title: "banana");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await _notes.CreateAsync(title: "apple");
        Assert.Equal(["apple", "banana"], _notes.Query(NoteView.All).Select(n => n.Title));
        Assert.Equal(["banana", "apple"], _notes.Query(NoteView.All, sort: NoteSort.Created, descending: false).Select(n => n.Title));
        Assert.Equal(["apple", "banana"], _notes.Query(NoteView.All, sort: NoteSort.Title, descending: false).Select(n => n.Title));
        await _notes.SetPinnedAsync([first.Id], true);
        Assert.Equal("banana", _notes.Query(NoteView.All).First().Title);
        var copy = await _notes.DuplicateAsync(second.Id);
        Assert.Equal("apple (copy)", copy!.Title);
        var folder = await _notes.AddFolderAsync("Fruit");
        await _notes.MoveAsync([first.Id, second.Id], folder.Id);
        Assert.Equal(2, _notes.Count(NoteView.Folder, folder.Id));
    }

    [Fact]
    public async Task Deleting_a_folder_keeps_its_notes_and_subfolders()
    {
        await OpenAsync();
        var parent = await _notes.AddFolderAsync("Parent");
        var child = await _notes.AddFolderAsync("Child", parent.Id);
        var note = await _notes.CreateAsync(parent.Id, "Inside");
        await Assert.ThrowsAsync<NoteStorageException>(() => _notes.MoveFolderAsync(parent.Id, child.Id));
        await _notes.DeleteFolderAsync(parent.Id);
        Assert.Null(_notes.FindFolder(parent.Id));
        Assert.Null(_notes.FindFolder(child.Id)!.ParentId);
        Assert.Null((await _notes.GetAsync(note.Id))!.FolderId);
        Assert.Equal(1, _notes.Count(NoteView.All));
    }

    [Fact]
    public async Task Full_text_search_matches_titles_and_bodies_and_treats_operators_literally()
    {
        await OpenAsync();
        var rent = await _notes.CreateAsync(title: "Rent", text: "Pay the landlord on Friday");
        await _notes.CreateAsync(title: "Groceries", text: "milk, eggs");
        var accented = await _notes.CreateAsync(title: "Café ideas", text: "");
        Assert.Equal([rent.Id], await _notes.SearchAsync("landl"));
        Assert.Equal([rent.Id], await _notes.SearchAsync("rent friday"));
        Assert.Equal([accented.Id], await _notes.SearchAsync("cafe"));
        Assert.Empty(await _notes.SearchAsync("NEAR(x y) \"*"));
        await _notes.DeleteForeverAsync([rent.Id]);
        Assert.Empty(await _notes.SearchAsync("landlord"));
    }

    [Fact]
    public async Task Search_over_ten_thousand_notes_is_fast()
    {
        await OpenAsync();
        var changes = new NoteChangeSet();
        for (var i = 0; i < 10_000; i++)
            changes.UpsertNotes.Add(new Note { Id = $"n{i:D5}", Title = $"Note {i}", Body = i % 100 == 0 ? $"quarterly budget review {i}" : $"ordinary text number {i}", CreatedUtc = Start, ModifiedUtc = Start });
        await _store.ApplyNotesAsync(changes, CancellationToken.None);
        await _notes.LoadAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = await _notes.SearchAsync("budget review");
        var listed = _notes.Query(NoteView.All, onlyIds: found);
        watch.Stop();
        Assert.Equal(100, listed.Count);
        Assert.True(watch.ElapsedMilliseconds < 500, $"search took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Pictures_are_checked_from_their_bytes_and_stored_once()
    {
        await OpenAsync();
        var png = Png();
        var asset = await _assets.AddAsync(png);
        Assert.Equal("image/png", asset.MimeType);
        Assert.Equal((1, 1), (asset.Width!.Value, asset.Height!.Value));
        Assert.Equal(NoteAssets.Hash(png), asset.Sha256);
        Assert.Equal(asset, await _assets.AddAsync(png));
        Assert.Single(Directory.GetFiles(_assets.Directory, "*", SearchOption.AllDirectories));
        var html = "<script>alert(1)</script>"u8.ToArray();
        var refused = await Assert.ThrowsAsync<NoteAssetException>(() => _assets.AddAsync(html));
        Assert.Contains("PNG, JPEG, GIF and WebP", refused.Message);
        var small = new NoteAssets(_assets.Directory, _store, () => Start) { MaxBytes = 10 };
        await Assert.ThrowsAsync<NoteAssetException>(() => small.AddAsync(Png(1)));
        var perNote = new NoteAssets(_assets.Directory, _store, () => Start) { MaxBytesPerNote = png.Length + 10 };
        Assert.Equal(asset, await perNote.AddAsync(png, [asset.Sha256]));
        var tooMuch = await Assert.ThrowsAsync<NoteAssetException>(() => perNote.AddAsync(Png(2), [asset.Sha256]));
        Assert.Contains("one note", tooMuch.Message);
        Assert.Equal("image/png", (await perNote.AddAsync(Png(2), [])).MimeType);
        Assert.False(await _assets.AddDownloadedAsync(new string('0', 64), png), "bytes must match the hash");
        Assert.Equal((640, 480), NoteAssets.Dimensions("image/gif", [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x80, 0x02, 0xE0, 0x01]) is var (w, h) ? (w!.Value, h!.Value) : default);
    }

    [Fact]
    public async Task Migration_from_schema_3_keeps_tasks_and_adds_notes()
    {
        var dir = Directory.CreateTempSubdirectory("alondra1-notes-").FullName;
        var path = Path.Combine(dir, "todo.db");
        await using (var old = new SqliteTaskStore(path))
        {
            await old.InitializeAsync(targetVersion: 3);
            var board = new TaskBoard(old, _clock, TimeZoneInfo.Utc);
            await board.LoadAsync();
            await board.AddTaskAsync(board.DefaultList.Id, "Survives");
        }

        await OpenAsync(dir);
        Assert.True(File.Exists(path + ".pre-v4.bak"));
        var tasks = new TaskBoard(_store, _clock, TimeZoneInfo.Utc);
        await tasks.LoadAsync();
        Assert.Contains(tasks.Tasks, t => t.Title == "Survives");
        Assert.NotNull(await _notes.CreateAsync(title: "First note"));
    }

    [Fact]
    public async Task Changes_are_queued_for_sync_only_while_recording()
    {
        await OpenAsync();
        await _notes.CreateAsync(title: "Local only");
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));
        await _store.SetRecordingAsync(true, CancellationToken.None);
        var folder = await _notes.AddFolderAsync("Work");
        var note = await _notes.CreateAsync(folder.Id, "Synced");
        await _notes.DeleteForeverAsync([note.Id]);
        var outbox = await _store.OutboxAsync(CancellationToken.None);
        Assert.Contains(outbox, i => i.EntityType == CloudKinds.NoteFolder && !i.Deleted);
        Assert.Contains(outbox, i => i.EntityType == CloudKinds.Note && i.EntityId == note.Id && i.Deleted);
    }

    // ---------------------------------------------------------------- Cloud Sync of notes

    private (CloudSyncEngine Engine, ScriptedCloud Cloud, Dictionary<string, byte[]> Uploaded) Engine(TaskBoard tasks)
    {
        var cloud = new ScriptedCloud();
        var uploaded = new Dictionary<string, byte[]>();
        var handler = new AssetServer(cloud, uploaded);
        var api = new CloudApi(new Uri("http://127.0.0.1:9/"), new MemoryTokens { Access = "t", Refresh = "r" }, handler);
        return (new CloudSyncEngine(tasks, _store, api, _clock, null, _notes, _assets), cloud, uploaded);
    }

    /// <summary>Adds the picture endpoints to the scripted sync server.</summary>
    private sealed class AssetServer(ScriptedCloud sync, Dictionary<string, byte[]> uploaded) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _sync = new(sync);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/v1/note-assets/", StringComparison.Ordinal))
                return await _sync.SendAsync(request, cancellationToken);
            var sha = path["/v1/note-assets/".Length..];
            sync.Paths.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Put)
            {
                uploaded[sha] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return ScriptedCloud.Json($$"""{"id":"{{sha}}","mime_type":"image/png","bytes":1,"created_at":"2026-11-24T09:00:00Z"}""", HttpStatusCode.Created);
            }

            return uploaded.TryGetValue(sha, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : ScriptedCloud.Json("""{"error":{"code":"NOT_FOUND","message":"no"}}""", HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Sync_uploads_pictures_before_the_note_and_sends_the_whole_document()
    {
        await OpenAsync();
        var tasks = new TaskBoard(_store, _clock, TimeZoneInfo.Utc);
        await tasks.LoadAsync();
        var picture = await _assets.AddAsync(Png());
        var note = await _notes.CreateAsync(title: "With picture");
        var doc = Doc("Plan");
        ((JsonArray)doc["content"]!).Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["assetId"] = picture.Sha256, ["width"] = 300 } });
        await _notes.SaveContentAsync(note.Id, "With picture", doc, "Plan", [picture.Sha256]);
        var (engine, cloud, uploaded) = Engine(tasks);
        cloud.BootstrapItems = "[]";

        var status = await engine.EnableAsync("01a209c9-4fe9-7666-a5f5-145014a54198");

        Assert.Equal(SyncState.Synced, status.State);
        Assert.Equal(Png(), uploaded[picture.Sha256]);
        var sent = cloud.Pushes.SelectMany(p => p["mutations"]!.AsArray()).OfType<JsonObject>().Single(m => m["resource_id"]!.GetValue<string>() == note.Id);
        Assert.Equal("create", sent["action"]!.GetValue<string>());
        Assert.Contains("#2563EB", sent["resource"]!["doc"]!.ToJsonString());
        Assert.Contains("futureNode", sent["resource"]!["doc"]!.ToJsonString());
        Assert.True((await _store.GetAssetAsync(picture.Sha256, CancellationToken.None))!.Uploaded);
        Assert.True(cloud.Paths.IndexOf("PUT /v1/note-assets/" + picture.Sha256) < cloud.Paths.FindIndex(p => p.StartsWith("/v1/sync/push", StringComparison.Ordinal)),
            "the picture is uploaded before the note that uses it");
    }

    [Fact]
    public async Task Notes_from_other_devices_arrive_with_their_pictures()
    {
        await OpenAsync();
        var tasks = new TaskBoard(_store, _clock, TimeZoneInfo.Utc);
        await tasks.LoadAsync();
        var (engine, cloud, uploaded) = Engine(tasks);
        var png = Png(7);
        var sha = NoteAssets.Hash(png);
        uploaded[sha] = png;
        var phoneNote = new JsonObject
        {
            ["id"] = "phone1", ["title"] = "From the phone", ["body"] = "Hi", ["folderId"] = "pf",
            ["doc"] = Doc("From the phone"), ["assets"] = new JsonArray(sha), ["createdUtc"] = "2026-11-24T08:00:00Z", ["modifiedUtc"] = "2026-11-24T08:00:00Z",
        };
        cloud.BootstrapItems = "[" + Feed("note_folder", "pf", 1, new JsonObject { ["id"] = "pf", ["name"] = "Phone" }) + "," + Feed("note", "phone1", 3, phoneNote) + "]";

        await engine.EnableAsync("01a209c9-4fe9-7666-a5f5-145014a54198");

        var arrived = await _notes.GetAsync("phone1");
        Assert.Equal(Doc("From the phone").ToJsonString(), arrived!.Doc!.ToJsonString());
        Assert.Equal("Phone", _notes.FindFolder("pf")!.Name);
        Assert.True(_assets.HasFile(sha));
        Assert.Null(await _store.GetMetaAsync("asset_downloads", CancellationToken.None));
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_note_edited_on_two_devices_keeps_both_versions()
    {
        await OpenAsync();
        var tasks = new TaskBoard(_store, _clock, TimeZoneInfo.Utc);
        await tasks.LoadAsync();
        var (engine, cloud, _) = Engine(tasks);
        var remote = new JsonObject { ["id"] = "n1", ["title"] = "Shared", ["body"] = "v1", ["doc"] = Doc("v1"), ["createdUtc"] = "2026-11-24T08:00:00Z", ["modifiedUtc"] = "2026-11-24T08:00:00Z" };
        cloud.BootstrapItems = "[" + Feed("note", "n1", 2, (JsonObject)remote.DeepClone()) + "]";
        await engine.EnableAsync("01a209c9-4fe9-7666-a5f5-145014a54198");
        await _notes.SaveContentAsync("n1", "Shared", Doc("edited on Windows"), "edited on Windows", []);
        var theirs = remote.DeepClone().AsObject();
        theirs["doc"] = Doc("edited on the phone");
        cloud.PushResponder = request =>
        {
            var m = request["mutations"]!.AsArray()[0]!;
            cloud.PushResponder = null;
            var current = new JsonObject { ["id"] = "n1", ["version"] = 3, ["schema_version"] = 1, ["updated_at"] = "2026-11-24T09:00:00Z", ["note"] = theirs };
            var result = new JsonObject { ["mutation_id"] = m["mutation_id"]!.GetValue<string>(), ["status"] = "conflict", ["code"] = "VERSION_CONFLICT", ["current"] = current };
            return ScriptedCloud.Json(new JsonObject { ["results"] = new JsonArray(result) }.ToJsonString());
        };

        await engine.SyncNowAsync();

        Assert.Contains("edited on the phone", (await _notes.GetAsync("n1"))!.Doc!.ToJsonString());
        var copy = _notes.Notes.Single(n => n.Title == "Shared (conflict copy)");
        Assert.Contains("edited on Windows", (await _notes.GetAsync(copy.Id))!.Doc!.ToJsonString());
        Assert.Single(await _store.IssuesAsync(CancellationToken.None));
        Assert.Equal(0, await _store.PendingCountAsync(CancellationToken.None));
    }
}
