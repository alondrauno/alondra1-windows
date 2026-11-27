using Alondra1.Core.Config;

namespace Alondra1.Core.Mail;

/// <summary>One message of a bulk move: the account it belongs to and that account's own destination folder.</summary>
public sealed record BulkMoveStep(StoredMessage Message, AccountConfig Account, string Destination);

/// <summary>A message that is not moved, and why (shown to the user).</summary>
public sealed record BulkMoveSkip(StoredMessage Message, string Reason);

public sealed record BulkMovePlan(IReadOnlyList<BulkMoveStep> Steps, IReadOnlyList<BulkMoveSkip> Skipped);

/// <summary>
/// Archive and Delete for a selection that may span accounts (Mail UI spec §3.2, T05): every message goes to its own
/// account's folder (never another account's, never a guessed name), and messages that can't be moved are listed with
/// the reason instead of being dropped silently.
/// </summary>
public static class BulkActions
{
    public static BulkMovePlan Plan(IEnumerable<StoredMessage> messages, IReadOnlyList<AccountConfig> accounts, FolderRole role)
    {
        var steps = new List<BulkMoveStep>();
        var skipped = new List<BulkMoveSkip>();
        foreach (var message in messages.DistinctBy(m => (m.AccountId, m.FolderId, m.Uid)))
        {
            if (accounts.FirstOrDefault(a => a.Id == message.AccountId) is not { } account)
            {
                skipped.Add(new(message, "its account was removed"));
                continue;
            }

            var name = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Address : account.DisplayName;
            if (SpecialFolders.TryResolve(account, role) is not { } destination)
            {
                skipped.Add(new(message, $"{name} has no {role} folder on its server"));
                continue;
            }

            if (destination == message.FolderId)
            {
                skipped.Add(new(message, $"it is already in {role}"));
                continue;
            }

            steps.Add(new(message, account, destination));
        }

        return new BulkMovePlan(steps, skipped);
    }

    /// <summary>"Archived 4 of 5 messages. Not archived: 1 — Personal has no Archive folder on its server."</summary>
    public static string Summary(string verb, int total, int done, IEnumerable<string> reasons)
    {
        var grouped = reasons.GroupBy(r => r).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()}: {g.Key}").ToList();
        var head = done == total ? $"{verb} {total} {(total == 1 ? "message" : "messages")}." : $"{verb} {done} of {total} messages.";
        return grouped.Count == 0 ? head : $"{head} Not {verb.ToLowerInvariant()}: {string.Join("; ", grouped)}.";
    }
}
