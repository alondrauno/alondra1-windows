using System.Text.Json.Serialization;

namespace Alondra1.Core.Rules;

/// <summary>
/// Mail rules and blocked senders &amp; words (FEATURES_EXTENSION Part B §3–§5). Everything is deterministic and runs on
/// this PC: mail stays in the Windows app, so this app is the one authoritative processor for its accounts (no other
/// client or the cloud executes mail rules). No AI, no regular expressions, no scripts, no forwarding.
/// </summary>
public enum RuleDirection
{
    Incoming,
    Outgoing,
}

public enum RuleMatch
{
    All,
    Any,
}

public enum RuleField
{
    From,
    Recipients,
    ReplyTo,
    Subject,
    Body,
    Tag,
    HasAttachments,
    AttachmentName,
    Account,
    Unread,
    Flagged,
}

public enum RuleOperator
{
    Is,
    IsNot,
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,
    Exists,
    DoesNotExist,
    HasTag,
    LacksTag,
}

public enum RuleActionKind
{
    AddTag,
    RemoveTag,
    MarkRead,
    MarkUnread,
    Flag,
    Unflag,
    MoveTo,
    Archive,
    MoveToJunk,
    MoveToTrash,
    Warn,
    Block,
}

public sealed class RuleCondition
{
    public RuleField Field { get; set; }
    public RuleOperator Operator { get; set; }

    /// <summary>The operand (text, tag id, account id); empty for Exists, Unread and similar.</summary>
    public string Value { get; set; } = "";
}

public sealed class RuleAction
{
    public RuleActionKind Kind { get; set; }

    /// <summary>Tag id, folder id, or the warning text (Warn and Block show it with the rule name).</summary>
    public string Value { get; set; } = "";
}

public sealed class MailRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public RuleDirection Direction { get; set; }

    /// <summary>One account, or null for every account the rule can run on.</summary>
    public string? AccountId { get; set; }

    /// <summary>Lower runs first; ties are broken by id (never by row order on screen).</summary>
    public int Priority { get; set; }
    public RuleMatch Match { get; set; } = RuleMatch.All;
    public List<RuleCondition> Conditions { get; set; } = [];
    public List<RuleAction> Actions { get; set; } = [];
    public bool StopProcessing { get; set; }

    /// <summary>The explicit "all messages" choice: a rule without conditions runs only when this is set.</summary>
    public bool AllMessages { get; set; }

    /// <summary>Increases with every saved change; executions and send confirmations record the revision they used.</summary>
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>Why the rule was suspended (its tag was deleted, its folder is gone); null when fine.</summary>
    public string? SuspendedReason { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? LastResult { get; set; }

    [JsonIgnore]
    public bool Active => Enabled && SuspendedReason is null;

    public MailRule Clone() => System.Text.Json.JsonSerializer.Deserialize<MailRule>(System.Text.Json.JsonSerializer.Serialize(this))!;
}

public enum BlockKind
{
    Address,
    Domain,
    SubjectText,
    BodyText,
}

public enum BlockMode
{
    /// <summary>Address and domain: exactly this; text: the whole word or phrase.</summary>
    Exact,

    /// <summary>Domain: this domain and its subdomains (label boundary); text: anywhere, also inside words.</summary>
    Broad,
}

public sealed class BlockEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>One account, or null for all accounts.</summary>
    public string? AccountId { get; set; }
    public BlockKind Kind { get; set; }

    /// <summary>Normalized when saved: a lower-case address, an ASCII (IDNA) domain, or NFC text.</summary>
    public string Value { get; set; } = "";
    public BlockMode Mode { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public int HitCount { get; set; }
    public DateTimeOffset? LastHitUtc { get; set; }
}

/// <summary>Three-valued result: missing data (a body not downloaded yet) is never evidence for a negative condition.</summary>
public enum Truth
{
    False,
    True,
    Unknown,
}

/// <summary>A message as the rules see it. Null means "not known" (distinct from empty).</summary>
public sealed class MessageFacts
{
    public string AccountId { get; init; } = "";
    public string? From { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public string? ReplyTo { get; init; }
    public string Subject { get; init; } = "";

    /// <summary>Visible plain text (at most <see cref="RuleLimits.MaxBodyChars"/>); null when the body isn't downloaded.</summary>
    public string? Body { get; init; }

    /// <summary>The body was longer than what is inspected: a miss in it is unknown, not false.</summary>
    public bool BodyTruncated { get; init; }

    /// <summary>Tag ids on the message, including ones added by earlier rules in the same run.</summary>
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public bool? HasAttachments { get; init; }

    /// <summary>Attachment names; null when the attachment list isn't known yet.</summary>
    public IReadOnlyList<string>? AttachmentNames { get; init; }
    public bool Unread { get; init; }
    public bool Flagged { get; init; }
}

public static class RuleLimits
{
    public const int MaxActiveRules = 100;
    public const int MaxConditions = 20;
    public const int MaxActions = 10;
    public const int MaxOperand = 200;
    public const int MaxName = 100;
    public const int MaxBlockEntries = 500;

    /// <summary>Inspected body text (256 KiB of UTF-16 is about this many characters).</summary>
    public const int MaxBodyChars = 131_072;

    /// <summary>How long rule activity is kept.</summary>
    public static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(30);
}

/// <summary>A rule or block entry that cannot be saved, with a plain reason.</summary>
public sealed class RuleException(string message) : Exception(message);
