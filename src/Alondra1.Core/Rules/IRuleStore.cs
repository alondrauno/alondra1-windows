namespace Alondra1.Core.Rules;

/// <summary>One run of the rules on one message or draft (Settings → Mail → Rule activity). No subjects or bodies.</summary>
public sealed class RuleExecution
{
    /// <summary>Unique per message arrival or draft revision and direction: the same message is never processed twice.</summary>
    public string Key { get; set; } = "";
    public string AccountId { get; set; } = "";
    public RuleDirection Direction { get; set; }

    /// <summary>pending, running, completed, partial, failed, unknown (unchecked).</summary>
    public string Status { get; set; } = "pending";

    /// <summary>What happened, in words without message content: "Newsletters: tagged, archived".</summary>
    public string Summary { get; set; } = "";

    /// <summary>The rules and revisions used ("id:revision,...").</summary>
    public string Revisions { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>A message moved out of the way by the blocklist; restorable.</summary>
public sealed class QuarantinedMail
{
    /// <summary>The stable mail reference (TagRules.MailRef): it follows the message between folders.</summary>
    public string Ref { get; set; } = "";
    public string AccountId { get; set; } = "";

    /// <summary>Where the message was when it was blocked (Restore puts it back there).</summary>
    public string OriginalFolderId { get; set; } = "";

    /// <summary>Where it is now (the Junk folder), with its uid there; null when it was left in place.</summary>
    public string? FolderId { get; set; }
    public long Uid { get; set; }
    public string EntryId { get; set; } = "";

    /// <summary>The reason in words: "Blocked sender: news@example.com".</summary>
    public string Reason { get; set; } = "";
    public DateTimeOffset BlockedUtc { get; set; }
}

/// <summary>Rules, block entries, the execution ledger, quarantine and exemptions, kept with the mail on this PC.</summary>
public interface IRuleStore
{
    Task<IReadOnlyList<MailRule>> LoadRulesAsync(CancellationToken cancellationToken);
    Task SaveRuleAsync(MailRule rule, CancellationToken cancellationToken);
    Task DeleteRuleAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<BlockEntry>> LoadBlocksAsync(CancellationToken cancellationToken);
    Task SaveBlockAsync(BlockEntry entry, CancellationToken cancellationToken);
    Task DeleteBlockAsync(string id, CancellationToken cancellationToken);

    /// <summary>Records the start of an execution; false when this key was already processed (a duplicate or a sync echo).</summary>
    Task<bool> TryBeginExecutionAsync(RuleExecution execution, CancellationToken cancellationToken);
    Task UpdateExecutionAsync(RuleExecution execution, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuleExecution>> ListExecutionsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Forgets activity older than <paramref name="before"/>; the keys stay as idempotency markers for 180 days.</summary>
    Task PruneExecutionsAsync(DateTimeOffset before, CancellationToken cancellationToken);
    Task SaveQuarantineAsync(QuarantinedMail item, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuarantinedMail>> ListQuarantineAsync(CancellationToken cancellationToken);
    Task DeleteQuarantineAsync(string mailRef, CancellationToken cancellationToken);

    /// <summary>Restore alone: this message is not blocked again when it is seen again (until <paramref name="until"/>).</summary>
    Task AddExemptionAsync(string mailRef, DateTimeOffset until, CancellationToken cancellationToken);
    Task<bool> IsExemptAsync(string mailRef, DateTimeOffset now, CancellationToken cancellationToken);
}
