using System.Globalization;
using System.Text.Json;
using Alondra1.Core.Cloud;
using Alondra1.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Alondra1.Persistence;

/// <summary>
/// Local to-do storage in its own SQLite file (todo.db) next to the Personal mail database. Entities are stored as
/// versioned JSON documents with indexed key columns. Every change set is one transaction.
/// </summary>
public sealed partial class SqliteTaskStore : ITaskStore, ICloudSyncStore, IAsyncDisposable
{
    /// <summary>Schema version written by this build. Opening a newer file is refused without changing it.</summary>
    public const int SchemaVersion = 5;

    private static readonly JsonSerializerOptions Json = TodoTransfer.CreateOptions();
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteTaskStore(string databasePath) => _path = databasePath;

    public string DatabasePath => _path;

    /// <summary>Path of the copy made before the last migration, if one was needed.</summary>
    public string? LastMigrationBackup { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default, int targetVersion = SchemaVersion)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existed = File.Exists(_path) && new FileInfo(_path).Length > 0;
            var connection = Open(_path, SqliteOpenMode.ReadWriteCreate);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await UserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > SchemaVersion)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new TodoStorageException($"The to-do database was written by a newer Alondra1 (schema {version}). This version supports {SchemaVersion}. The file was not changed.");
            }

            if (existed && version > 0 && version < targetVersion)
            {
                // Protect against data loss during upgrades: keep a consistent copy of the old file.
                LastMigrationBackup = _path + $".pre-v{targetVersion}.bak";
                await using (var backup = Open(LastMigrationBackup, SqliteOpenMode.ReadWriteCreate))
                {
                    await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
                    connection.BackupDatabase(backup);
                }
            }

            await MigrateAsync(connection, version, targetVersion, cancellationToken).ConfigureAwait(false);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _connection = connection;
            Recording = targetVersion >= 3 && await ReadMetaAsync(connection, null, MetaRecording, cancellationToken).ConfigureAwait(false) == "1";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TodoData> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            var data = new TodoData
            {
                Groups = await ReadAllAsync<ListGroup>(connection, "SELECT json FROM list_groups", cancellationToken).ConfigureAwait(false),
                Lists = await ReadAllAsync<TaskList>(connection, "SELECT json FROM task_lists", cancellationToken).ConfigureAwait(false),
                Tasks = await ReadAllAsync<TodoTask>(connection, "SELECT json FROM tasks", cancellationToken).ConfigureAwait(false),
                History = await ReadAllAsync<CompletionRecord>(connection, "SELECT json FROM completion_history", cancellationToken).ConfigureAwait(false),
            };
            return data;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync(TodoChangeSet changes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(connection, tx, changes, cancellationToken).ConfigureAwait(false);
            if (Recording)
                await RecordAsync(connection, tx, changes, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAllAsync(TodoData data, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM tasks; DELETE FROM task_lists; DELETE FROM list_groups; DELETE FROM completion_history;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var changes = new TodoChangeSet();
            changes.UpsertGroups.AddRange(data.Groups);
            changes.UpsertLists.AddRange(data.Lists);
            changes.UpsertTasks.AddRange(data.Tasks);
            changes.UpsertHistory.AddRange(data.History);
            await WriteAsync(connection, tx, changes, cancellationToken).ConfigureAwait(false);
            if (Recording)
            {
                // A replace (import, restore) is a local change too: everything is queued; removed items as deletes.
                await RecordAsync(connection, tx, changes, cancellationToken).ConfigureAwait(false);
                await ExecAsync(connection, tx, """
                    INSERT INTO sync_outbox(entity_type, entity_id, mutation_id, attempted, deleted, created_utc)
                    SELECT s.entity_type, s.entity_id, lower(hex(randomblob(16))), 0, 1, $now FROM sync_state s
                    WHERE (s.entity_type = 'task' AND s.entity_id NOT IN (SELECT id FROM tasks))
                       OR (s.entity_type = 'list' AND s.entity_id NOT IN (SELECT id FROM task_lists))
                       OR (s.entity_type = 'group' AND s.entity_id NOT IN (SELECT id FROM list_groups))
                    ON CONFLICT(entity_type, entity_id) DO UPDATE SET deleted = 1, attempted = 0,
                      mutation_id = CASE WHEN sync_outbox.attempted = 1 THEN excluded.mutation_id ELSE sync_outbox.mutation_id END
                    """, cancellationToken, ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TaskPreferences> LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT json FROM preferences WHERE key = 'todo'";
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json)
            {
                try
                {
                    return JsonSerializer.Deserialize<TaskPreferences>(json, Json) ?? new TaskPreferences();
                }
                catch (JsonException)
                {
                    return new TaskPreferences();
                }
            }

            return new TaskPreferences();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePreferencesAsync(TaskPreferences preferences, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "INSERT INTO preferences(key, json) VALUES ('todo', $json) ON CONFLICT(key) DO UPDATE SET json = excluded.json";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preferences, Json));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes a consistent copy of the database using SQLite's online backup.</summary>
    public async Task BackupToAsync(string destination, CancellationToken cancellationToken = default)
    {
        var temporary = destination + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            var target = Open(temporary, SqliteOpenMode.ReadWriteCreate);
            await using (target.ConfigureAwait(false))
            {
                await target.OpenAsync(cancellationToken).ConfigureAwait(false);
                Require().BackupDatabase(target);
            }
        }
        finally
        {
            _gate.Release();
        }

        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>
    /// Reads a backup file without changing it: the file is copied to a temporary location, checked, and migrated there.
    /// </summary>
    public static async Task<TodoData> ReadBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
            throw new TodoStorageException("The backup file does not exist.");
        var header = new byte[16];
        await using (var stream = File.OpenRead(backupPath))
        {
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) < 16 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                throw new TodoStorageException("The file is not a Alondra1 to-do backup.");
        }

        var scratch = Path.Combine(Path.GetTempPath(), "alondra1-restore-" + Guid.NewGuid().ToString("n") + ".db");
        File.Copy(backupPath, scratch);
        try
        {
            var probe = Open(scratch, SqliteOpenMode.ReadWrite);
            await using (probe.ConfigureAwait(false))
            {
                await probe.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var check = probe.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                var result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new TodoStorageException("The backup file is damaged: " + result);
                check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('tasks', 'task_lists')";
                if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 2)
                    throw new TodoStorageException("The file is not a Alondra1 to-do backup.");
            }

            var store = new SqliteTaskStore(scratch);
            await using (store.ConfigureAwait(false))
            {
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(scratch);
            TryDelete(scratch + ".pre-v" + SchemaVersion + ".bak");
        }
    }

    public async Task<long> CountTasksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT count(*) FROM tasks";
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            var connection = _connection;
            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------- Cloud Sync bookkeeping (ICloudSyncStore) ----------

    private const string MetaRecording = "recording";

    public bool Recording { get; private set; }

    private static async Task<string?> ReadMetaAsync(SqliteConnection connection, SqliteTransaction? tx, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT value FROM sync_meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static Task WriteMetaAsync(SqliteConnection connection, SqliteTransaction tx, string key, string? value, CancellationToken cancellationToken) =>
        value is null
            ? ExecAsync(connection, tx, "DELETE FROM sync_meta WHERE key = $key", cancellationToken, ("$key", key))
            : ExecAsync(connection, tx, "INSERT INTO sync_meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                cancellationToken, ("$key", key), ("$value", value));

    private static Task EnqueueAsync(SqliteConnection connection, SqliteTransaction tx, string type, string id, bool deleted, CancellationToken cancellationToken) =>
        // Same mutation id while unsent; a new one if the item changed after a send attempt (so an older server receipt
        // can never stand for a newer edit).
        ExecAsync(connection, tx, """
            INSERT INTO sync_outbox(entity_type, entity_id, mutation_id, attempted, deleted, created_utc)
            VALUES ($type, $id, lower(hex(randomblob(16))), 0, $deleted, $now)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET deleted = excluded.deleted, attempted = 0,
              mutation_id = CASE WHEN sync_outbox.attempted = 1 THEN excluded.mutation_id ELSE sync_outbox.mutation_id END
            """, cancellationToken, ("$type", type), ("$id", id), ("$deleted", deleted ? 1 : 0),
            ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

    private static async Task RecordAsync(SqliteConnection connection, SqliteTransaction tx, TodoChangeSet changes, CancellationToken cancellationToken)
    {
        foreach (var item in changes.UpsertGroups)
            await EnqueueAsync(connection, tx, CloudKinds.Group, item.Id, false, cancellationToken).ConfigureAwait(false);
        foreach (var item in changes.UpsertLists)
            await EnqueueAsync(connection, tx, CloudKinds.List, item.Id, false, cancellationToken).ConfigureAwait(false);
        foreach (var item in changes.UpsertTasks)
            await EnqueueAsync(connection, tx, CloudKinds.Task, item.Id, false, cancellationToken).ConfigureAwait(false);
        foreach (var id in changes.DeleteGroups)
            await EnqueueAsync(connection, tx, CloudKinds.Group, id, true, cancellationToken).ConfigureAwait(false);
        foreach (var id in changes.DeleteLists)
            await EnqueueAsync(connection, tx, CloudKinds.List, id, true, cancellationToken).ConfigureAwait(false);
        foreach (var id in changes.DeleteTasks)
            await EnqueueAsync(connection, tx, CloudKinds.Task, id, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> LockedAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(Require()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task InTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> work, CancellationToken cancellationToken) =>
        LockedAsync(async connection =>
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await work(connection, tx).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task SetRecordingAsync(bool recording, CancellationToken cancellationToken)
    {
        await InTransactionAsync((c, tx) => WriteMetaAsync(c, tx, MetaRecording, recording ? "1" : null, cancellationToken), cancellationToken).ConfigureAwait(false);
        Recording = recording;
    }

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) =>
        LockedAsync(c => ReadMetaAsync(c, null, key, cancellationToken), cancellationToken);

    public Task SetMetaAsync(string key, string? value, CancellationToken cancellationToken) =>
        InTransactionAsync((c, tx) => WriteMetaAsync(c, tx, key, value, cancellationToken), cancellationToken);

    public Task ApplyRemoteAsync(TodoChangeSet changes, IReadOnlyCollection<SyncVersion> versions, string? cursor, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            await WriteAsync(c, tx, changes, cancellationToken).ConfigureAwait(false);
            foreach (var v in versions)
                await PutVersionAsync(c, tx, v, cancellationToken).ConfigureAwait(false);
            if (cursor is not null)
                await WriteMetaAsync(c, tx, "cursor", cursor, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static Task PutVersionAsync(SqliteConnection c, SqliteTransaction tx, SyncVersion v, CancellationToken cancellationToken) =>
        ExecAsync(c, tx, """
            INSERT INTO sync_state(entity_type, entity_id, server_version, server_json) VALUES ($type, $id, $version, $json)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET server_version = excluded.server_version, server_json = excluded.server_json
            """, cancellationToken, ("$type", v.EntityType), ("$id", v.EntityId), ("$version", v.Version), ("$json", v.ServerJson));

    public Task<IReadOnlyList<OutboxItem>> OutboxAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<OutboxItem>>(async c =>
        {
            var list = new List<OutboxItem>();
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT seq, entity_type, entity_id, mutation_id, attempted, deleted FROM sync_outbox ORDER BY seq";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(new OutboxItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, reader.GetInt64(5) == 1));
            return list;
        }, cancellationToken);

    public Task<int> PendingCountAsync(CancellationToken cancellationToken) =>
        LockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sync_outbox";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task MarkAttemptedAsync(IReadOnlyCollection<long> seqs, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            foreach (var seq in seqs)
                await ExecAsync(c, tx, "UPDATE sync_outbox SET attempted = 1 WHERE seq = $seq", cancellationToken, ("$seq", seq)).ConfigureAwait(false);
        }, cancellationToken);

    public Task CompleteAsync(OutboxItem item, SyncVersion? newVersion, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            await ExecAsync(c, tx, "DELETE FROM sync_outbox WHERE entity_type = $type AND entity_id = $id AND mutation_id = $mutation", cancellationToken,
                ("$type", item.EntityType), ("$id", item.EntityId), ("$mutation", item.MutationId)).ConfigureAwait(false);
            if (newVersion is not null)
                await PutVersionAsync(c, tx, newVersion, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task EnqueueAsync(string entityType, string entityId, bool deleted, CancellationToken cancellationToken) =>
        InTransactionAsync((c, tx) => EnqueueAsync(c, tx, entityType, entityId, deleted, cancellationToken), cancellationToken);

    public Task<IReadOnlyDictionary<(string Type, string Id), SyncVersion>> VersionsAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyDictionary<(string Type, string Id), SyncVersion>>(async c =>
        {
            var map = new Dictionary<(string Type, string Id), SyncVersion>();
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT entity_type, entity_id, server_version, server_json FROM sync_state";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var v = new SyncVersion(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3));
                map[(v.EntityType, v.EntityId)] = v;
            }
            return map;
        }, cancellationToken);

    public Task AddIssueAsync(string entityType, string entityId, string title, string message, DateTimeOffset at, CancellationToken cancellationToken, string? copyId = null) =>
        InTransactionAsync((c, tx) => ExecAsync(c, tx, "INSERT INTO sync_issues(entity_type, entity_id, title, message, created_utc, copy_id) VALUES ($type, $id, $title, $message, $at, $copy)",
            cancellationToken, ("$type", entityType), ("$id", entityId), ("$title", title), ("$message", message), ("$at", at.ToString("O", CultureInfo.InvariantCulture)),
            ("$copy", (object?)copyId ?? DBNull.Value)), cancellationToken);

    public Task<IReadOnlyList<SyncIssue>> IssuesAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<SyncIssue>>(async c =>
        {
            var list = new List<SyncIssue>();
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT id, entity_type, entity_id, title, message, created_utc, copy_id FROM sync_issues ORDER BY id DESC LIMIT 100";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(new SyncIssue(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.IsDBNull(6) ? null : reader.GetString(6)));
            return list;
        }, cancellationToken);

    public Task DismissIssueAsync(long id, CancellationToken cancellationToken) =>
        InTransactionAsync((c, tx) => ExecAsync(c, tx, "DELETE FROM sync_issues WHERE id = $id", cancellationToken, ("$id", id)), cancellationToken);

    // ---------- Schema and migrations ----------

    /// <summary>Ordered migrations. Each runs in its own transaction and sets user_version when it succeeds.</summary>
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS list_groups (
              id TEXT PRIMARY KEY,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_lists (
              id TEXT PRIMARY KEY,
              group_id TEXT,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tasks (
              id TEXT PRIMARY KEY,
              list_id TEXT NOT NULL,
              json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_list ON tasks(list_id);
            """),
        (2, """
            ALTER TABLE tasks ADD COLUMN due_date TEXT;
            ALTER TABLE tasks ADD COLUMN completed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE tasks ADD COLUMN modified_utc TEXT NOT NULL DEFAULT '';
            UPDATE tasks SET
              due_date = json_extract(json, '$.dueDate'),
              completed = coalesce(json_extract(json, '$.completed'), 0),
              modified_utc = coalesce(json_extract(json, '$.modifiedUtc'), '');
            CREATE INDEX IF NOT EXISTS ix_tasks_due ON tasks(due_date) WHERE completed = 0;
            CREATE TABLE IF NOT EXISTS completion_history (
              id TEXT PRIMARY KEY,
              task_id TEXT NOT NULL,
              completed_utc TEXT NOT NULL,
              json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_completed ON completion_history(completed_utc);
            CREATE TABLE IF NOT EXISTS preferences (
              key TEXT PRIMARY KEY,
              json TEXT NOT NULL
            );
            """),
        (3, """
            CREATE TABLE IF NOT EXISTS sync_outbox (
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              entity_type TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              mutation_id TEXT NOT NULL,
              attempted INTEGER NOT NULL DEFAULT 0,
              deleted INTEGER NOT NULL DEFAULT 0,
              created_utc TEXT NOT NULL,
              UNIQUE(entity_type, entity_id)
            );
            CREATE TABLE IF NOT EXISTS sync_state (
              entity_type TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              server_version INTEGER NOT NULL,
              server_json TEXT,
              PRIMARY KEY(entity_type, entity_id)
            );
            CREATE TABLE IF NOT EXISTS sync_meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_issues (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              entity_type TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              title TEXT NOT NULL,
              message TEXT NOT NULL,
              created_utc TEXT NOT NULL
            );
            """),
        (4, """
            CREATE TABLE IF NOT EXISTS notes (
              id TEXT PRIMARY KEY,
              folder_id TEXT,
              title TEXT NOT NULL DEFAULT '',
              preview TEXT NOT NULL DEFAULT '',
              favorite INTEGER NOT NULL DEFAULT 0,
              pinned INTEGER NOT NULL DEFAULT 0,
              archived_utc TEXT,
              trashed_utc TEXT,
              created_utc TEXT NOT NULL,
              modified_utc TEXT NOT NULL,
              first_asset TEXT,
              link_count INTEGER NOT NULL DEFAULT 0,
              json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_notes_folder ON notes(folder_id);
            CREATE INDEX IF NOT EXISTS ix_notes_modified ON notes(modified_utc);
            CREATE TABLE IF NOT EXISTS note_folders (
              id TEXT PRIMARY KEY,
              parent_id TEXT,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS note_assets (
              sha256 TEXT PRIMARY KEY,
              mime_type TEXT NOT NULL,
              bytes INTEGER NOT NULL,
              width INTEGER,
              height INTEGER,
              created_utc TEXT NOT NULL,
              uploaded INTEGER NOT NULL DEFAULT 0
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(id UNINDEXED, title, body, tokenize = 'unicode61 remove_diacritics 2');
            """),
        // A note conflict remembers its conflict copy, so the user can compare the two versions and resolve them.
        (5, "ALTER TABLE sync_issues ADD COLUMN copy_id TEXT;"),
    ];

    private static async Task MigrateAsync(SqliteConnection connection, long from, int to, CancellationToken cancellationToken)
    {
        foreach (var (version, sql) in Migrations)
        {
            if (version <= from || version > to)
                continue;
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql + $"\nPRAGMA user_version = {version};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> UserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    // ---------- Reads and writes ----------

    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction tx, TodoChangeSet changes, CancellationToken cancellationToken)
    {
        foreach (var id in changes.DeleteTasks)
            await ExecAsync(connection, tx, "DELETE FROM tasks WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteLists)
            await ExecAsync(connection, tx, "DELETE FROM task_lists WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteGroups)
            await ExecAsync(connection, tx, "DELETE FROM list_groups WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteHistory)
            await ExecAsync(connection, tx, "DELETE FROM completion_history WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var group in changes.UpsertGroups)
        {
            await ExecAsync(connection, tx, "INSERT INTO list_groups(id, json) VALUES ($id, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
                cancellationToken, ("$id", group.Id), ("$json", JsonSerializer.Serialize(group, Json))).ConfigureAwait(false);
        }

        foreach (var list in changes.UpsertLists)
        {
            await ExecAsync(connection, tx, "INSERT INTO task_lists(id, group_id, json) VALUES ($id, $group, $json) ON CONFLICT(id) DO UPDATE SET group_id = excluded.group_id, json = excluded.json",
                cancellationToken, ("$id", list.Id), ("$group", list.GroupId), ("$json", JsonSerializer.Serialize(list, Json))).ConfigureAwait(false);
        }

        foreach (var task in changes.UpsertTasks)
        {
            await ExecAsync(connection, tx, """
                INSERT INTO tasks(id, list_id, json, due_date, completed, modified_utc) VALUES ($id, $list, $json, $due, $done, $modified)
                ON CONFLICT(id) DO UPDATE SET list_id = excluded.list_id, json = excluded.json, due_date = excluded.due_date,
                  completed = excluded.completed, modified_utc = excluded.modified_utc
                """,
                cancellationToken,
                ("$id", task.Id), ("$list", task.ListId), ("$json", JsonSerializer.Serialize(task, Json)),
                ("$due", task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$done", task.Completed ? 1 : 0),
                ("$modified", task.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }

        foreach (var record in changes.UpsertHistory)
        {
            await ExecAsync(connection, tx, "INSERT INTO completion_history(id, task_id, completed_utc, json) VALUES ($id, $task, $at, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
                cancellationToken, ("$id", record.Id), ("$task", record.TaskId), ("$at", record.CompletedUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$json", JsonSerializer.Serialize(record, Json))).ConfigureAwait(false);
        }
    }

    private static async Task ExecAsync(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<T>> ReadAllAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var list = new List<T>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            try
            {
                if (JsonSerializer.Deserialize<T>(json, Json) is { } item)
                    list.Add(item);
            }
            catch (JsonException)
            {
                // A damaged row is skipped rather than blocking every other task from loading.
            }
        }

        return list;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());

    private SqliteConnection Require() => _connection ?? throw new TodoStorageException("The to-do store is not open.");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
