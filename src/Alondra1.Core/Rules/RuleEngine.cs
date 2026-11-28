using System.Security.Cryptography;
using System.Text;

namespace Alondra1.Core.Rules;

/// <summary>What an incoming message should get: the final result of all matching rules (or a block).</summary>
public sealed class IncomingPlan
{
    /// <summary>The blocklist entry that matched: the message is quarantined and ordinary rules are skipped.</summary>
    public BlockEntry? BlockedBy { get; set; }

    /// <summary>A body-text block entry could not be checked yet (the body isn't downloaded).</summary>
    public bool BlockCheckPending { get; set; }
    public HashSet<string> AddTags { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RemoveTags { get; } = new(StringComparer.Ordinal);
    public bool? Read { get; set; }
    public bool? Flagged { get; set; }

    /// <summary>The final destination: a folder id, or "Archive", "Junk", "Trash" (system folders of the account).</summary>
    public string? MoveTo { get; set; }
    public List<MailRule> Matched { get; } = [];

    /// <summary>Rules that could not be decided (missing body or attachment list); they did nothing.</summary>
    public List<MailRule> Undecided { get; } = [];

    public bool HasEffects => BlockedBy is not null || AddTags.Count > 0 || RemoveTags.Count > 0 || Read is not null || Flagged is not null || MoveTo is not null;
}

public sealed record RuleNotice(string RuleName, string Text);

/// <summary>What outgoing rules say about a draft: blockers win over warnings.</summary>
public sealed class OutgoingVerdict
{
    public List<RuleNotice> Blockers { get; } = [];
    public List<RuleNotice> Warnings { get; } = [];
    public HashSet<string> AddTags { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RemoveTags { get; } = new(StringComparer.Ordinal);
    public bool? Flagged { get; set; }
    public List<MailRule> Matched { get; } = [];

    /// <summary>Identifies the draft and the rule revisions a confirmation was given for (any change invalidates it).</summary>
    public string ApprovalKey { get; set; } = "";
}

public static class RuleEngine
{
    /// <summary>Active rules for one direction and account, in their fixed order (priority, then id).</summary>
    public static IReadOnlyList<MailRule> Applicable(IEnumerable<MailRule> rules, RuleDirection direction, string accountId) =>
        rules.Where(r => r.Active && r.Direction == direction && (r.AccountId is null || r.AccountId == accountId))
            .OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

    public static Truth Evaluate(MailRule rule, MessageFacts facts)
    {
        if (rule.Conditions.Count == 0)
            return rule.AllMessages ? Truth.True : Truth.False;
        var unknown = false;
        foreach (var condition in rule.Conditions)
        {
            var t = Evaluate(condition, facts);
            if (rule.Match == RuleMatch.All && t == Truth.False)
                return Truth.False;
            if (rule.Match == RuleMatch.Any && t == Truth.True)
                return Truth.True;
            unknown |= t == Truth.Unknown;
        }

        return unknown ? Truth.Unknown : rule.Match == RuleMatch.All ? Truth.True : Truth.False;
    }

    public static Truth Evaluate(RuleCondition c, MessageFacts facts)
    {
        switch (c.Field)
        {
            case RuleField.From:
                return Single(facts.From, c, address: true);
            case RuleField.ReplyTo:
                return Single(string.IsNullOrEmpty(facts.ReplyTo) ? "" : facts.ReplyTo, c, address: true);
            case RuleField.Subject:
                return Single(facts.Subject, c, address: false);
            case RuleField.Recipients:
                return Many(facts.Recipients, c);
            case RuleField.AttachmentName:
                return facts.AttachmentNames is null ? Truth.Unknown : Many(facts.AttachmentNames, c);
            case RuleField.Body:
                if (facts.Body is null)
                    return Truth.Unknown;
                var found = MailMatch.ContainsText(MailMatch.Fold(facts.Body), MailMatch.Fold(c.Value), wholeWord: false);
                // A miss in a truncated body proves nothing.
                return c.Operator switch
                {
                    RuleOperator.Contains => found ? Truth.True : facts.BodyTruncated ? Truth.Unknown : Truth.False,
                    RuleOperator.DoesNotContain => found ? Truth.False : facts.BodyTruncated ? Truth.Unknown : Truth.True,
                    _ => Truth.False,
                };
            case RuleField.Tag:
                var has = facts.Tags.Contains(c.Value);
                return Of(c.Operator == RuleOperator.HasTag ? has : !has);
            case RuleField.HasAttachments:
                return facts.HasAttachments is { } a ? Of(c.Operator == RuleOperator.Exists ? a : !a) : Truth.Unknown;
            case RuleField.Account:
                var same = string.Equals(facts.AccountId, c.Value, StringComparison.Ordinal);
                return Of(c.Operator == RuleOperator.Is ? same : !same);
            case RuleField.Unread:
                return Of(c.Operator == RuleOperator.Exists ? facts.Unread : !facts.Unread);
            case RuleField.Flagged:
                return Of(c.Operator == RuleOperator.Exists ? facts.Flagged : !facts.Flagged);
            default:
                return Truth.False;
        }
    }

    private static Truth Of(bool value) => value ? Truth.True : Truth.False;

    private static Truth Single(string? raw, RuleCondition c, bool address)
    {
        if (raw is null)
            return Truth.Unknown;
        var value = address ? MailMatch.Address(raw) ?? MailMatch.Fold(raw) : MailMatch.Fold(raw);
        var present = value.Length > 0;
        if (c.Operator == RuleOperator.Exists)
            return Of(present);
        if (c.Operator == RuleOperator.DoesNotExist)
            return Of(!present);
        return Of(Compare(value, MailMatch.Fold(c.Value), c.Operator));
    }

    private static Truth Many(IReadOnlyList<string> values, RuleCondition c)
    {
        var folded = values.Select(v => MailMatch.Address(v) ?? MailMatch.Fold(v)).ToList();
        var operand = MailMatch.Fold(c.Value);
        return c.Operator switch
        {
            RuleOperator.Exists => Of(folded.Count > 0),
            RuleOperator.DoesNotExist => Of(folded.Count == 0),
            // Negative operators hold when no value has the property.
            RuleOperator.IsNot => Of(!folded.Any(v => Compare(v, operand, RuleOperator.Is))),
            RuleOperator.DoesNotContain => Of(!folded.Any(v => Compare(v, operand, RuleOperator.Contains))),
            _ => Of(folded.Any(v => Compare(v, operand, c.Operator))),
        };
    }

    private static bool Compare(string value, string operand, RuleOperator op) => op switch
    {
        RuleOperator.Is => value == operand,
        RuleOperator.IsNot => value != operand,
        RuleOperator.Contains => value.Contains(operand, StringComparison.Ordinal),
        RuleOperator.DoesNotContain => !value.Contains(operand, StringComparison.Ordinal),
        RuleOperator.StartsWith => value.StartsWith(operand, StringComparison.Ordinal),
        RuleOperator.EndsWith => value.EndsWith(operand, StringComparison.Ordinal),
        _ => false,
    };

    // ------------------------------------------------------------------ blocklist

    /// <summary>
    /// The first enabled entry that matches (addresses and domains before text). Body entries need the body: without it
    /// the result says the check is pending instead of "not blocked".
    /// </summary>
    public static (BlockEntry? Entry, bool Pending) MatchBlock(IEnumerable<BlockEntry> entries, MessageFacts facts)
    {
        var from = MailMatch.Address(facts.From);
        var domain = MailMatch.DomainOf(from);
        var pending = false;
        var subject = MailMatch.Fold(facts.Subject);
        string? body = null;
        foreach (var e in entries.Where(e => e.Enabled && (e.AccountId is null || e.AccountId == facts.AccountId))
                     .OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            switch (e.Kind)
            {
                case BlockKind.Address when from is not null && from == e.Value:
                case BlockKind.Domain when MailMatch.DomainMatches(domain, e.Value, e.Mode == BlockMode.Broad):
                case BlockKind.SubjectText when MailMatch.ContainsText(subject, MailMatch.Fold(e.Value), e.Mode == BlockMode.Exact):
                    return (e, false);
                case BlockKind.BodyText:
                    if (facts.Body is null)
                    {
                        pending = true;
                        continue;
                    }

                    body ??= MailMatch.Fold(facts.Body);
                    if (MailMatch.ContainsText(body, MailMatch.Fold(e.Value), e.Mode == BlockMode.Exact))
                        return (e, false);
                    pending |= facts.BodyTruncated;
                    break;
            }
        }

        return (null, pending);
    }

    // ------------------------------------------------------------------ plans

    /// <summary>
    /// The plan for a newly received message: the blocklist first (a block skips every rule), then the rules in order on
    /// working metadata (tags added by an earlier rule are seen by later ones; the last folder action wins; Stop ends it).
    /// </summary>
    public static IncomingPlan PlanIncoming(MessageFacts facts, IReadOnlyList<MailRule> rules, IReadOnlyList<BlockEntry> blocks, bool exempt)
    {
        var plan = new IncomingPlan();
        if (!exempt)
        {
            var (entry, pending) = MatchBlock(blocks, facts);
            plan.BlockedBy = entry;
            plan.BlockCheckPending = entry is null && pending;
            if (entry is not null)
                return plan;
        }

        var tags = new HashSet<string>(facts.Tags, StringComparer.Ordinal);
        var unread = facts.Unread;
        var flagged = facts.Flagged;
        foreach (var rule in Applicable(rules, RuleDirection.Incoming, facts.AccountId))
        {
            var working = new MessageFacts
            {
                AccountId = facts.AccountId, From = facts.From, Recipients = facts.Recipients, ReplyTo = facts.ReplyTo, Subject = facts.Subject,
                Body = facts.Body, BodyTruncated = facts.BodyTruncated, Tags = tags, HasAttachments = facts.HasAttachments,
                AttachmentNames = facts.AttachmentNames, Unread = unread, Flagged = flagged,
            };
            var truth = Evaluate(rule, working);
            if (truth == Truth.Unknown)
                plan.Undecided.Add(rule);
            if (truth != Truth.True)
                continue;
            plan.Matched.Add(rule);
            foreach (var a in rule.Actions)
            {
                switch (a.Kind)
                {
                    case RuleActionKind.AddTag:
                        tags.Add(a.Value);
                        plan.AddTags.Add(a.Value);
                        plan.RemoveTags.Remove(a.Value);
                        break;
                    case RuleActionKind.RemoveTag:
                        tags.Remove(a.Value);
                        plan.RemoveTags.Add(a.Value);
                        plan.AddTags.Remove(a.Value);
                        break;
                    case RuleActionKind.MarkRead: plan.Read = true; unread = false; break;
                    case RuleActionKind.MarkUnread: plan.Read = false; unread = true; break;
                    case RuleActionKind.Flag: plan.Flagged = true; flagged = true; break;
                    case RuleActionKind.Unflag: plan.Flagged = false; flagged = false; break;
                    case RuleActionKind.MoveTo: plan.MoveTo = a.Value; break;
                    case RuleActionKind.Archive: plan.MoveTo = "Archive"; break;
                    case RuleActionKind.MoveToJunk: plan.MoveTo = "Junk"; break;
                    case RuleActionKind.MoveToTrash: plan.MoveTo = "Trash"; break;
                }
            }

            if (rule.StopProcessing)
                break;
        }

        return plan;
    }

    /// <summary>
    /// What outgoing rules say about a draft. A rule that cannot be decided and would warn or block is treated as a
    /// warning ("could not be checked"), never as a pass.
    /// </summary>
    public static OutgoingVerdict CheckOutgoing(MessageFacts facts, IReadOnlyList<MailRule> rules, string draftFingerprint)
    {
        var verdict = new OutgoingVerdict();
        var applicable = Applicable(rules, RuleDirection.Outgoing, facts.AccountId);
        foreach (var rule in applicable)
        {
            var truth = Evaluate(rule, facts);
            var guards = rule.Actions.Where(a => a.Kind is RuleActionKind.Warn or RuleActionKind.Block).ToList();
            if (truth == Truth.Unknown && guards.Count > 0)
            {
                verdict.Warnings.Add(new RuleNotice(rule.Name, "This rule could not check the message."));
                continue;
            }

            if (truth != Truth.True)
                continue;
            verdict.Matched.Add(rule);
            foreach (var a in rule.Actions)
            {
                switch (a.Kind)
                {
                    case RuleActionKind.Block: verdict.Blockers.Add(new RuleNotice(rule.Name, a.Value)); break;
                    case RuleActionKind.Warn: verdict.Warnings.Add(new RuleNotice(rule.Name, a.Value)); break;
                    case RuleActionKind.AddTag: verdict.AddTags.Add(a.Value); verdict.RemoveTags.Remove(a.Value); break;
                    case RuleActionKind.RemoveTag: verdict.RemoveTags.Add(a.Value); verdict.AddTags.Remove(a.Value); break;
                    case RuleActionKind.Flag: verdict.Flagged = true; break;
                    case RuleActionKind.Unflag: verdict.Flagged = false; break;
                }
            }

            if (rule.StopProcessing)
                break;
        }

        verdict.ApprovalKey = Hash(draftFingerprint + "\u001f" + string.Join(",", applicable.Select(r => r.Id + ":" + r.Revision)));
        return verdict;
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}
