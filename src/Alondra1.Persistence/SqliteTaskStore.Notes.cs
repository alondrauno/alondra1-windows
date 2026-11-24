using System.Globalization;
using System.Text.Json;
using Alondra1.Core.Cloud;
using Alondra1.Core.Notes;
using Alondra1.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Alondra1.Persistence;

/// <summary>
/// Notes, note folders and note pictures live in todo.db next to tasks (schema 4), so they share the backup, the
/// Cloud Sync outbox and one transaction per change. Full-text search uses SQLite FTS5.
/// </summary>
public sealed partial class SqliteTaskStore : ICloudNoteStore
{
    private static readonly JsonSerializerOptions NoteJson = TodoTransfer.CreateOptions();

    public Task<NotesSnapshot> LoadNotesAsync(CancellationToken cancellationToken) =>
        LockedAsync(async c =>
        {
            var snapshot = new NotesSnapshot();
            await using (var command = c.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, folder_id, title, preview, favorite, pinned, archived_utc, trashed_utc, created_utc, modified_utc, first_asset, link_count
                    FROM notes
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    snapshot.Notes.Add(new NoteSummary(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetInt64(4) == 1, reader.GetInt64(5) == 1, Time(reader, 6), Time(reader, 7), Time(reader, 8)!.Value, Time(reader, 9)!.Value,
                        reader.IsDBNull(10) ? null : reader.GetString(10), (int)reader.GetInt64(11)));
                }
            }

            snapshot.Folders.AddRange(await ReadAllAsync<NoteFolder>(c, "SELECT json FROM note_folders", cancellationToken).ConfigureAwait(false));
            return snapshot;
        }, cancellationToken);

    private static DateTimeOffset? Time(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    public Task<Note?> GetNoteAsync(string id, CancellationToken cancellationToken) =>
        LockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT json FROM notes WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json ? JsonSerializer.Deserialize<Note>(json, NoteJson) : null;
        }, cancellationToken);

    public Task ApplyNotesAsync(NoteChangeSet changes, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            await WriteNotesAsync(c, tx, changes, cancellationToken).ConfigureAwait(false);
            if (Recording)
            {
                foreach (var f in changes.UpsertFolders)
                    await EnqueueAsync(c, tx, CloudKinds.NoteFolder, f.Id, false, cancellationToken).ConfigureAwait(false);
                foreach (var n in changes.UpsertNotes)
                    await EnqueueAsync(c, tx, CloudKinds.Note, n.Id, false, cancellationToken).ConfigureAwait(false);
                foreach (var id in changes.DeleteFolders)
                    await EnqueueAsync(c, tx, CloudKinds.NoteFolder, id, true, cancellationToken).ConfigureAwait(false);
                foreach (var id in changes.DeleteNotes)
                    await EnqueueAsync(c, tx, CloudKinds.Note, id, true, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task ApplyRemoteNotesAsync(NoteChangeSet changes, IReadOnlyCollection<SyncVersion> versions, string? cursor, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            await WriteNotesAsync(c, tx, changes, cancellationToken).ConfigureAwait(false);
            foreach (var v in versions)
                await PutVersionAsync(c, tx, v, cancellationToken).ConfigureAwait(false);
            if (cursor is not null)
                await WriteMetaAsync(c, tx, "cursor", cursor, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task WriteNotesAsync(SqliteConnection c, SqliteTransaction tx, NoteChangeSet changes, CancellationToken cancellationToken)
    {
        foreach (var f in changes.UpsertFolders)
        {
            await ExecAsync(c, tx, "INSERT INTO note_folders(id, parent_id, json) VALUES ($id, $parent, $json) ON CONFLICT(id) DO UPDATE SET parent_id = excluded.parent_id, json = excluded.json",
                cancellationToken, ("$id", f.Id), ("$parent", f.ParentId), ("$json", JsonSerializer.Serialize(f, NoteJson))).ConfigureAwait(false);
        }

        foreach (var n in changes.UpsertNotes)
        {
            var s = NoteSummary.From(n);
            await ExecAsync(c, tx, """
                INSERT INTO notes(id, folder_id, title, preview, favorite, pinned, archived_utc, trashed_utc, created_utc, modified_utc, first_asset, link_count, json)
                VALUES ($id, $folder, $title, $preview, $favorite, $pinned, $archived, $trashed, $created, $modified, $asset, $links, $json)
                ON CONFLICT(id) DO UPDATE SET folder_id = excluded.folder_id, title = excluded.title, preview = excluded.preview, favorite = excluded.favorite,
                  pinned = excluded.pinned, archived_utc = excluded.archived_utc, trashed_utc = excluded.trashed_utc, created_utc = excluded.created_utc,
                  modified_utc = excluded.modified_utc, first_asset = excluded.first_asset, link_count = excluded.link_count, json = excluded.json
                """, cancellationToken, ("$id", n.Id), ("$folder", n.FolderId), ("$title", n.Title), ("$preview", s.Preview), ("$favorite", n.Favorite ? 1 : 0),
                ("$pinned", n.Pinned ? 1 : 0), ("$archived", Stamp(n.ArchivedUtc)), ("$trashed", Stamp(n.TrashedUtc)), ("$created", Stamp(n.CreatedUtc)),
                ("$modified", Stamp(n.ModifiedUtc)), ("$asset", s.FirstAsset), ("$links", n.Links.Count), ("$json", JsonSerializer.Serialize(n, NoteJson))).ConfigureAwait(false);
            await ExecAsync(c, tx, "DELETE FROM notes_fts WHERE id = $id", cancellationToken, ("$id", n.Id)).ConfigureAwait(false);
            await ExecAsync(c, tx, "INSERT INTO notes_fts(id, title, body) VALUES ($id, $title, $body)", cancellationToken,
                ("$id", n.Id), ("$title", n.Title), ("$body", n.Body)).ConfigureAwait(false);
        }

        foreach (var id in changes.DeleteNotes)
        {
            await ExecAsync(c, tx, "DELETE FROM notes WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
            await ExecAsync(c, tx, "DELETE FROM notes_fts WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        }

        foreach (var id in changes.DeleteFolders)
            await ExecAsync(c, tx, "DELETE FROM note_folders WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
    }

    private static string? Stamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Every word must match (as a prefix), best matches first. Words are quoted, so FTS operators typed by the user
    /// (AND, NEAR, *, :) are searched for literally instead of being interpreted.
    /// </summary>
    public Task<IReadOnlyList<string>> SearchNotesAsync(string query, int limit, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<string>>(async c =>
        {
            var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(12).Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*").ToList();
            var ids = new List<string>();
            if (words.Count == 0)
                return ids;
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT id FROM notes_fts WHERE notes_fts MATCH $q ORDER BY bm25(notes_fts, 0, 5.0, 1.0) LIMIT $limit";
            command.Parameters.AddWithValue("$q", string.Join(" AND ", words));
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
            return ids;
        }, cancellationToken);

    public Task<NoteAsset?> GetAssetAsync(string sha256, CancellationToken cancellationToken) =>
        LockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT sha256, mime_type, bytes, width, height, created_utc, uploaded FROM note_assets WHERE sha256 = $sha";
            command.Parameters.AddWithValue("$sha", sha256);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            return new NoteAsset(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetInt64(6) == 1);
        }, cancellationToken);

    public Task SaveAssetAsync(NoteAsset asset, CancellationToken cancellationToken) =>
        InTransactionAsync((c, tx) => ExecAsync(c, tx, """
            INSERT INTO note_assets(sha256, mime_type, bytes, width, height, created_utc, uploaded) VALUES ($sha, $mime, $bytes, $w, $h, $created, $uploaded)
            ON CONFLICT(sha256) DO UPDATE SET mime_type = excluded.mime_type, bytes = excluded.bytes, width = excluded.width, height = excluded.height
            """, cancellationToken, ("$sha", asset.Sha256), ("$mime", asset.MimeType), ("$bytes", asset.Bytes), ("$w", asset.Width), ("$h", asset.Height),
            ("$created", Stamp(asset.CreatedUtc)), ("$uploaded", asset.Uploaded ? 1 : 0)), cancellationToken);

    public Task MarkAssetUploadedAsync(string sha256, CancellationToken cancellationToken) =>
        InTransactionAsync((c, tx) => ExecAsync(c, tx, "UPDATE note_assets SET uploaded = 1 WHERE sha256 = $sha", cancellationToken, ("$sha", sha256)), cancellationToken);
}
