using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Alondra1.App.ViewModels;
using Alondra1.Core.Mail;
using Alondra1.Core.Notes;

namespace Alondra1.App.Notes;

/// <summary>A row of the Notes sidebar: a view (All, Favorites, Archive, Trash) or a folder.</summary>
public sealed class NoteNavItem(string key, string label, string icon, NoteView view, string? folderId = null, int indent = 0)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public NoteView View { get; } = view;
    public string? FolderId { get; } = folderId;
    public int Indent { get; } = indent;
    public int Count { get; set; }
    public string CountText => Count > 0 ? Count.ToString(CultureInfo.CurrentCulture) : "";
    public bool IsFolder => FolderId is not null;
    public System.Windows.Thickness Margin => new(Indent * 14, 0, 0, 0);
}

/// <summary>A row of the note list (no document loaded).</summary>
public sealed class NoteItem(NoteSummary s, string? thumbnail)
{
    public string Id { get; } = s.Id;
    public string Title { get; } = s.DisplayTitle;
    public string Preview { get; } = s.Preview;
    public bool Favorite { get; } = s.Favorite;
    public bool Pinned { get; } = s.Pinned;
    public bool HasLinks { get; } = s.LinkCount > 0;
    public string? Thumbnail { get; } = thumbnail;
    public string When { get; } = Format(s.ModifiedUtc.ToLocalTime());
    public string AccessName => $"{Title}. {When}{(Favorite ? ". Favorite" : "")}{(Pinned ? ". Pinned" : "")}";

    private static string Format(DateTimeOffset t)
    {
        var today = DateTime.Today;
        if (t.Date == today)
            return t.ToString("t", CultureInfo.CurrentCulture);
        if (t.Date > today.AddDays(-7))
            return t.ToString("ddd", CultureInfo.CurrentCulture);
        return t.Year == today.Year ? t.ToString("MMM d", CultureInfo.CurrentCulture) : t.ToString("d", CultureInfo.CurrentCulture);
    }
}

/// <summary>A linked object shown above the editor.</summary>
public sealed record NoteLinkChip(NoteLink Link)
{
    public string Text => (Link.Type switch { NoteLink.Event => "📅 ", NoteLink.Contact => "👤 ", NoteLink.Mail => "✉ ", _ => "✓ " }) + (Link.Label ?? Link.Id);
}

/// <summary>
/// Presentation state of the Notes workspace (Notes spec §2): folders and views, the note list with search and
/// sorting, and the editor. All rules live in <see cref="NotesBoard"/>; this class turns them into rows and keeps the
/// editor and the store in step (autosave, pictures, links).
/// </summary>
public sealed partial class NotesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly NotesBoard? _board;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private IReadOnlyCollection<string>? _searchIds;
    private NoteEditorHost? _editor;
    private string? _openId;
    private bool _loadingNote;

    public NotesViewModel(AppServices services, MainViewModel host)
    {
        _services = services;
        Host = host;
        _board = services.NotesBoard;
        if (_board is not null)
            _board.Changed += Refresh;
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearchAsync(); };
        Sort = services.Config.Notes.Sort;
        Descending = services.Config.Notes.Descending;
        ShowFolders = services.Config.Notes.ShowFolders;
        ShowList = services.Config.Notes.ShowList;
        Refresh();
        if (host.IsDemo && _board is not null && _board.Notes.Count == 0)
            _ = NotesSamples.SeedAsync(_board, services.NoteAssets);
    }

    public MainViewModel Host { get; }
    public bool IsAvailable => _board is not null;
    public NotesBoard? Board => _board;
    public NoteAssets? Assets => _services.NoteAssets;
    public ObservableCollection<NoteNavItem> Sidebar { get; } = [];
    public ObservableCollection<NoteItem> Items { get; } = [];
    public ObservableCollection<NoteLinkChip> Links { get; } = [];

    public NoteView View { get; private set; } = NoteView.All;
    public string? FolderId { get; private set; }
    public string SearchText { get; private set; } = "";
    public NoteSort Sort { get; private set; }
    public bool Descending { get; private set; }
    public bool ShowFolders { get; private set; }
    public bool ShowList { get; private set; }
    public string? SelectedId => _openId;
    public bool HasNote => _openId is not null;
    public bool IsTrashView => View == NoteView.Trash;
    public string Status { get; private set; } = "";
    public string Heading => View switch
    {
        NoteView.Favorites => "Favorites",
        NoteView.Archive => "Archive",
        NoteView.Trash => "Trash",
        NoteView.Folder => _board?.FindFolder(FolderId ?? "")?.Name ?? "Folder",
        _ => "All notes",
    };

    public string EmptyText => !string.IsNullOrWhiteSpace(SearchText) ? "No notes match the search."
        : View switch
        {
            NoteView.Trash => "Trash is empty. Deleted notes stay here for " + _services.Config.Notes.TrashRetentionDays + " days.",
            NoteView.Archive => "No archived notes.",
            NoteView.Favorites => "Star a note to see it here.",
            _ => "No notes yet. Press Ctrl+N or choose New note.",
        };

    public string[] SortOptions { get; } = ["Last edited", "Date created", "Title"];

    // ------------------------------------------------------------------ list

    public void Refresh()
    {
        if (_board is null)
            return;
        var selectedKey = View == NoteView.Folder ? "f:" + FolderId : View.ToString();
        Sidebar.Clear();
        void Add(NoteNavItem item)
        {
            item.Count = _board.Count(item.View, item.FolderId);
            Sidebar.Add(item);
        }

        Add(new NoteNavItem(nameof(NoteView.All), "All notes", "Notes", NoteView.All));
        Add(new NoteNavItem(nameof(NoteView.Favorites), "Favorites", "Priority", NoteView.Favorites));
        void Folders(string? parent, int depth)
        {
            foreach (var f in _board.Children(parent))
            {
                Add(new NoteNavItem("f:" + f.Id, f.Name, "Folder", NoteView.Folder, f.Id, depth));
                if (depth < 6)
                    Folders(f.Id, depth + 1);
            }
        }

        Folders(null, 0);
        Add(new NoteNavItem(nameof(NoteView.Archive), "Archive", "Archive", NoteView.Archive));
        Add(new NoteNavItem(nameof(NoteView.Trash), "Trash", "Delete", NoteView.Trash));
        if (View == NoteView.Folder && (FolderId is null || _board.FindFolder(FolderId) is null))
        {
            View = NoteView.All;
            FolderId = null;
        }

        Items.Clear();
        foreach (var s in _board.Query(View, FolderId, _searchIds, Sort, Descending))
            Items.Add(new NoteItem(s, s.FirstAsset is { } a && Assets?.HasFile(a) == true ? Assets.PathOf(a) : null));
        SelectedNavKey = View == NoteView.Folder ? "f:" + FolderId : View.ToString();
        _ = selectedKey;
        Raise();
    }

    public string SelectedNavKey { get; private set; } = nameof(NoteView.All);

    private void Raise()
    {
        foreach (var name in new[] { nameof(Heading), nameof(EmptyText), nameof(HasNote), nameof(IsTrashView), nameof(SelectedNavKey), nameof(Status), nameof(ShowFolders), nameof(ShowList), nameof(Sort), nameof(Descending) })
            OnPropertyChanged(name);
    }

    public void SelectNav(NoteNavItem? item)
    {
        if (item is null || (item.View == View && item.FolderId == FolderId))
            return;
        View = item.View;
        FolderId = item.FolderId;
        Refresh();
    }

    public void ApplySearch(string? text)
    {
        SearchText = text ?? "";
        _searchTimer.Stop();
        _searchTimer.Start();
        OnPropertyChanged(nameof(SearchText));
    }

    private async Task RunSearchAsync()
    {
        if (_board is null)
            return;
        _searchIds = string.IsNullOrWhiteSpace(SearchText) ? null : (await _board.SearchAsync(SearchText)).ToHashSet();
        Refresh();
    }

    public void SetSort(int index, bool? descending = null)
    {
        Sort = (NoteSort)Math.Clamp(index, 0, 2);
        Descending = descending ?? Sort != NoteSort.Title;
        _services.Config.Notes.Sort = Sort;
        _services.Config.Notes.Descending = Descending;
        _services.SaveConfig();
        Refresh();
    }

    public void TogglePanes(bool folders)
    {
        if (folders)
            ShowFolders = !ShowFolders;
        else
            ShowList = !ShowList;
        _services.Config.Notes.ShowFolders = ShowFolders;
        _services.Config.Notes.ShowList = ShowList;
        _services.SaveConfig();
        Raise();
    }

    // ------------------------------------------------------------------ editor

    public void AttachEditor(NoteEditorHost editor)
    {
        _editor = editor;
        editor.Assets = Assets;
        editor.MessageReceived += message => _ = OnEditorMessageAsync(message);
        editor.MissingAsset += sha => _ = DownloadMissingAsync(sha);
    }

    /// <summary>Saves what is in the editor now (switching notes, closing, Ctrl+S).</summary>
    public async Task FlushAsync()
    {
        if (_editor is null || _openId is null || _loadingNote)
            return;
        var snapshot = await _editor.FlushAsync();
        if (snapshot is not null && snapshot["dirty"]?.GetValue<bool>() == true)
            await SaveSnapshotAsync(_openId, snapshot);
    }

    public async Task OpenAsync(string? id, bool focusTitle = false)
    {
        if (_board is null || _editor is null || id == _openId)
            return;
        await FlushAsync();
        _loadingNote = true;
        try
        {
            _openId = id;
            var note = id is null ? null : await _board.GetAsync(id);
            Links.Clear();
            foreach (var link in note?.Links ?? [])
                Links.Add(new NoteLinkChip(link));
            await _editor.LoadAsync(note, focusTitle);
            Status = note is null ? "" : $"Edited {note.ModifiedUtc.ToLocalTime():g}";
        }
        finally
        {
            _loadingNote = false;
        }

        Raise();
    }

    private async Task OnEditorMessageAsync(JsonObject m)
    {
        try
        {
            switch (m["type"]?.GetValue<string>())
            {
                case "change" when _openId is not null:
                    await SaveSnapshotAsync(_openId, m);
                    break;
                case "saveNow":
                    await FlushAsync();
                    break;
                case "newNote":
                    await NewNoteAsync();
                    break;
                case "pickImage":
                    PickImageRequested?.Invoke();
                    break;
                case "storeImage":
                    await StoreImageAsync(m["requestId"]!.GetValue<string>(), Convert.FromBase64String(m["data"]?.GetValue<string>() ?? ""));
                    break;
                case "fetchImage":
                    await FetchImageAsync(m["requestId"]!.GetValue<string>(), m["url"]!.GetValue<string>());
                    break;
                case "openLink":
                    OpenLink(m["href"]?.GetValue<string>() ?? "");
                    break;
                case "checklistToTask":
                    await ChecklistToTaskAsync(m["requestId"]!.GetValue<string>(), m["text"]!.GetValue<string>(), m["checked"]?.GetValue<bool>() == true);
                    break;
                case "error":
                    Debug.WriteLine("Note editor: " + m["where"] + " " + m["message"]);
                    break;
            }
        }
        catch (Exception ex) when (ex is NoteStorageException or NoteAssetException or FormatException or IOException or InvalidOperationException)
        {
            SetStatus("Not saved: " + ex.Message);
        }
    }

    private async Task SaveSnapshotAsync(string id, JsonObject snapshot)
    {
        if (_board is null || snapshot["doc"] is not JsonObject doc)
            return;
        var assets = (snapshot["assetIds"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        try
        {
            if (await _board.SaveContentAsync(id, snapshot["title"]?.GetValue<string>() ?? "", (JsonObject)doc.DeepClone(), snapshot["text"]?.GetValue<string>() ?? "", assets))
            {
                if (id == _openId)
                    await _editor!.CallAsync("saved", JsonValue.Create("Saved locally"));
            }
        }
        catch (NoteStorageException ex)
        {
            if (id == _openId)
                await _editor!.CallAsync("setStatus", JsonValue.Create("Not saved: " + ex.Message));
            throw;
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        OnPropertyChanged(nameof(Status));
        if (_editor is not null)
            _ = _editor.CallAsync("setStatus", JsonValue.Create(text));
    }

    /// <summary>Raised when the editor asks for a picture; the view shows the file picker and calls <see cref="InsertPicturesAsync"/>.</summary>
    public event Action? PickImageRequested;

    public async Task InsertPicturesAsync(IEnumerable<string> paths)
    {
        if (Assets is null || _editor is null)
            return;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > Assets.MaxBytes)
                    throw new NoteAssetException($"{info.Name} is larger than {Assets.MaxBytes >> 20} MB.");
                var asset = await Assets.AddAsync(await File.ReadAllBytesAsync(path), await OpenNoteAssetsAsync());
                await _editor.CallAsync("insertAsset", new JsonObject { ["assetId"] = asset.Sha256, ["width"] = asset.Width, ["name"] = info.Name });
            }
            catch (Exception ex) when (ex is NoteAssetException or IOException or UnauthorizedAccessException)
            {
                SetStatus(ex.Message);
            }
        }
    }

    /// <summary>Pictures the open note uses now (its edits are saved first), for the per-note picture limit.</summary>
    private async Task<IReadOnlyCollection<string>> OpenNoteAssetsAsync()
    {
        await FlushAsync();
        return _openId is { } id && _board is not null && await _board.GetAsync(id) is { } note ? note.Assets : [];
    }

    private async Task StoreImageAsync(string requestId, byte[] data)
    {
        try
        {
            var asset = await Assets!.AddAsync(data, await OpenNoteAssetsAsync());
            await _editor!.CallAsync("imageStored", new JsonObject { ["requestId"] = requestId, ["assetId"] = asset.Sha256, ["width"] = asset.Width });
        }
        catch (NoteAssetException ex)
        {
            await _editor!.CallAsync("imageFailed", new JsonObject { ["requestId"] = requestId, ["message"] = ex.Message });
        }
    }

    /// <summary>A picture pasted from a web page, loaded only because the user pressed "Load picture".</summary>
    private async Task FetchImageAsync(string requestId, string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new NoteAssetException("Only web pictures (http or https) can be loaded.");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > Assets!.MaxBytes)
                throw new NoteAssetException($"The picture is larger than {Assets.MaxBytes >> 20} MB.");
            var data = await response.Content.ReadAsByteArrayAsync();
            await StoreImageAsync(requestId, data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NoteAssetException)
        {
            await _editor!.CallAsync("imageFailed", new JsonObject { ["requestId"] = requestId, ["message"] = ex is NoteAssetException ? ex.Message : "The picture could not be loaded." });
        }
    }

    private async Task DownloadMissingAsync(string sha)
    {
        if (_services.CloudSync is not { } sync || await sync.TenantAsync() is not { } tenant)
            return;
        if (await sync.DownloadAssetAsync(tenant, sha) && _openId is { } id && _editor is not null)
        {
            _openId = null; // reload the note so the picture appears
            await OpenAsync(id);
        }
    }

    private async Task ChecklistToTaskAsync(string requestId, string text, bool done)
    {
        if (_services.Tasks is not { } tasks)
        {
            SetStatus("To-do is not available.");
            return;
        }

        var task = await tasks.AddTaskAsync(tasks.DefaultList.Id, text.Length > 500 ? text[..500] : text, t => t.Completed = done);
        await _editor!.CallAsync("taskCreated", new JsonObject { ["requestId"] = requestId, ["taskId"] = task.Id });
        if (_openId is { } id && _board is not null)
            await _board.AddLinkAsync(id, new NoteLink(NoteLink.Task, task.Id, task.Title));
        await ReloadLinksAsync();
    }

    /// <summary>Links in notes: web and mail links open in the default app; alondra:// links open inside Alondra1.</summary>
    public void OpenLink(string href)
    {
        if (href.StartsWith("alondra://task/", StringComparison.Ordinal))
        {
            Host.SelectWorkspace("To-do");
            Host.Todo.OpenTask(href["alondra://task/".Length..]);
            return;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "tel")
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                SetStatus("No app is set up to open this link.");
            }
        }
    }

    // ------------------------------------------------------------------ note actions

    public async Task NewNoteAsync(string title = "", JsonObject? doc = null, IEnumerable<NoteLink>? links = null, string? text = null, IEnumerable<string>? assets = null)
    {
        if (_board is null)
            return;
        await FlushAsync();
        var folder = View == NoteView.Folder ? FolderId : null;
        if (View is NoteView.Trash or NoteView.Archive)
        {
            View = NoteView.All;
            FolderId = null;
        }

        _searchIds = null;
        SearchText = "";
        var note = await _board.CreateAsync(folder, title, doc, links, text, assets);
        if (View == NoteView.Favorites)
            await _board.SetFavoriteAsync([note.Id], true);
        Refresh();
        await OpenAsync(note.Id, focusTitle: string.IsNullOrEmpty(title));
        NoteCreated?.Invoke(note.Id);
    }

    public event Action<string>? NoteCreated;

    public async Task DeleteAsync(IReadOnlyCollection<string> ids, bool forever)
    {
        if (_board is null || ids.Count == 0)
            return;
        if (ids.Contains(_openId ?? ""))
            await FlushAsync();
        if (forever)
            await _board.DeleteForeverAsync(ids);
        else
            await _board.TrashAsync(ids);
        if (ids.Contains(_openId ?? ""))
            await OpenAsync(null);
    }

    public Task RestoreAsync(IReadOnlyCollection<string> ids) => _board?.RestoreAsync(ids) ?? Task.CompletedTask;
    public Task ArchiveAsync(IReadOnlyCollection<string> ids, bool archived) => _board?.ArchiveAsync(ids, archived) ?? Task.CompletedTask;
    public Task FavoriteAsync(IReadOnlyCollection<string> ids, bool on) => _board?.SetFavoriteAsync(ids, on) ?? Task.CompletedTask;
    public Task PinAsync(IReadOnlyCollection<string> ids, bool on) => _board?.SetPinnedAsync(ids, on) ?? Task.CompletedTask;
    public Task MoveAsync(IReadOnlyCollection<string> ids, string? folderId) => _board?.MoveAsync(ids, folderId) ?? Task.CompletedTask;

    public async Task DuplicateAsync(string id)
    {
        await FlushAsync();
        if (await _board!.DuplicateAsync(id) is { } copy)
            await OpenAsync(copy.Id);
    }

    public async Task EmptyTrashAsync()
    {
        if (_board is null)
            return;
        await DeleteAsync(_board.Query(NoteView.Trash).Select(n => n.Id).ToList(), forever: true);
    }

    public async Task ReloadLinksAsync()
    {
        Links.Clear();
        if (_openId is not null && await _board!.GetAsync(_openId) is { } note)
        {
            foreach (var link in note.Links)
                Links.Add(new NoteLinkChip(link));
        }
    }

    public async Task AddLinkAsync(NoteLink link)
    {
        if (_openId is null)
            return;
        await FlushAsync();
        await _board!.AddLinkAsync(_openId, link);
        await ReloadLinksAsync();
    }

    public async Task RemoveLinkAsync(NoteLink link)
    {
        if (_openId is null)
            return;
        await FlushAsync();
        await _board!.RemoveLinkAsync(_openId, link);
        await ReloadLinksAsync();
    }

    /// <summary>Opens a linked object, or says it is no longer available.</summary>
    public async Task OpenLinkedAsync(NoteLink link)
    {
        switch (link.Type)
        {
            case NoteLink.Task:
                if (_services.Tasks?.FindTask(link.Id) is null)
                    SetStatus("That task no longer exists.");
                else
                    OpenLink("alondra://task/" + link.Id);
                break;
            case NoteLink.Event:
                var (events, _) = await Host.LinkTargetsAsync();
                if (events.FirstOrDefault(e => e.Id == link.Id) is { } ev)
                    Host.OpenCalendarAt(ev.Start.ToLocalTime());
                else
                    SetStatus("That event is no longer available (deleted, or its account is not connected).");
                break;
            case NoteLink.Contact:
                Host.OpenContact(link.Label ?? "");
                break;
            case NoteLink.Mail:
                if (!Host.OpenMessageById(link.Id, link.Account))
                    SetStatus("That message is no longer in the mailbox on this PC.");
                break;
        }
    }

    // ------------------------------------------------------------------ folders

    public Task<NoteFolder> AddFolderAsync(string name, string? parentId) => _board!.AddFolderAsync(name, parentId);
    public Task RenameFolderAsync(string id, string name) => _board!.RenameFolderAsync(id, name);
    public Task DeleteFolderAsync(string id) => _board!.DeleteFolderAsync(id);
    public Task MoveFolderAsync(string id, string? parentId) => _board!.MoveFolderAsync(id, parentId);

    // ------------------------------------------------------------------ mail, import, export

    /// <summary>
    /// A note from an email (only on the user's request): subject as title, sender and date, the text, and, when asked,
    /// the pictures attached to the message. The note links back to the message.
    /// </summary>
    public async Task CreateFromMailAsync(StoredMessage message, string text, IReadOnlyList<(string Name, byte[] Data)> pictures)
    {
        var content = new JsonArray
        {
            Paragraph($"From {message.FromName} <{message.FromAddress}> · {message.ReceivedUtc.ToLocalTime():g}", italic: true),
        };
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(5000))
            content.Add(Paragraph(line));
        var assets = new List<string>();
        foreach (var (name, data) in pictures)
        {
            try
            {
                var asset = await Assets!.AddAsync(data, assets);
                assets.Add(asset.Sha256);
                content.Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["assetId"] = asset.Sha256, ["caption"] = name, ["width"] = asset.Width > 600 ? 600 : null } });
            }
            catch (NoteAssetException)
            {
                content.Add(Paragraph($"[Attachment not added: {name}]"));
            }
        }

        var doc = new JsonObject { ["schemaVersion"] = 1, ["type"] = "doc", ["content"] = content };
        var link = new NoteLink(NoteLink.Mail, message.MessageId ?? $"{message.FolderId}/{message.Uid}", message.Subject, message.AccountId);
        await NewNoteAsync(string.IsNullOrWhiteSpace(message.Subject) ? "Email note" : message.Subject, doc, [link], text, assets);
    }

    private static JsonObject Paragraph(string text, bool italic = false)
    {
        var p = new JsonObject { ["type"] = "paragraph" };
        if (text.Length > 0)
        {
            var node = new JsonObject { ["type"] = "text", ["text"] = text };
            if (italic)
                node["marks"] = new JsonArray(new JsonObject { ["type"] = "italic" });
            p["content"] = new JsonArray(node);
        }

        return p;
    }

    /// <summary>Copies the plain-text notes kept in mail accounts by earlier versions into local notes (once per note).</summary>
    public async Task<int> ImportAccountNotesAsync()
    {
        var existing = _board!.Notes.Select(n => n.Title).ToHashSet();
        var imported = 0;
        foreach (var entry in await Host.AccountNotesAsync())
        {
            if (existing.Contains(entry.Title))
                continue;
            await _board.CreateAsync(null, entry.Title, NoteEditorHost.PlainDocument(entry.Body), null, entry.Body);
            imported++;
        }

        return imported;
    }

    /// <summary>Exports the open note. HTML is one self-contained file (pictures embedded); Markdown writes pictures next to it.</summary>
    public async Task ExportAsync(string path, string format)
    {
        if (_editor is null || _openId is null)
            return;
        await FlushAsync();
        var text = await _editor.ExportAsync(format) ?? "";
        var note = await _board!.GetAsync(_openId);
        foreach (var sha in note?.Assets ?? [])
        {
            if (await Assets!.ReadAsync(sha) is not { } found)
                continue;
            var url = NoteEditorHost.AssetPrefix + sha;
            if (format == "html")
            {
                text = text.Replace(url, $"data:{found.Asset.MimeType};base64,{Convert.ToBase64String(found.Data)}", StringComparison.Ordinal);
            }
            else
            {
                var folder = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_files");
                Directory.CreateDirectory(folder);
                var name = sha[..12] + "." + found.Asset.MimeType.Split('/')[1].Replace("jpeg", "jpg", StringComparison.Ordinal);
                await File.WriteAllBytesAsync(Path.Combine(folder, name), found.Data);
                text = text.Replace(url, Path.GetFileName(folder) + "/" + name, StringComparison.Ordinal);
            }
        }

        await File.WriteAllTextAsync(path, text);
    }

    /// <summary>Imports an HTML file as a new note (scripts and remote content are dropped by the editor's schema).</summary>
    public async Task ImportHtmlAsync(string path)
    {
        var html = await File.ReadAllTextAsync(path);
        await NewNoteAsync(Path.GetFileNameWithoutExtension(path));
        await _editor!.CallAsync("importHtml", JsonValue.Create(html));
    }
}
