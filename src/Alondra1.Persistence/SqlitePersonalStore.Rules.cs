using System.Globalization;
using System.Text.Json;
using Alondra1.Core.Rules;
using Microsoft.Data.Sqlite;

namespace Alondra1.Persistence;

/// <summary>
/// Mail rules (FEATURES_EXTENSION Part B §6) live in personal.db next to the mail they act on: additive tables, created
/// when missing, never touching messages. Rules and block entries are stored as JSON (older builds ignore the tables).
/// </summary>
public sealed partial class SqlitePersonalStore : IRuleStore
{
    private static readonly JsonSerializerOptions RuleJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private const string RuleSchema = """
        CREATE TABLE IF NOT EXISTS mail_rules (
          id TEXT PRIMARY KEY,
          json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS mail_block_entries (
          id TEXT PRIMARY KEY,
          json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS mail_rule_executions (
          key TEXT PRIMARY KEY,
          account_id TEXT NOT NULL,
          direction TEXT NOT NULL,
          status TEXT NOT NULL,
          summary TEXT NOT NULL DEFAULT '',
          revisions TEXT NOT NULL DEFAULT '',
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rule_executions_created ON mail_rule_executions(created_utc);
        CREATE TABLE IF NOT EXISTS mail_quarantine (
          ref TEXT PRIMARY KEY,
          json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS mail_block_exemptions (
          ref TEXT PRIMARY KEY,
          until_utc TEXT NOT NULL
        );
        """;

    private async Task<T> RuleLockedAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken)
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

    private Task RuleExecAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) =>
        RuleLockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private Task<List<T>> RuleJsonListAsync<T>(string table, CancellationToken cancellationToken) =>
        RuleLockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = $"SELECT json FROM {table}";
            var list = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (JsonSerializer.Deserialize<T>(reader.GetString(0), RuleJson) is { } item)
                    list.Add(item);
            }

            return list;
        }, cancellationToken);

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public async Task<IReadOnlyList<MailRule>> LoadRulesAsync(CancellationToken cancellationToken) =>
        await RuleJsonListAsync<MailRule>("mail_rules", cancellationToken).ConfigureAwait(false);

    public Task SaveRuleAsync(MailRule rule, CancellationToken cancellationToken) =>
        RuleExecAsync("INSERT INTO mail_rules(id, json) VALUES ($id, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json", cancellationToken,
            ("$id", rule.Id), ("$json", JsonSerializer.Serialize(rule, RuleJson)));

    public Task DeleteRuleAsync(string id, CancellationToken cancellationToken) =>
        RuleExecAsync("DELETE FROM mail_rules WHERE id = $id", cancellationToken, ("$id", id));

    public async Task<IReadOnlyList<BlockEntry>> LoadBlocksAsync(CancellationToken cancellationToken) =>
        await RuleJsonListAsync<BlockEntry>("mail_block_entries", cancellationToken).ConfigureAwait(false);

    public Task SaveBlockAsync(BlockEntry entry, CancellationToken cancellationToken) =>
        RuleExecAsync("INSERT INTO mail_block_entries(id, json) VALUES ($id, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json", cancellationToken,
            ("$id", entry.Id), ("$json", JsonSerializer.Serialize(entry, RuleJson)));

    public Task DeleteBlockAsync(string id, CancellationToken cancellationToken) =>
        RuleExecAsync("DELETE FROM mail_block_entries WHERE id = $id", cancellationToken, ("$id", id));

    public async Task<bool> TryBeginExecutionAsync(RuleExecution e, CancellationToken cancellationToken)
    {
        var inserted = await RuleLockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO mail_rule_executions(key, account_id, direction, status, summary, revisions, created_utc, updated_utc)
                VALUES ($key, $account, $direction, $status, $summary, $revisions, $created, $updated)
                """;
            command.Parameters.AddWithValue("$key", e.Key);
            command.Parameters.AddWithValue("$account", e.AccountId);
            command.Parameters.AddWithValue("$direction", e.Direction.ToString());
            command.Parameters.AddWithValue("$status", e.Status);
            command.Parameters.AddWithValue("$summary", e.Summary);
            command.Parameters.AddWithValue("$revisions", e.Revisions);
            command.Parameters.AddWithValue("$created", Utc(e.CreatedUtc));
            command.Parameters.AddWithValue("$updated", Utc(e.UpdatedUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    public Task UpdateExecutionAsync(RuleExecution e, CancellationToken cancellationToken) =>
        RuleExecAsync("UPDATE mail_rule_executions SET status = $status, summary = $summary, revisions = $revisions, updated_utc = $updated WHERE key = $key",
            cancellationToken, ("$key", e.Key), ("$status", e.Status), ("$summary", e.Summary), ("$revisions", e.Revisions), ("$updated", Utc(e.UpdatedUtc)));

    public Task<IReadOnlyList<RuleExecution>> ListExecutionsAsync(int limit, CancellationToken cancellationToken) =>
        RuleLockedAsync<IReadOnlyList<RuleExecution>>(async c =>
        {
            await using var command = c.CreateCommand();
            // Rows pruned of their activity keep only the key (summary '-'): they are not shown.
            command.CommandText = "SELECT key, account_id, direction, status, summary, revisions, created_utc, updated_utc FROM mail_rule_executions WHERE summary <> '-' ORDER BY created_utc DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<RuleExecution>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new RuleExecution
                {
                    Key = reader.GetString(0), AccountId = reader.GetString(1), Direction = Enum.Parse<RuleDirection>(reader.GetString(2)),
                    Status = reader.GetString(3), Summary = reader.GetString(4), Revisions = reader.GetString(5),
                    CreatedUtc = ParseUtc(reader.GetString(6)), UpdatedUtc = ParseUtc(reader.GetString(7)),
                });
            }

            return list;
        }, cancellationToken);

    public async Task PruneExecutionsAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        // Activity older than the retention is hidden (the key stays so a replay of that message is still recognized);
        // markers older than 180 days go entirely.
        await RuleExecAsync("UPDATE mail_rule_executions SET summary = '-', revisions = '' WHERE created_utc < $before AND summary <> '-'", cancellationToken, ("$before", Utc(before))).ConfigureAwait(false);
        await RuleExecAsync("DELETE FROM mail_rule_executions WHERE created_utc < $old", cancellationToken, ("$old", Utc(before.AddDays(-150)))).ConfigureAwait(false);
    }

    public Task SaveQuarantineAsync(QuarantinedMail item, CancellationToken cancellationToken) =>
        RuleExecAsync("INSERT INTO mail_quarantine(ref, json) VALUES ($ref, $json) ON CONFLICT(ref) DO UPDATE SET json = excluded.json", cancellationToken,
            ("$ref", item.Ref), ("$json", JsonSerializer.Serialize(item, RuleJson)));

    public async Task<IReadOnlyList<QuarantinedMail>> ListQuarantineAsync(CancellationToken cancellationToken) =>
        (await RuleJsonListAsync<QuarantinedMail>("mail_quarantine", cancellationToken).ConfigureAwait(false)).OrderByDescending(q => q.BlockedUtc).ToList();

    public Task DeleteQuarantineAsync(string mailRef, CancellationToken cancellationToken) =>
        RuleExecAsync("DELETE FROM mail_quarantine WHERE ref = $ref", cancellationToken, ("$ref", mailRef));

    public Task AddExemptionAsync(string mailRef, DateTimeOffset until, CancellationToken cancellationToken) =>
        RuleExecAsync("INSERT INTO mail_block_exemptions(ref, until_utc) VALUES ($ref, $until) ON CONFLICT(ref) DO UPDATE SET until_utc = excluded.until_utc", cancellationToken,
            ("$ref", mailRef), ("$until", Utc(until)));

    public Task<bool> IsExemptAsync(string mailRef, DateTimeOffset now, CancellationToken cancellationToken) =>
        RuleLockedAsync(async c =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT 1 FROM mail_block_exemptions WHERE ref = $ref AND until_utc > $now";
            command.Parameters.AddWithValue("$ref", mailRef);
            command.Parameters.AddWithValue("$now", Utc(now));
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }, cancellationToken);
}
