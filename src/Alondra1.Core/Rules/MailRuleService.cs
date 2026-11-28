using System.Text.Json;
using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Core.Tags;

namespace Alondra1.Core.Rules;

/// <summary>
/// Mail rules and the blocklist on this PC (FEATURES_EXTENSION Part B §3–§5).
/// Ownership (§4.1): mail exists only in the Windows app, so this app is the single processor of its accounts' rules.
/// Incoming rules run for accounts that keep mail on this PC (Personal mode), once per newly received message (never
/// for the first download of an account, folder moves, read changes or tag sync). Outgoing rules run before every
/// send, in the compose window and again when a queued message is dispatched.
/// Rules never fail a sync or a send: errors are recorded as activity.
/// </summary>
public sealed class MailRuleService
{
    private readonly IRuleStore _store;
    private readonly TagBoard? _tags;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<MailRule> _rules = [];
    private List<BlockEntry> _blocks = [];

    public MailRuleService(IRuleStore store, TagBoard? tags, IClock clock)
    {
        _store = store;
        _tags = tags;
        _clock = clock;
        if (_tags is not null)
            _tags.Changed += () => _ = SuspendForMissingTagsAsync();
    }

    public event Action? Changed;

    /// <summary>Turns incoming rules and the blocklist off without deleting them (Settings switch).</summary>
    public bool IncomingEnabled { get; set; } = true;
    public bool BlocklistEnabled { get; set; } = true;

    public IReadOnlyList<MailRule> Rules => _rules.OrderBy(r => r.Direction).ThenBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    public IReadOnlyList<BlockEntry> Blocks => _blocks.OrderBy(b => b.Kind).ThenBy(b => b.Value, StringComparer.Ordinal).ToList();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _rules = (await _store.LoadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        _blocks = (await _store.LoadBlocksAsync(cancellationToken).ConfigureAwait(false)).ToList();
        await _store.PruneExecutionsAsync(_clock.UtcNow - RuleLimits.ActivityRetention, cancellationToken).ConfigureAwait(false);
        await SuspendForMissingTagsAsync().ConfigureAwait(false);
    }

    private bool TagExists(string id) => _tags is null || _tags.Find(id) is not null;

    // ------------------------------------------------------------------ rules

    /// <summary>Validates and saves (a new rule goes last). Throws <see cref="RuleException"/> with a plain reason.</summary>
    public async Task<MailRule> SaveRuleAsync(MailRule edited)
    {
        var rule = edited.Clone();
        RuleValidator.Validate(rule, TagExists);
        var existing = _rules.FirstOrDefault(r => r.Id == rule.Id);
        if (rule.Enabled && _rules.Count(r => r.Enabled && r.Id != rule.Id) >= RuleLimits.MaxActiveRules)
            throw new RuleException($"You can have at most {RuleLimits.MaxActiveRules} rules turned on.");
        var now = _clock.UtcNow;
        rule.CreatedUtc = existing?.CreatedUtc ?? now;
        rule.ModifiedUtc = now;
        rule.Revision = (existing?.Revision ?? 0) + 1;
        rule.SuspendedReason = null; // saving after a fix resumes the rule (validation checked its tags)
        if (existing is null)
            rule.Priority = _rules.Where(r => r.Direction == rule.Direction).Select(r => r.Priority + 1).DefaultIfEmpty(0).Max();
        await _store.SaveRuleAsync(rule, CancellationToken.None).ConfigureAwait(false);
        _rules.RemoveAll(r => r.Id == rule.Id);
        _rules.Add(rule);
        Changed?.Invoke();
        return rule;
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == id)?.Clone() ?? throw new RuleException("This rule no longer exists.");
        rule.Enabled = enabled;
        await SaveRuleAsync(rule).ConfigureAwait(false);
    }

    public async Task<MailRule> DuplicateAsync(string id)
    {
        var source = _rules.FirstOrDefault(r => r.Id == id) ?? throw new RuleException("This rule no longer exists.");
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("n");
        copy.Name = (source.Name + " (copy)")[..Math.Min(RuleLimits.MaxName, source.Name.Length + 7)];
        copy.Enabled = false; // never silently active
        copy.Revision = 0;
        copy.LastRunUtc = null;
        copy.LastResult = null;
        return await SaveRuleAsync(copy).ConfigureAwait(false);
    }

    public async Task DeleteRuleAsync(string id)
    {
        await _store.DeleteRuleAsync(id, CancellationToken.None).ConfigureAwait(false);
        _rules.RemoveAll(r => r.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Moves a rule up (-1) or down (+1) among the rules of its direction; priorities are renumbered.</summary>
    public async Task MoveAsync(string id, int delta)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == id);
        if (rule is null)
            return;
        var ordered = _rules.Where(r => r.Direction == rule.Direction).OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var index = ordered.IndexOf(rule);
        var target = Math.Clamp(index + delta, 0, ordered.Count - 1);
        if (target == index)
            return;
        ordered.RemoveAt(index);
        ordered.Insert(target, rule);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Priority == i)
                continue;
            ordered[i].Priority = i;
            await _store.SaveRuleAsync(ordered[i], CancellationToken.None).ConfigureAwait(false);
        }

        Changed?.Invoke();
    }

    /// <summary>How many rules use a tag (the tag delete prompt says they will be turned off).</summary>
    public int RulesUsing(string tagId) => _rules.Count(r => UsesTag(r, tagId));

    private static bool UsesTag(MailRule r, string tagId) =>
        r.Conditions.Any(c => c.Field == RuleField.Tag && c.Value == tagId) || r.Actions.Any(a => a.Kind is RuleActionKind.AddTag or RuleActionKind.RemoveTag && a.Value == tagId);

    /// <summary>A rule whose tag was deleted is suspended (kept, with the reason shown) instead of acting on nothing.</summary>
    public async Task SuspendForMissingTagsAsync()
    {
        if (_tags is null)
            return;
        var changed = false;
        foreach (var rule in _rules.Where(r => r.SuspendedReason is null))
        {
            var ids = rule.Conditions.Where(c => c.Field == RuleField.Tag).Select(c => c.Value)
                .Concat(rule.Actions.Where(a => a.Kind is RuleActionKind.AddTag or RuleActionKind.RemoveTag).Select(a => a.Value));
            if (ids.All(id => _tags.Find(id) is not null))
                continue;
            rule.SuspendedReason = "A tag it uses was deleted. Edit the rule to choose another tag.";
            await _store.SaveRuleAsync(rule, CancellationToken.None).ConfigureAwait(false);
            changed = true;
        }

        if (changed)
            Changed?.Invoke();
    }

    // ------------------------------------------------------------------ blocklist

    /// <summary>Adds or edits an entry. An identical entry is reused instead of duplicated.</summary>
    public async Task<BlockEntry> SaveBlockAsync(BlockEntry edited)
    {
        var entry = JsonSerializer.Deserialize<BlockEntry>(JsonSerializer.Serialize(edited))!;
        RuleValidator.Validate(entry);
        var same = _blocks.FirstOrDefault(b => b.Id != entry.Id && b.Kind == entry.Kind && b.AccountId == entry.AccountId && b.Mode == entry.Mode
            && string.Equals(MailMatch.Fold(b.Value), MailMatch.Fold(entry.Value), StringComparison.Ordinal));
        if (same is not null)
            return same;
        if (_blocks.All(b => b.Id != entry.Id) && _blocks.Count >= RuleLimits.MaxBlockEntries)
            throw new RuleException($"You can have at most {RuleLimits.MaxBlockEntries} blocked senders and words.");
        var existing = _blocks.FirstOrDefault(b => b.Id == entry.Id);
        entry.CreatedUtc = existing?.CreatedUtc ?? _clock.UtcNow;
        await _store.SaveBlockAsync(entry, CancellationToken.None).ConfigureAwait(false);
        _blocks.RemoveAll(b => b.Id == entry.Id);
        _blocks.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    public async Task DeleteBlockAsync(string id)
    {
        await _store.DeleteBlockAsync(id, CancellationToken.None).ConfigureAwait(false);
        _blocks.RemoveAll(b => b.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Which entry would block this message (Test against a message), without changing anything.</summary>
    public (BlockEntry? Entry, bool Pending) TestBlock(BlockEntry entry, MessageFacts facts) => RuleEngine.MatchBlock([entry], facts);

    // ------------------------------------------------------------------ facts

    public static MessageFacts Facts(StoredMessage m, StoredBody? body, IReadOnlySet<string> tags)
    {
        var text = body?.Text;
        var truncated = text is not null && text.Length > RuleLimits.MaxBodyChars;
        return new MessageFacts
        {
            AccountId = m.AccountId,
            From = string.IsNullOrEmpty(m.FromAddress) ? null : m.FromAddress,
            Recipients = MailMatch.Addresses(m.Recipients).Concat(MailMatch.Addresses(m.Cc)).ToList(),
            ReplyTo = m.ReplyTo,
            Subject = m.Subject,
            Body = text is null ? null : truncated ? text[..RuleLimits.MaxBodyChars] : text,
            BodyTruncated = truncated,
            Tags = tags,
            HasAttachments = m.HasAttachments,
            AttachmentNames = body?.Attachments.Select(a => a.FileName).ToList(),
            Unread = m.IsUnread,
            Flagged = m.IsFlagged,
        };
    }

    public static MessageFacts Facts(AccountConfig account, OutgoingMail mail) => new()
    {
        AccountId = account.Id,
        From = account.Address,
        Recipients = mail.To.Concat(mail.Cc).Concat(mail.Bcc).SelectMany(MailMatch.Addresses).ToList(),
        Subject = mail.Subject,
        Body = mail.TextBody.Length > RuleLimits.MaxBodyChars ? mail.TextBody[..RuleLimits.MaxBodyChars] : mail.TextBody,
        BodyTruncated = mail.TextBody.Length > RuleLimits.MaxBodyChars,
        Tags = new HashSet<string>(),
        HasAttachments = mail.Attachments.Count > 0,
        AttachmentNames = mail.Attachments.Select(a => a.FileName).ToList(),
    };

    /// <summary>Everything about a draft that a confirmation covers: changing any of it asks again.</summary>
    public static string Fingerprint(AccountConfig account, OutgoingMail mail) => JsonSerializer.Serialize(new
    {
        account.Id, mail.To, mail.Cc, mail.Bcc, mail.Subject, mail.TextBody,
        Attachments = mail.Attachments.Select(a => a.FileName + "|" + a.ContentType + "|" + a.SourcePath),
    });

    // ------------------------------------------------------------------ outgoing

    /// <summary>What the outgoing rules say about this draft (no side effects).</summary>
    public OutgoingVerdict CheckOutgoing(AccountConfig account, OutgoingMail mail) =>
        RuleEngine.CheckOutgoing(Facts(account, mail), _rules, Fingerprint(account, mail));

    /// <summary>
    /// The guard on the send path: null when the message may go now; otherwise why not. Blockers always stop it;
    /// warnings stop it unless the user confirmed exactly this draft under these rule revisions.
    /// </summary>
    public string? SendRefusal(AccountConfig account, OutgoingMail mail)
    {
        OutgoingVerdict verdict;
        try
        {
            verdict = CheckOutgoing(account, mail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An enabled guard that can't run must not be bypassed.
            return "Outgoing rules could not check this message, so it was not sent. Try again.";
        }

        if (verdict.Blockers.Count > 0)
            return $"Not sent: the rule “{verdict.Blockers[0].RuleName}” stops it. {verdict.Blockers[0].Text}";
        if (verdict.Warnings.Count > 0 && mail.RuleApproval != verdict.ApprovalKey)
            return $"Not sent yet: the rule “{verdict.Warnings[0].RuleName}” asks you to confirm. Open the message and send it again.";
        return null;
    }

    /// <summary>After a confirmed send: tags and flag from outgoing rules go on the sent copy (app data only, never headers).</summary>
    public async Task ApplySentAsync(AccountConfig account, OutgoingMail mail, StoredMessage sentCopy)
    {
        try
        {
            var verdict = CheckOutgoing(account, mail);
            var execution = Begin($"out|{account.Id}|{RuleEngine.Hash(mail.MessageId ?? Fingerprint(account, mail))}", account.Id, RuleDirection.Outgoing, verdict.Matched);
            if (!await _store.TryBeginExecutionAsync(execution, CancellationToken.None).ConfigureAwait(false))
                return;
            if (_tags is not null && (verdict.AddTags.Count > 0 || verdict.RemoveTags.Count > 0))
            {
                var mailRef = TagRules.MailRef(account.Id, sentCopy.MessageId, sentCopy.FolderId, sentCopy.Uid);
                var target = new MailTagTarget(mailRef, account.Id, sentCopy.FolderId, sentCopy.Uid, sentCopy.Subject, sentCopy.ReceivedUtc);
                foreach (var id in verdict.AddTags.Where(TagExists))
                    await _tags.SetTagAsync(TagRules.Mail, [mailRef], id, true, [target]).ConfigureAwait(false);
                foreach (var id in verdict.RemoveTags.Where(TagExists))
                    await _tags.SetTagAsync(TagRules.Mail, [mailRef], id, false, [target]).ConfigureAwait(false);
            }

            await FinishAsync(execution, "completed", Describe(verdict.Matched, verdict.AddTags.Count, verdict.Warnings.Count > 0 ? "confirmed" : null)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The message is sent; only its tags are missing.
        }
    }

    // ------------------------------------------------------------------ incoming

    /// <summary>
    /// Runs the blocklist and the incoming rules on messages that just arrived. <paramref name="apply"/> performs a
    /// local mail command (the same path as the user's own actions). Each message is processed at most once: the
    /// execution key is recorded before any side effect, so a duplicate arrival or a sync echo does nothing.
    /// </summary>
    public async Task<int> ProcessIncomingAsync(AccountConfig account, IReadOnlyList<StoredMessage> arrivals, Func<StoredMessage, Task<StoredBody?>> bodyOf,
        Func<RemoteMailCommand, Task> apply, CancellationToken cancellationToken)
    {
        if (!IncomingEnabled && !BlocklistEnabled)
            return 0;
        var rules = IncomingEnabled ? RuleEngine.Applicable(_rules, RuleDirection.Incoming, account.Id) : [];
        var blocks = BlocklistEnabled ? _blocks.Where(b => b.Enabled && (b.AccountId is null || b.AccountId == account.Id)).ToList() : [];
        if (rules.Count == 0 && blocks.Count == 0)
            return 0;
        var processed = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var message in arrivals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mailRef = TagRules.MailRef(account.Id, message.MessageId, message.FolderId, message.Uid);
                var execution = Begin($"in|{account.Id}|{mailRef}", account.Id, RuleDirection.Incoming, rules);
                if (!await _store.TryBeginExecutionAsync(execution, cancellationToken).ConfigureAwait(false))
                    continue;
                processed++;
                try
                {
                    await ProcessOneAsync(account, message, mailRef, execution, rules, blocks, bodyOf, apply, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    await FinishAsync(execution, "failed", "The rules could not run (" + ex.GetType().Name + "). The message was kept as it arrived.").ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (processed > 0)
            Changed?.Invoke();
        return processed;
    }

    private async Task ProcessOneAsync(AccountConfig account, StoredMessage message, string mailRef, RuleExecution execution, IReadOnlyList<MailRule> rules,
        IReadOnlyList<BlockEntry> blocks, Func<StoredMessage, Task<StoredBody?>> bodyOf, Func<RemoteMailCommand, Task> apply, CancellationToken cancellationToken)
    {
        var body = message.BodyDownloaded ? await bodyOf(message).ConfigureAwait(false) : null;
        var tags = (_tags?.TagsOf(TagRules.Mail, mailRef).Select(t => t.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        var exempt = await _store.IsExemptAsync(mailRef, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var plan = RuleEngine.PlanIncoming(Facts(message, body, tags), rules, blocks, exempt);
        var now = _clock.UtcNow;

        if (plan.BlockedBy is { } entry)
        {
            var junk = SpecialFolders.TryResolve(account, FolderRole.Junk) ?? (account.Incoming.Protocol == MailProtocol.Pop3 ? "Junk" : null);
            var reason = Reason(entry);
            if (junk is not null && junk != message.FolderId)
                await apply(new RemoteMailCommand { Kind = MailCommandKind.Move, FolderId = message.FolderId, Uid = message.Uid, DestinationFolderId = junk }).ConfigureAwait(false);
            await _store.SaveQuarantineAsync(new QuarantinedMail
            {
                Ref = mailRef, AccountId = account.Id, OriginalFolderId = message.FolderId, FolderId = junk ?? message.FolderId, Uid = message.Uid,
                EntryId = entry.Id, Reason = reason, BlockedUtc = now,
            }, cancellationToken).ConfigureAwait(false);
            entry.HitCount++;
            entry.LastHitUtc = now;
            await _store.SaveBlockAsync(entry, cancellationToken).ConfigureAwait(false);
            await FinishAsync(execution, "completed", reason + (junk is null ? " (this account has no Junk folder: left in place)" : " → Junk")).ConfigureAwait(false);
            return;
        }

        var problems = new List<string>();
        var destination = message.FolderId;
        if (plan.MoveTo is { } move)
        {
            var target = move switch
            {
                "Archive" => SpecialFolders.TryResolve(account, FolderRole.Archive),
                "Junk" => SpecialFolders.TryResolve(account, FolderRole.Junk),
                "Trash" => SpecialFolders.TryResolve(account, FolderRole.Trash),
                _ => account.Folders.Known.Count == 0 || account.Folders.Known.Any(f => f.Id == move) ? move : null,
            };
            if (target is null)
            {
                // Never fall back to another folder: the rule is suspended and the message stays where it is.
                foreach (var rule in plan.Matched.Where(r => r.Actions.Any(a => a.Kind is RuleActionKind.MoveTo or RuleActionKind.Archive or RuleActionKind.MoveToJunk or RuleActionKind.MoveToTrash)))
                {
                    rule.SuspendedReason = "Its folder no longer exists in this account. Edit the rule to choose another folder.";
                    await _store.SaveRuleAsync(rule, cancellationToken).ConfigureAwait(false);
                }

                problems.Add("folder missing");
            }
            else if (target != message.FolderId)
            {
                destination = target;
            }
        }

        if (_tags is not null && (plan.AddTags.Count > 0 || plan.RemoveTags.Count > 0))
        {
            var target = new MailTagTarget(mailRef, account.Id, destination, message.Uid, message.Subject, message.ReceivedUtc);
            foreach (var id in plan.AddTags.Where(TagExists))
                await _tags.SetTagAsync(TagRules.Mail, [mailRef], id, true, [target]).ConfigureAwait(false);
            foreach (var id in plan.RemoveTags.Where(TagExists))
                await _tags.SetTagAsync(TagRules.Mail, [mailRef], id, false, [target]).ConfigureAwait(false);
        }

        if (plan.Read is { } read && read == message.IsUnread)
            await apply(new RemoteMailCommand { Kind = MailCommandKind.SetRead, FolderId = message.FolderId, Uid = message.Uid, Value = read }).ConfigureAwait(false);
        if (plan.Flagged is { } flag && flag != message.IsFlagged)
            await apply(new RemoteMailCommand { Kind = MailCommandKind.SetFlagged, FolderId = message.FolderId, Uid = message.Uid, Value = flag }).ConfigureAwait(false);
        if (destination != message.FolderId)
        {
            var kind = plan.MoveTo switch { "Archive" => MailCommandKind.Archive, "Trash" => MailCommandKind.Delete, _ => MailCommandKind.Move };
            await apply(new RemoteMailCommand { Kind = kind, FolderId = message.FolderId, Uid = message.Uid, DestinationFolderId = destination }).ConfigureAwait(false);
        }

        foreach (var rule in plan.Matched)
        {
            rule.LastRunUtc = now;
            rule.LastResult = problems.Count == 0 ? "Applied" : "Partly applied: " + string.Join(", ", problems);
            await _store.SaveRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        }

        var unchecked_ = plan.BlockCheckPending || plan.Undecided.Count > 0;
        var status = problems.Count > 0 ? "partial" : plan.Matched.Count == 0 && unchecked_ ? "unknown" : "completed";
        var summary = plan.Matched.Count == 0 ? "No rule matched" : Describe(plan.Matched, plan.AddTags.Count, plan.MoveTo);
        if (unchecked_)
            summary += ". Some checks need the message text, which isn't downloaded for this folder: they were skipped";
        await FinishAsync(execution, status, summary).ConfigureAwait(false);
    }

    public static string Reason(BlockEntry e) => e.Kind switch
    {
        BlockKind.Address => "Blocked sender: " + e.Value,
        BlockKind.Domain => "Blocked domain: " + e.Value + (e.Mode == BlockMode.Broad ? " (and subdomains)" : ""),
        BlockKind.SubjectText => "Blocked words in the subject: “" + e.Value + "”",
        _ => "Blocked words in the message: “" + e.Value + "”",
    };

    private static string Describe(IReadOnlyList<MailRule> matched, int tags, string? extra)
    {
        if (matched.Count == 0)
            return "No rule matched";
        var parts = new List<string>();
        if (tags > 0)
            parts.Add("tagged");
        if (extra is not null)
            parts.Add(extra switch { "Archive" => "archived", "Junk" => "moved to Junk", "Trash" => "moved to Trash", "confirmed" => "sent after confirmation", _ => "moved" });
        return string.Join(", ", matched.Select(r => r.Name)) + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
    }

    private RuleExecution Begin(string key, string accountId, RuleDirection direction, IReadOnlyList<MailRule> rules) => new()
    {
        Key = key, AccountId = accountId, Direction = direction, Status = "running",
        Revisions = string.Join(",", rules.Select(r => r.Id + ":" + r.Revision)),
        CreatedUtc = _clock.UtcNow, UpdatedUtc = _clock.UtcNow,
    };

    private async Task FinishAsync(RuleExecution execution, string status, string summary)
    {
        execution.Status = status;
        execution.Summary = summary;
        execution.UpdatedUtc = _clock.UtcNow;
        await _store.UpdateExecutionAsync(execution, CancellationToken.None).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RuleExecution>> ActivityAsync(int limit = 200) => _store.ListExecutionsAsync(limit, CancellationToken.None);

    // ------------------------------------------------------------------ quarantine

    public Task<IReadOnlyList<QuarantinedMail>> QuarantineAsync() => _store.ListQuarantineAsync(CancellationToken.None);

    /// <summary>
    /// Restore puts the message back where it was and remembers it, so it is not blocked again when seen again
    /// (180 days); future messages from the same sender are still blocked. With <paramref name="unblock"/> the
    /// entry that blocked it is deleted too.
    /// </summary>
    public async Task RestoreAsync(QuarantinedMail item, bool unblock, Func<RemoteMailCommand, Task> apply)
    {
        if (item.FolderId is { } at && at != item.OriginalFolderId)
            await apply(new RemoteMailCommand { Kind = MailCommandKind.Move, FolderId = at, Uid = item.Uid, DestinationFolderId = item.OriginalFolderId }).ConfigureAwait(false);
        await _store.AddExemptionAsync(item.Ref, _clock.UtcNow.AddDays(180), CancellationToken.None).ConfigureAwait(false);
        await _store.DeleteQuarantineAsync(item.Ref, CancellationToken.None).ConfigureAwait(false);
        if (unblock)
            await DeleteBlockAsync(item.EntryId).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Unblock only: the entry goes, the message stays in Junk.</summary>
    public async Task UnblockAsync(QuarantinedMail item)
    {
        await DeleteBlockAsync(item.EntryId).ConfigureAwait(false);
        await _store.DeleteQuarantineAsync(item.Ref, CancellationToken.None).ConfigureAwait(false);
        Changed?.Invoke();
    }
}
