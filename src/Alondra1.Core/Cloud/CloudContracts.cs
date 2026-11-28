using Alondra1.Core.Tasks;

namespace Alondra1.Core.Cloud;

/// <summary>
/// Synced kinds on Windows: To-do (groups, lists, tasks) and the local Notes module (folders, notes). Calendar events in
/// this app come from mail accounts, so they are not synced.
/// </summary>
public static class CloudKinds
{
    public const string Group = "group";
    public const string List = "list";
    public const string Task = "task";
    public const string NoteFolder = "note_folder";
    public const string Note = "note";
    public const string Tag = "tag";
    public const string TagLink = "tag_link";

    /// <summary>Push order: containers before their contents, so references exist on the server (tag links last).</summary>
    public static readonly string[] Order = [Group, List, Task, NoteFolder, Note, Tag, TagLink];

    public static bool IsNoteKind(string type) => type is Note or NoteFolder;

    public static bool IsTagKind(string type) => type is Tag or TagLink;
}

/// <summary>One pending cloud change. The mutation id is renewed when the item changes after a send attempt.</summary>
public sealed record OutboxItem(long Seq, string EntityType, string EntityId, string MutationId, bool Attempted, bool Deleted);

/// <summary>The server version of an item and its last server JSON (the base for merge patches).</summary>
public sealed record SyncVersion(string EntityType, string EntityId, long Version, string? ServerJson);

/// <summary>A sync problem shown to the user. CopyId: the conflict copy kept for a note that conflicted (compare and resolve it).</summary>
public sealed record SyncIssue(long Id, string EntityType, string EntityId, string Title, string Message, DateTimeOffset CreatedUtc, string? CopyId = null);

/// <summary>Sync states (Windows integration spec §6.6; the Android app uses the same names).</summary>
public enum SyncState
{
    LocalOnly, SignInRequired, PlanRequired, ReadyOff, Bootstrapping, Syncing, Synced, Offline, Paused,
    AuthRequired, ReadOnlyPlan, Conflict, ErrorRetryable, ErrorPermanent,
}

public sealed record SyncStatus(SyncState State, DateTimeOffset? LastSynced = null, string? Message = null);

/// <summary>
/// Cloud bookkeeping that lives in todo.db next to the tasks, so the outbox is written in the same transaction as the
/// change it describes. Implemented by the SQLite task store.
/// </summary>
public interface ICloudSyncStore
{
    /// <summary>When true, every local change is also queued in the outbox (set once Cloud Sync is set up).</summary>
    bool Recording { get; }

    Task SetRecordingAsync(bool recording, CancellationToken cancellationToken);
    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);
    Task SetMetaAsync(string key, string? value, CancellationToken cancellationToken);

    /// <summary>Writes changes from the cloud, their versions and the new cursor in one transaction, without queueing them.</summary>
    Task ApplyRemoteAsync(TodoChangeSet changes, IReadOnlyCollection<SyncVersion> versions, string? cursor, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxItem>> OutboxAsync(CancellationToken cancellationToken);
    Task<int> PendingCountAsync(CancellationToken cancellationToken);
    Task MarkAttemptedAsync(IReadOnlyCollection<long> seqs, CancellationToken cancellationToken);

    /// <summary>Removes the outbox row if its mutation id is unchanged, and records the new server version.</summary>
    Task CompleteAsync(OutboxItem item, SyncVersion? newVersion, CancellationToken cancellationToken);

    Task EnqueueAsync(string entityType, string entityId, bool deleted, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<(string Type, string Id), SyncVersion>> VersionsAsync(CancellationToken cancellationToken);
    Task AddIssueAsync(string entityType, string entityId, string title, string message, DateTimeOffset at, CancellationToken cancellationToken, string? copyId = null);
    Task<IReadOnlyList<SyncIssue>> IssuesAsync(CancellationToken cancellationToken);
    Task DismissIssueAsync(long id, CancellationToken cancellationToken);
}

/// <summary>Where a file attached on this PC is, for Cloud Sync (Dev-only spec U9; same meanings on every client).</summary>
public enum TaskFileTransferState { Queued, Synced, Failed, KeptOnThisPc }

public sealed record TaskFileTransfer(TaskFileTransferState State, string? Error);
