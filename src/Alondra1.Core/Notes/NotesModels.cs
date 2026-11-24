using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alondra1.Core.Notes;

/// <summary>
/// A local note (alondra1-backend contract/schemas/notes-v1.schema.json). The document is the shared rich-text JSON
/// (note-document-v1, edited by the shared notes editor in alondra1-backend contract/notes-editor); Body is its plain text, for search, previews and older clients.
/// </summary>
public sealed class Note
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Pinned { get; set; }
    public JsonObject? Doc { get; set; }
    public string? FolderId { get; set; }
    public bool Favorite { get; set; }
    public DateTimeOffset? ArchivedUtc { get; set; }
    public DateTimeOffset? TrashedUtc { get; set; }
    public List<string> Assets { get; set; } = [];
    public List<NoteLink> Links { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    public Note Clone()
    {
        var copy = (Note)MemberwiseClone();
        copy.Doc = Doc?.DeepClone() as JsonObject;
        copy.Assets = [.. Assets];
        copy.Links = Links.Select(l => l with { }).ToList();
        return copy;
    }
}

/// <summary>A reference to another Alondra1 object. Opening it checks access again; a missing target says so.</summary>
public sealed record NoteLink(string Type, string Id, string? Label = null, string? Account = null)
{
    public const string Event = "event", Contact = "contact", Mail = "mail", Task = "task";
}

public sealed class NoteFolder
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public string Name { get; set; } = "";
    public double Position { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    public NoteFolder Clone() => (NoteFolder)MemberwiseClone();
}

/// <summary>What the note list needs without loading documents (fast with 10,000+ notes).</summary>
public sealed record NoteSummary(
    string Id, string? FolderId, string Title, string Preview, bool Favorite, bool Pinned,
    DateTimeOffset? ArchivedUtc, DateTimeOffset? TrashedUtc, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc,
    string? FirstAsset, int LinkCount)
{
    public static NoteSummary From(Note n) => new(n.Id, n.FolderId, n.Title, PreviewOf(n.Body, n.Title), n.Favorite, n.Pinned,
        n.ArchivedUtc, n.TrashedUtc, n.CreatedUtc, n.ModifiedUtc, n.Assets.FirstOrDefault(), n.Links.Count);

    /// <summary>The first lines of text that are not the title, up to 200 characters.</summary>
    public static string PreviewOf(string body, string title)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count > 0 && string.IsNullOrWhiteSpace(title) is false && lines[0] == title.Trim())
            lines.RemoveAt(0);
        var text = string.Join(" ", lines.Select(l => l.StartsWith("[x] ", StringComparison.Ordinal) ? "☑ " + l[4..] : l.StartsWith("[ ] ", StringComparison.Ordinal) ? "☐ " + l[4..] : l));
        return text.Length > 200 ? text[..200] : text;
    }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? (Preview.Length > 0 ? Preview.Split(' ', 8).Take(7).Aggregate((a, b) => a + " " + b) : "Untitled note") : Title;
}

public sealed class NotesSnapshot
{
    public List<NoteFolder> Folders { get; set; } = [];
    public List<NoteSummary> Notes { get; set; } = [];
}

/// <summary>Changes written in one transaction (local edits or changes from the cloud).</summary>
public sealed class NoteChangeSet
{
    public List<Note> UpsertNotes { get; } = [];
    public List<string> DeleteNotes { get; } = [];
    public List<NoteFolder> UpsertFolders { get; } = [];
    public List<string> DeleteFolders { get; } = [];
    public bool IsEmpty => UpsertNotes.Count == 0 && DeleteNotes.Count == 0 && UpsertFolders.Count == 0 && DeleteFolders.Count == 0;
}

/// <summary>A picture used in notes, stored once by content hash (notes-assets\ab\abcd…).</summary>
public sealed record NoteAsset(string Sha256, string MimeType, long Bytes, int? Width, int? Height, DateTimeOffset CreatedUtc, bool Uploaded);

/// <summary>Persistence of notes, folders and pictures (implemented by SqliteTaskStore in todo.db).</summary>
public interface INoteStore
{
    Task<NotesSnapshot> LoadNotesAsync(CancellationToken cancellationToken);
    Task<Note?> GetNoteAsync(string id, CancellationToken cancellationToken);
    /// <summary>Writes the changes in one transaction; queued for Cloud Sync when recording.</summary>
    Task ApplyNotesAsync(NoteChangeSet changes, CancellationToken cancellationToken);
    /// <summary>Note ids matching all words (prefix match), best first. Empty query = none.</summary>
    Task<IReadOnlyList<string>> SearchNotesAsync(string query, int limit, CancellationToken cancellationToken);
    Task<NoteAsset?> GetAssetAsync(string sha256, CancellationToken cancellationToken);
    Task SaveAssetAsync(NoteAsset asset, CancellationToken cancellationToken);
    Task MarkAssetUploadedAsync(string sha256, CancellationToken cancellationToken);
}

public enum NoteView { All, Favorites, Folder, Archive, Trash }

public enum NoteSort { Modified, Created, Title }

public sealed class NoteStorageException(string message) : Exception(message);
