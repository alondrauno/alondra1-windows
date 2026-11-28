using System.Globalization;
using System.Text.Json;
using Alondra1.Core.Cloud;
using Alondra1.Core.Tags;
using Alondra1.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Alondra1.Persistence;

/// <summary>
/// Tags and tag links live in todo.db (schema 6) next to tasks and notes, so they share the backup, the Cloud Sync
/// outbox and one transaction per change. mail_tag_targets is local only: where a tagged message is on this PC.
/// </summary>
public sealed partial class SqliteTaskStore : ITagStore
{
    private static readonly JsonSerializerOptions TagJson = TodoTransfer.CreateOptions();

    public Task<TagSnapshot> LoadTagsAsync(CancellationToken cancellationToken) =>
        LockedAsync(async c =>
        {
            var snapshot = new TagSnapshot();
            snapshot.Tags.AddRange(await ReadAllAsync<Tag>(c, "SELECT json FROM tags", cancellationToken).ConfigureAwait(false));
            snapshot.Links.AddRange(await ReadAllAsync<TagLink>(c, "SELECT json FROM tag_links", cancellationToken).ConfigureAwait(false));
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT ref, account_id, folder_id, uid, subject, received_utc FROM mail_tag_targets";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshot.MailTargets.Add(new MailTagTarget(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            }

            return snapshot;
        }, cancellationToken);

    public Task ApplyTagsAsync(TagChangeSet changes, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            // Read before writing: whether a deleted link went to the cloud depends on its item type.
            var syncedDeletes = new List<string>();
            foreach (var id in changes.DeleteLinks)
            {
                await using var command = c.CreateCommand();
                command.Transaction = tx;
                command.CommandText = "SELECT object_type FROM tag_links WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string type && type != TagRules.Event)
                    syncedDeletes.Add(id);
            }

            await WriteTagsAsync(c, tx, changes, cancellationToken).ConfigureAwait(false);
            if (Recording)
            {
                foreach (var t in changes.UpsertTags)
                    await EnqueueAsync(c, tx, CloudKinds.Tag, t.Id, false, cancellationToken).ConfigureAwait(false);
                foreach (var l in changes.UpsertLinks.Where(TagRules.IsSynced))
                    await EnqueueAsync(c, tx, CloudKinds.TagLink, l.Id, false, cancellationToken).ConfigureAwait(false);
                foreach (var id in changes.DeleteTags)
                    await EnqueueAsync(c, tx, CloudKinds.Tag, id, true, cancellationToken).ConfigureAwait(false);
                foreach (var id in syncedDeletes)
                    await EnqueueAsync(c, tx, CloudKinds.TagLink, id, true, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task ApplyRemoteTagsAsync(TagChangeSet changes, IReadOnlyCollection<SyncVersion> versions, CancellationToken cancellationToken) =>
        InTransactionAsync(async (c, tx) =>
        {
            await WriteTagsAsync(c, tx, changes, cancellationToken).ConfigureAwait(false);
            foreach (var v in versions)
                await PutVersionAsync(c, tx, v, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task WriteTagsAsync(SqliteConnection c, SqliteTransaction tx, TagChangeSet changes, CancellationToken cancellationToken)
    {
        foreach (var t in changes.UpsertTags)
        {
            await ExecAsync(c, tx, "INSERT INTO tags(id, name_key, json) VALUES ($id, $key, $json) ON CONFLICT(id) DO UPDATE SET name_key = excluded.name_key, json = excluded.json",
                cancellationToken, ("$id", t.Id), ("$key", TagRules.NameKey(t.Name)), ("$json", JsonSerializer.Serialize(t, TagJson))).ConfigureAwait(false);
        }

        foreach (var l in changes.UpsertLinks)
        {
            await ExecAsync(c, tx, """
                INSERT INTO tag_links(id, tag_id, object_type, object_ref, removed, json) VALUES ($id, $tag, $type, $ref, $removed, $json)
                ON CONFLICT(id) DO UPDATE SET removed = excluded.removed, json = excluded.json
                """, cancellationToken, ("$id", l.Id), ("$tag", l.TagId), ("$type", l.ObjectType), ("$ref", l.ObjectRef), ("$removed", l.Removed ? 1 : 0),
                ("$json", JsonSerializer.Serialize(l, TagJson))).ConfigureAwait(false);
        }

        foreach (var id in changes.DeleteLinks.Concat(changes.DropLinks))
            await ExecAsync(c, tx, "DELETE FROM tag_links WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteTags.Concat(changes.DropTags))
            await ExecAsync(c, tx, "DELETE FROM tags WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);

        // Dropped records are gone locally and in the cloud (or never reached it): forget their sync bookkeeping.
        foreach (var id in changes.DropLinks)
            await ForgetAsync(c, tx, CloudKinds.TagLink, id, cancellationToken).ConfigureAwait(false);
        foreach (var id in changes.DropTags)
            await ForgetAsync(c, tx, CloudKinds.Tag, id, cancellationToken).ConfigureAwait(false);

        foreach (var m in changes.MailTargets)
        {
            await ExecAsync(c, tx, """
                INSERT INTO mail_tag_targets(ref, account_id, folder_id, uid, subject, received_utc) VALUES ($ref, $account, $folder, $uid, $subject, $received)
                ON CONFLICT(ref) DO UPDATE SET account_id = excluded.account_id, folder_id = excluded.folder_id, uid = excluded.uid,
                  subject = excluded.subject, received_utc = excluded.received_utc
                """, cancellationToken, ("$ref", m.Ref), ("$account", m.AccountId), ("$folder", m.FolderId), ("$uid", m.Uid), ("$subject", m.Subject),
                ("$received", m.ReceivedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }
    }

    private static async Task ForgetAsync(SqliteConnection c, SqliteTransaction tx, string type, string id, CancellationToken cancellationToken)
    {
        await ExecAsync(c, tx, "DELETE FROM sync_state WHERE entity_type = $type AND entity_id = $id", cancellationToken, ("$type", type), ("$id", id)).ConfigureAwait(false);
        await ExecAsync(c, tx, "DELETE FROM sync_outbox WHERE entity_type = $type AND entity_id = $id", cancellationToken, ("$type", type), ("$id", id)).ConfigureAwait(false);
    }
}
