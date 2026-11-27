using System.Text.Json.Nodes;
using Alondra1.Core.Cloud;
using Alondra1.Core.Ports;

namespace Alondra1.Core.Notes;

/// <summary>
/// The local Notes module: the only writer of notes and note folders (like TaskBoard for tasks). Keeps summaries in
/// memory for fast lists; documents load on demand. Everything works offline and without an account; when Cloud Sync
/// is on, the store queues each change in the same transaction. Use from the UI thread.
/// </summary>
public sealed class NotesBoard(INoteStore store, IClock clock)
{
    public const int MaxTitle = 500;
    public const int MaxBody = 200_000;
    public const int MaxDocumentBytes = 2 << 20;
    public static readonly TimeSpan DefaultTrashRetention = TimeSpan.FromDays(30);

    private readonly Dictionary<string, NoteSummary> _notes = [];
    private readonly Dictionary<string, NoteFolder> _folders = [];

    public event Action? Changed;

    public IReadOnlyCollection<NoteSummary> Notes => _notes.Values;
    public IReadOnlyList<NoteFolder> Folders => _folders.Values.OrderBy(f => f.Position).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    public NoteSummary? Find(string id) => _notes.GetValueOrDefault(id);
    public NoteFolder? FindFolder(string id) => _folders.GetValueOrDefault(id);

    /// <summary>The folder's name for display, or "Notes" for notes outside any folder.</summary>
    public string FolderName(string? id) => id is not null && _folders.TryGetValue(id, out var folder) ? folder.Name : "Notes";
    public DateTimeOffset Now => clock.UtcNow;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadNotesAsync(cancellationToken).ConfigureAwait(true);
        _notes.Clear();
        _folders.Clear();
        foreach (var n in snapshot.Notes)
            _notes[n.Id] = n;
        foreach (var f in snapshot.Folders)
            _folders[f.Id] = f;
        Changed?.Invoke();
    }

    public Task<Note?> GetAsync(string id, CancellationToken cancellationToken = default) => store.GetNoteAsync(id, cancellationToken);

    // ------------------------------------------------------------------ views

    public static bool InView(NoteSummary n, NoteView view, string? folderId) => view switch
    {
        NoteView.Trash => n.TrashedUtc is not null,
        NoteView.Archive => n.TrashedUtc is null && n.ArchivedUtc is not null,
        NoteView.Favorites => n.TrashedUtc is null && n.ArchivedUtc is null && n.Favorite,
        NoteView.Folder => n.TrashedUtc is null && n.ArchivedUtc is null && n.FolderId == folderId,
        _ => n.TrashedUtc is null && n.ArchivedUtc is null,
    };

    /// <summary>Notes of a view, pinned first (except Trash), then by the chosen order. With search ids, only those.</summary>
    public IReadOnlyList<NoteSummary> Query(NoteView view, string? folderId = null, IReadOnlyCollection<string>? onlyIds = null,
        NoteSort sort = NoteSort.Modified, bool descending = true)
    {
        IEnumerable<NoteSummary> items = _notes.Values.Where(n => InView(n, view, folderId));
        if (onlyIds is not null)
        {
            var set = onlyIds as ISet<string> ?? onlyIds.ToHashSet();
            items = items.Where(n => set.Contains(n.Id));
        }

        Func<NoteSummary, object> key = sort switch
        {
            NoteSort.Created => n => n.CreatedUtc,
            NoteSort.Title => n => n.DisplayTitle.ToLowerInvariant(),
            _ => n => n.ModifiedUtc,
        };
        var ordered = view == NoteView.Trash ? items.OrderBy(_ => 0) : items.OrderByDescending(n => n.Pinned);
        ordered = descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        return ordered.ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    public int Count(NoteView view, string? folderId = null) => _notes.Values.Count(n => InView(n, view, folderId));

    public Task<IReadOnlyList<string>> SearchAsync(string query, int limit = 500, CancellationToken cancellationToken = default) =>
        store.SearchNotesAsync(query, limit, cancellationToken);

    // ------------------------------------------------------------------ notes

    public async Task<Note> CreateAsync(string? folderId = null, string title = "", JsonObject? doc = null, IEnumerable<NoteLink>? links = null,
        string? text = null, IEnumerable<string>? assets = null)
    {
        var now = clock.UtcNow;
        var note = new Note
        {
            Id = NewId(), Title = Clip(title.Trim(), MaxTitle), FolderId = folderId is not null && _folders.ContainsKey(folderId) ? folderId : null,
            Doc = doc, Body = Clip(text ?? "", MaxBody), Links = links?.ToList() ?? [], Assets = assets?.Distinct().ToList() ?? [],
            CreatedUtc = now, ModifiedUtc = now,
        };
        await WriteAsync(note).ConfigureAwait(true);
        return note;
    }

    /// <summary>The editor's autosave. Returns false (and writes nothing) when the content did not change.</summary>
    public async Task<bool> SaveContentAsync(string id, string title, JsonObject doc, string text, IReadOnlyCollection<string> assets)
    {
        var note = await store.GetNoteAsync(id, CancellationToken.None).ConfigureAwait(true) ?? throw new NoteStorageException("The note no longer exists.");
        title = Clip(title.Trim(), MaxTitle);
        var docJson = doc.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(docJson) > MaxDocumentBytes)
            throw new NoteStorageException("This note is too large to save (over 2 MB of text and tables). Split it into smaller notes; pictures do not count.");
        if (note.Title == title && note.Doc?.ToJsonString() == docJson)
            return false;
        note.Title = title;
        note.Doc = doc;
        note.Body = Clip(text, MaxBody);
        note.Assets = assets.Distinct().ToList();
        note.ModifiedUtc = clock.UtcNow;
        await WriteAsync(note).ConfigureAwait(true);
        return true;
    }

    public Task RenameAsync(string id, string title) => EditAsync([id], n => n.Title = Clip(title.Trim(), MaxTitle));
    public Task SetFavoriteAsync(IReadOnlyCollection<string> ids, bool favorite) => EditAsync(ids, n => n.Favorite = favorite);
    public Task SetPinnedAsync(IReadOnlyCollection<string> ids, bool pinned) => EditAsync(ids, n => n.Pinned = pinned);
    public Task ArchiveAsync(IReadOnlyCollection<string> ids, bool archived) => EditAsync(ids, n => n.ArchivedUtc = archived ? clock.UtcNow : null);
    public Task TrashAsync(IReadOnlyCollection<string> ids) => EditAsync(ids, n => n.TrashedUtc = clock.UtcNow);
    public Task RestoreAsync(IReadOnlyCollection<string> ids) => EditAsync(ids, n => n.TrashedUtc = null);

    public Task MoveAsync(IReadOnlyCollection<string> ids, string? folderId)
    {
        if (folderId is not null && !_folders.ContainsKey(folderId))
            throw new NoteStorageException("That folder no longer exists.");
        return EditAsync(ids, n => n.FolderId = folderId);
    }

    public Task AddLinkAsync(string id, NoteLink link) => EditAsync([id], n =>
    {
        if (!n.Links.Any(l => l.Type == link.Type && l.Id == link.Id))
            n.Links.Add(link);
    });

    public Task RemoveLinkAsync(string id, NoteLink link) => EditAsync([id], n => n.Links.RemoveAll(l => l.Type == link.Type && l.Id == link.Id));

    public async Task<Note?> DuplicateAsync(string id)
    {
        var source = await store.GetNoteAsync(id, CancellationToken.None).ConfigureAwait(true);
        if (source is null)
            return null;
        var now = clock.UtcNow;
        var copy = source.Clone();
        copy.Id = NewId();
        copy.Title = Clip(string.IsNullOrWhiteSpace(source.Title) ? "Copy" : source.Title + " (copy)", MaxTitle);
        copy.Pinned = false;
        copy.TrashedUtc = null;
        copy.CreatedUtc = now;
        copy.ModifiedUtc = now;
        await WriteAsync(copy).ConfigureAwait(true);
        return copy;
    }

    /// <summary>Removes notes for good (after the user confirmed). Their pictures stay until no note uses them.</summary>
    public async Task DeleteForeverAsync(IReadOnlyCollection<string> ids)
    {
        var changes = new NoteChangeSet();
        changes.DeleteNotes.AddRange(ids.Where(_notes.ContainsKey));
        if (changes.IsEmpty)
            return;
        await store.ApplyNotesAsync(changes, CancellationToken.None).ConfigureAwait(true);
        foreach (var id in changes.DeleteNotes)
            _notes.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Deletes notes that have been in Trash longer than the retention period. Returns how many.</summary>
    public async Task<int> PurgeTrashAsync(TimeSpan retention)
    {
        var cutoff = clock.UtcNow - retention;
        var old = _notes.Values.Where(n => n.TrashedUtc is { } t && t < cutoff).Select(n => n.Id).ToList();
        await DeleteForeverAsync(old).ConfigureAwait(true);
        return old.Count;
    }

    /// <summary>Keeps a local version that lost a sync conflict as a separate note.</summary>
    public async Task<Note> ConflictCopyAsync(Note mine)
    {
        var copy = mine.Clone();
        copy.Id = NewId();
        copy.Title = Clip((string.IsNullOrWhiteSpace(mine.Title) ? "Note" : mine.Title) + NoteMerge.ConflictSuffix, MaxTitle);
        copy.CreatedUtc = clock.UtcNow;
        copy.ModifiedUtc = clock.UtcNow;
        await WriteAsync(copy).ConfigureAwait(true);
        return copy;
    }

    /// <summary>
    /// Resolves a sync conflict between a note (the other device's version, under the original id) and its conflict copy
    /// (this device's version). The original id keeps the result so every device converges; the conflict copy goes to
    /// Trash, where it can still be restored. Returns the note to show afterwards.
    /// </summary>
    public async Task<string?> ResolveConflictAsync(string originalId, string copyId, NoteConflictChoice choice)
    {
        var copy = await store.GetNoteAsync(copyId, CancellationToken.None).ConfigureAwait(true);
        var original = await store.GetNoteAsync(originalId, CancellationToken.None).ConfigureAwait(true);
        switch (choice)
        {
            case NoteConflictChoice.KeepBoth:
                return copy?.Id ?? original?.Id;
            case NoteConflictChoice.KeepOther:
                if (copy is not null)
                    await TrashAsync([copy.Id]).ConfigureAwait(true);
                return original?.Id;
        }

        if (copy is null)
            return original?.Id;
        if (original is null)
        {
            // The other device deleted the note: this device's version stays, under its own title again.
            await EditAsync([copy.Id], n => n.Title = Clip(NoteMerge.OriginalTitle(n.Title), MaxTitle)).ConfigureAwait(true);
            return copy.Id;
        }

        if (choice == NoteConflictChoice.KeepMine)
        {
            original.Title = Clip(NoteMerge.OriginalTitle(copy.Title), MaxTitle);
            original.Doc = copy.Doc?.DeepClone() as JsonObject;
            original.Body = copy.Body;
            original.Assets = [.. copy.Assets];
            original.Links = copy.Links.Select(l => l with { }).ToList();
        }
        else
        {
            var doc = NoteMerge.CombineDocuments(original.Doc, original.Body, copy.Doc, copy.Body);
            if (System.Text.Encoding.UTF8.GetByteCount(doc.ToJsonString()) > MaxDocumentBytes)
                throw new NoteStorageException("Both versions together are over 2 MB of text and tables. Keep one version, or keep both as separate notes.");
            original.Doc = doc;
            original.Body = Clip(NoteMerge.CombineText(original.Body, copy.Body), MaxBody);
            original.Assets = original.Assets.Concat(copy.Assets).Distinct().ToList();
            original.Links = original.Links.Concat(copy.Links.Where(l => !original.Links.Any(o => o.Type == l.Type && o.Id == l.Id))).Take(50).ToList();
        }

        original.ModifiedUtc = clock.UtcNow;
        await WriteAsync(original).ConfigureAwait(true);
        await TrashAsync([copy.Id]).ConfigureAwait(true);
        return original.Id;
    }

    /// <summary>The first note (not in Trash) that links to the object, e.g. an event's meeting notes.</summary>
    public async Task<Note?> FindLinkedAsync(string type, string id)
    {
        foreach (var summary in _notes.Values.Where(n => n.LinkCount > 0 && n.TrashedUtc is null).OrderByDescending(n => n.ModifiedUtc).ToList())
        {
            if (await store.GetNoteAsync(summary.Id, CancellationToken.None).ConfigureAwait(true) is { } note && note.Links.Any(l => l.Type == type && l.Id == id))
                return note;
        }

        return null;
    }

    private async Task EditAsync(IReadOnlyCollection<string> ids, Action<Note> edit)
    {
        var changes = new NoteChangeSet();
        foreach (var id in ids.Distinct())
        {
            var note = await store.GetNoteAsync(id, CancellationToken.None).ConfigureAwait(true);
            if (note is null)
                continue;
            edit(note);
            note.ModifiedUtc = clock.UtcNow;
            changes.UpsertNotes.Add(note);
        }

        if (changes.IsEmpty)
            return;
        await store.ApplyNotesAsync(changes, CancellationToken.None).ConfigureAwait(true);
        foreach (var n in changes.UpsertNotes)
            _notes[n.Id] = NoteSummary.From(n);
        Changed?.Invoke();
    }

    private async Task WriteAsync(Note note)
    {
        var changes = new NoteChangeSet();
        changes.UpsertNotes.Add(note);
        await store.ApplyNotesAsync(changes, CancellationToken.None).ConfigureAwait(true);
        _notes[note.Id] = NoteSummary.From(note);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ folders

    public async Task<NoteFolder> AddFolderAsync(string name, string? parentId = null)
    {
        name = ValidFolderName(name);
        var now = clock.UtcNow;
        var folder = new NoteFolder
        {
            Id = NewId(), Name = name, ParentId = parentId is not null && _folders.ContainsKey(parentId) ? parentId : null,
            Position = _folders.Count == 0 ? 0 : _folders.Values.Max(f => f.Position) + 1, CreatedUtc = now, ModifiedUtc = now,
        };
        await WriteFoldersAsync([folder]).ConfigureAwait(true);
        return folder;
    }

    public Task RenameFolderAsync(string id, string name)
    {
        var folder = _folders.GetValueOrDefault(id)?.Clone() ?? throw new NoteStorageException("That folder no longer exists.");
        folder.Name = ValidFolderName(name);
        folder.ModifiedUtc = clock.UtcNow;
        return WriteFoldersAsync([folder]);
    }

    /// <summary>Moves a folder under another (null = top level); refuses to put a folder inside itself.</summary>
    public Task MoveFolderAsync(string id, string? parentId, double? position = null)
    {
        var folder = _folders.GetValueOrDefault(id)?.Clone() ?? throw new NoteStorageException("That folder no longer exists.");
        for (var p = parentId; p is not null; p = _folders.GetValueOrDefault(p)?.ParentId)
        {
            if (p == id)
                throw new NoteStorageException("A folder cannot be moved into itself.");
        }

        folder.ParentId = parentId;
        if (position is not null)
            folder.Position = position.Value;
        folder.ModifiedUtc = clock.UtcNow;
        return WriteFoldersAsync([folder]);
    }

    /// <summary>Deletes a folder. Its notes move to All Notes (nothing is deleted); subfolders move up one level.</summary>
    public async Task DeleteFolderAsync(string id)
    {
        if (!_folders.TryGetValue(id, out var folder))
            return;
        var changes = new NoteChangeSet();
        changes.DeleteFolders.Add(id);
        foreach (var child in _folders.Values.Where(f => f.ParentId == id))
        {
            var moved = child.Clone();
            moved.ParentId = folder.ParentId;
            moved.ModifiedUtc = clock.UtcNow;
            changes.UpsertFolders.Add(moved);
        }

        foreach (var summary in _notes.Values.Where(n => n.FolderId == id).ToList())
        {
            if (await store.GetNoteAsync(summary.Id, CancellationToken.None).ConfigureAwait(true) is { } note)
            {
                note.FolderId = null;
                note.ModifiedUtc = clock.UtcNow;
                changes.UpsertNotes.Add(note);
            }
        }

        await store.ApplyNotesAsync(changes, CancellationToken.None).ConfigureAwait(true);
        _folders.Remove(id);
        foreach (var f in changes.UpsertFolders)
            _folders[f.Id] = f;
        foreach (var n in changes.UpsertNotes)
            _notes[n.Id] = NoteSummary.From(n);
        Changed?.Invoke();
    }

    private async Task WriteFoldersAsync(IEnumerable<NoteFolder> folders)
    {
        var changes = new NoteChangeSet();
        changes.UpsertFolders.AddRange(folders);
        await store.ApplyNotesAsync(changes, CancellationToken.None).ConfigureAwait(true);
        foreach (var f in changes.UpsertFolders)
            _folders[f.Id] = f;
        Changed?.Invoke();
    }

    private static string ValidFolderName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new NoteStorageException("Enter a folder name.");
        return Clip(name, 200);
    }

    /// <summary>Folders below a parent (null = top level), in order.</summary>
    public IReadOnlyList<NoteFolder> Children(string? parentId) =>
        Folders.Where(f => f.ParentId == parentId || (parentId is null && f.ParentId is not null && !_folders.ContainsKey(f.ParentId))).ToList();

    // ------------------------------------------------------------------ cloud

    /// <summary>Changes from other devices: written with their versions and the cursor, not queued again.</summary>
    public async Task ApplyRemoteAsync(NoteChangeSet changes, IReadOnlyCollection<SyncVersion> versions, string? cursor)
    {
        if (store is not ICloudNoteStore cloud)
            throw new InvalidOperationException("The note store does not support Cloud Sync.");
        await cloud.ApplyRemoteNotesAsync(changes, versions, cursor, CancellationToken.None).ConfigureAwait(true);
        foreach (var id in changes.DeleteNotes)
            _notes.Remove(id);
        foreach (var id in changes.DeleteFolders)
            _folders.Remove(id);
        foreach (var n in changes.UpsertNotes)
            _notes[n.Id] = NoteSummary.From(n);
        foreach (var f in changes.UpsertFolders)
            _folders[f.Id] = f;
        if (!changes.IsEmpty)
            Changed?.Invoke();
    }

    private static string Clip(string value, int max) => value.Length > max ? value[..max] : value;
}

/// <summary>A note store that can also take changes from the cloud without queueing them.</summary>
public interface ICloudNoteStore : INoteStore
{
    Task ApplyRemoteNotesAsync(NoteChangeSet changes, IReadOnlyCollection<SyncVersion> versions, string? cursor, CancellationToken cancellationToken);
}
