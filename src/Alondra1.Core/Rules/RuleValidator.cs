namespace Alondra1.Core.Rules;

/// <summary>Checks a rule or block entry before it is saved; throws <see cref="RuleException"/> with a plain reason.</summary>
public static class RuleValidator
{
    private static readonly Dictionary<RuleField, RuleOperator[]> Operators = new()
    {
        [RuleField.From] = [RuleOperator.Is, RuleOperator.IsNot, RuleOperator.Contains, RuleOperator.DoesNotContain, RuleOperator.StartsWith, RuleOperator.EndsWith, RuleOperator.Exists, RuleOperator.DoesNotExist],
        [RuleField.ReplyTo] = [RuleOperator.Is, RuleOperator.IsNot, RuleOperator.Contains, RuleOperator.DoesNotContain, RuleOperator.EndsWith, RuleOperator.Exists, RuleOperator.DoesNotExist],
        [RuleField.Recipients] = [RuleOperator.Is, RuleOperator.IsNot, RuleOperator.Contains, RuleOperator.DoesNotContain, RuleOperator.EndsWith, RuleOperator.Exists, RuleOperator.DoesNotExist],
        [RuleField.Subject] = [RuleOperator.Is, RuleOperator.IsNot, RuleOperator.Contains, RuleOperator.DoesNotContain, RuleOperator.StartsWith, RuleOperator.EndsWith],
        [RuleField.Body] = [RuleOperator.Contains, RuleOperator.DoesNotContain],
        [RuleField.Tag] = [RuleOperator.HasTag, RuleOperator.LacksTag],
        [RuleField.HasAttachments] = [RuleOperator.Exists, RuleOperator.DoesNotExist],
        [RuleField.AttachmentName] = [RuleOperator.Is, RuleOperator.Contains, RuleOperator.DoesNotContain, RuleOperator.EndsWith],
        [RuleField.Account] = [RuleOperator.Is, RuleOperator.IsNot],
        [RuleField.Unread] = [RuleOperator.Exists, RuleOperator.DoesNotExist],
        [RuleField.Flagged] = [RuleOperator.Exists, RuleOperator.DoesNotExist],
    };

    public static IReadOnlyList<RuleOperator> OperatorsFor(RuleField field) => Operators[field];

    public static IReadOnlyList<RuleField> FieldsFor(RuleDirection direction) => direction == RuleDirection.Incoming
        ? Enum.GetValues<RuleField>()
        : [RuleField.From, RuleField.Recipients, RuleField.Subject, RuleField.Body, RuleField.Tag, RuleField.HasAttachments, RuleField.AttachmentName, RuleField.Account];

    public static IReadOnlyList<RuleActionKind> ActionsFor(RuleDirection direction) => direction == RuleDirection.Incoming
        ? [RuleActionKind.AddTag, RuleActionKind.RemoveTag, RuleActionKind.MarkRead, RuleActionKind.MarkUnread, RuleActionKind.Flag, RuleActionKind.Unflag,
            RuleActionKind.MoveTo, RuleActionKind.Archive, RuleActionKind.MoveToJunk, RuleActionKind.MoveToTrash]
        // Sent copies aren't moved by rules yet; outgoing tags are app data only, never message headers.
        : [RuleActionKind.AddTag, RuleActionKind.RemoveTag, RuleActionKind.Flag, RuleActionKind.Unflag, RuleActionKind.Warn, RuleActionKind.Block];

    private static bool NeedsOperand(RuleCondition c) => c.Field is not (RuleField.HasAttachments or RuleField.Unread or RuleField.Flagged)
        && c.Operator is not (RuleOperator.Exists or RuleOperator.DoesNotExist);

    /// <summary>Validates and trims the rule in place. <paramref name="tagExists"/> says whether a tag id is live.</summary>
    public static void Validate(MailRule rule, Func<string, bool> tagExists)
    {
        rule.Name = (rule.Name ?? "").Trim();
        if (rule.Name.Length == 0)
            throw new RuleException("Give the rule a name.");
        if (rule.Name.Length > RuleLimits.MaxName)
            throw new RuleException($"Rule names can be at most {RuleLimits.MaxName} characters.");
        if (rule.Conditions.Count > RuleLimits.MaxConditions)
            throw new RuleException($"A rule can have at most {RuleLimits.MaxConditions} conditions.");
        if (rule.Conditions.Count == 0 && !rule.AllMessages)
            throw new RuleException("Add a condition, or choose to apply the rule to all messages.");
        if (rule.Actions.Count == 0)
            throw new RuleException("Add at least one action.");
        if (rule.Actions.Count > RuleLimits.MaxActions)
            throw new RuleException($"A rule can have at most {RuleLimits.MaxActions} actions.");

        var fields = FieldsFor(rule.Direction);
        foreach (var c in rule.Conditions)
        {
            c.Value = (c.Value ?? "").Trim();
            if (!fields.Contains(c.Field))
                throw new RuleException($"{Describe(c.Field)} can't be used in {rule.Direction.ToString().ToLowerInvariant()} rules.");
            if (!Operators[c.Field].Contains(c.Operator))
                throw new RuleException($"“{Describe(c.Operator)}” doesn't work with {Describe(c.Field)}.");
            if (!NeedsOperand(c))
            {
                c.Value = "";
                continue;
            }

            if (c.Value.Length == 0)
                throw new RuleException($"Enter a value for {Describe(c.Field)}.");
            if (c.Value.Length > RuleLimits.MaxOperand)
                throw new RuleException($"Values can be at most {RuleLimits.MaxOperand} characters.");
            if (c.Value.Any(char.IsControl))
                throw new RuleException("Values can't contain control characters.");
            if (c.Field == RuleField.Tag && !tagExists(c.Value))
                throw new RuleException("The rule uses a tag that no longer exists.");
        }

        var actions = ActionsFor(rule.Direction);
        foreach (var a in rule.Actions)
        {
            a.Value = (a.Value ?? "").Trim();
            if (!actions.Contains(a.Kind))
                throw new RuleException($"“{Describe(a.Kind)}” isn't available for {rule.Direction.ToString().ToLowerInvariant()} rules.");
            switch (a.Kind)
            {
                case RuleActionKind.AddTag or RuleActionKind.RemoveTag when !tagExists(a.Value):
                    throw new RuleException("Choose a tag for the tag action.");
                case RuleActionKind.MoveTo when a.Value.Length == 0:
                    throw new RuleException("Choose the folder to move messages to.");
                case RuleActionKind.MoveTo when rule.AccountId is null:
                    // Folder ids differ between accounts: a rule for all accounts uses Archive, Junk or Trash instead.
                    throw new RuleException("A rule for all accounts can't move to a folder of one account. Choose an account, or use Archive, Junk or Trash.");
                case RuleActionKind.Warn or RuleActionKind.Block when a.Value.Length == 0:
                    a.Value = a.Kind == RuleActionKind.Block ? "This message can't be sent." : "Check this message before sending.";
                    break;
            }

            if (a.Value.Length > RuleLimits.MaxOperand)
                throw new RuleException($"Values can be at most {RuleLimits.MaxOperand} characters.");
        }
    }

    /// <summary>Validates and normalizes a block entry in place.</summary>
    public static void Validate(BlockEntry entry)
    {
        var raw = (entry.Value ?? "").Trim();
        switch (entry.Kind)
        {
            case BlockKind.Address:
                entry.Value = MailMatch.Address(raw) ?? throw new RuleException("Enter a complete email address, such as name@example.com.");
                entry.Mode = BlockMode.Exact;
                break;
            case BlockKind.Domain:
                if (raw.Contains('*'))
                    throw new RuleException("Wildcards aren't used: enter the domain, and choose whether to include its subdomains.");
                var domain = MailMatch.Domain(raw);
                if (domain is null || !domain.Contains('.'))
                    throw new RuleException("Enter a domain such as example.com.");
                entry.Value = domain;
                break;
            default:
                var text = raw.Normalize(System.Text.NormalizationForm.FormC);
                if (string.IsNullOrWhiteSpace(text))
                    throw new RuleException("Enter a word or phrase.");
                if (text.Length > RuleLimits.MaxOperand)
                    throw new RuleException($"Words and phrases can be at most {RuleLimits.MaxOperand} characters.");
                if (text.Any(char.IsControl))
                    throw new RuleException("Words and phrases can't contain control characters.");
                entry.Value = text;
                break;
        }
    }

    public static string Describe(RuleField f) => f switch
    {
        RuleField.From => "From",
        RuleField.Recipients => "To or Cc",
        RuleField.ReplyTo => "Reply-To",
        RuleField.Subject => "Subject",
        RuleField.Body => "Message text",
        RuleField.Tag => "Tag",
        RuleField.HasAttachments => "Has attachments",
        RuleField.AttachmentName => "Attachment name",
        RuleField.Account => "Account",
        RuleField.Unread => "Is unread",
        RuleField.Flagged => "Is flagged",
        _ => f.ToString(),
    };

    public static string Describe(RuleOperator o) => o switch
    {
        RuleOperator.Is => "is",
        RuleOperator.IsNot => "is not",
        RuleOperator.Contains => "contains",
        RuleOperator.DoesNotContain => "does not contain",
        RuleOperator.StartsWith => "starts with",
        RuleOperator.EndsWith => "ends with",
        RuleOperator.Exists => "yes",
        RuleOperator.DoesNotExist => "no",
        RuleOperator.HasTag => "has tag",
        RuleOperator.LacksTag => "doesn't have tag",
        _ => o.ToString(),
    };

    public static string Describe(RuleActionKind a) => a switch
    {
        RuleActionKind.AddTag => "Add tag",
        RuleActionKind.RemoveTag => "Remove tag",
        RuleActionKind.MarkRead => "Mark as read",
        RuleActionKind.MarkUnread => "Mark as unread",
        RuleActionKind.Flag => "Flag",
        RuleActionKind.Unflag => "Remove flag",
        RuleActionKind.MoveTo => "Move to folder",
        RuleActionKind.Archive => "Archive",
        RuleActionKind.MoveToJunk => "Move to Junk",
        RuleActionKind.MoveToTrash => "Move to Trash",
        RuleActionKind.Warn => "Ask before sending",
        RuleActionKind.Block => "Don't send",
        _ => a.ToString(),
    };
}
