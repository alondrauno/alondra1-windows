using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Core.Rules;
using Alondra1.Core.Security;
using Alondra1.Core.Sync;
using Alondra1.Core.Tags;
using Alondra1.Persistence;

namespace Alondra1.Tests;

/// <summary>FEATURES_EXTENSION Part B §3–§5 and §8 (RULE-01..07, BLOCK-01..04, SEC-01): matching, evaluation and the processor.</summary>
public sealed class MailRuleMatchingTests
{
    private static MessageFacts Facts(string from = "news@example.com", string subject = "Hello", string? body = "Hi there", bool truncated = false,
        IEnumerable<string>? tags = null, string to = "me@home.org") => new()
    {
        AccountId = "a1", From = from, Recipients = MailMatch.Addresses(to), Subject = subject, Body = body, BodyTruncated = truncated,
        Tags = (tags ?? []).ToHashSet(), HasAttachments = false, AttachmentNames = [],
    };

    private static BlockEntry Block(BlockKind kind, string value, BlockMode mode = BlockMode.Exact)
    {
        var entry = new BlockEntry { Kind = kind, Value = value, Mode = mode };
        RuleValidator.Validate(entry);
        return entry;
    }

    [Fact]
    public void Block01_AddressesAndDomainsMatchExactlyWithoutSuffixTricksOrDisplayNames()
    {
        var domain = Block(BlockKind.Domain, "Example.COM.");
        Assert.Equal("example.com", domain.Value);
        Assert.NotNull(RuleEngine.MatchBlock([domain], Facts("x@example.com")).Entry);
        Assert.Null(RuleEngine.MatchBlock([domain], Facts("x@notexample.com")).Entry);
        Assert.Null(RuleEngine.MatchBlock([domain], Facts("x@example.com.evil")).Entry);
        Assert.Null(RuleEngine.MatchBlock([domain], Facts("x@mail.example.com")).Entry); // exact by default
        var broad = Block(BlockKind.Domain, "example.com", BlockMode.Broad);
        Assert.NotNull(RuleEngine.MatchBlock([broad], Facts("x@mail.example.com")).Entry);
        Assert.Null(RuleEngine.MatchBlock([broad], Facts("x@mail.notexample.com")).Entry);

        // The display name is never the identity.
        var address = Block(BlockKind.Address, "Boss <Boss@Example.com>");
        Assert.Equal("boss@example.com", address.Value);
        Assert.Null(RuleEngine.MatchBlock([address], Facts("\"boss@example.com\" <attacker@evil.test>")).Entry);
        Assert.NotNull(RuleEngine.MatchBlock([address], Facts("BOSS@example.com")).Entry);
        // Plus addressing is kept.
        Assert.Null(RuleEngine.MatchBlock([address], Facts("boss+x@example.com")).Entry);

        // International domains compare in ASCII.
        var idn = Block(BlockKind.Domain, "bücher.example");
        Assert.Equal("xn--bcher-kva.example", idn.Value);
        Assert.NotNull(RuleEngine.MatchBlock([idn], Facts("a@BÜCHER.example")).Entry);
    }

    [Theory]
    [InlineData(BlockKind.Domain, "*.example.com")]
    [InlineData(BlockKind.Domain, "example")]
    [InlineData(BlockKind.Domain, "exa mple.com")]
    [InlineData(BlockKind.Address, "not an address")]
    [InlineData(BlockKind.SubjectText, "   ")]
    public void MalformedEntriesAreRefusedWithAReason(BlockKind kind, string value) =>
        Assert.Throws<RuleException>(() => RuleValidator.Validate(new BlockEntry { Kind = kind, Value = value }));

    [Fact]
    public void Block02_WordsMatchWholeWordsAfterNormalizationAndAMissingBodyIsPending()
    {
        var phrase = Block(BlockKind.SubjectText, "Limited  Offer");
        Assert.NotNull(RuleEngine.MatchBlock([phrase], Facts(subject: "A LIMITED offer, today")).Entry);
        Assert.Null(RuleEngine.MatchBlock([phrase], Facts(subject: "Unlimited offers")).Entry); // whole words only
        var anywhere = Block(BlockKind.SubjectText, "offer", BlockMode.Broad);
        Assert.NotNull(RuleEngine.MatchBlock([anywhere], Facts(subject: "Special offers")).Entry);
        // Other scripts: a word boundary is any non-letter.
        var cyrillic = Block(BlockKind.SubjectText, "скидка");
        Assert.NotNull(RuleEngine.MatchBlock([cyrillic], Facts(subject: "Большая СКИДКА!")).Entry);
        Assert.Null(RuleEngine.MatchBlock([cyrillic], Facts(subject: "скидками")).Entry);
        // Composed and decomposed forms are the same text.
        var cafe = Block(BlockKind.SubjectText, "café");
        Assert.NotNull(RuleEngine.MatchBlock([cafe], Facts(subject: "Café news")).Entry);

        var body = Block(BlockKind.BodyText, "buy now");
        Assert.NotNull(RuleEngine.MatchBlock([body], Facts(body: "Please BUY now.")).Entry);
        Assert.True(RuleEngine.MatchBlock([body], Facts(body: null)).Pending); // not downloaded: unknown, not "clean"
        Assert.True(RuleEngine.MatchBlock([body], Facts(body: "nothing here", truncated: true)).Pending);
        Assert.False(RuleEngine.MatchBlock([body], Facts(body: "nothing here")).Pending);
    }

    private static MailRule Rule(string name, RuleMatch match, IEnumerable<RuleCondition> conditions, IEnumerable<RuleAction> actions, bool stop = false, int priority = 0) => new()
    {
        Id = name, Name = name, Enabled = true, Direction = RuleDirection.Incoming, Match = match, Priority = priority,
        Conditions = conditions.ToList(), Actions = actions.ToList(), StopProcessing = stop,
    };

    private static RuleCondition C(RuleField field, RuleOperator op, string value = "") => new() { Field = field, Operator = op, Value = value };
    private static RuleAction A(RuleActionKind kind, string value = "") => new() { Kind = kind, Value = value };

    [Fact]
    public void Rule01_AllAnyTagChainingAndStopAreDeterministic()
    {
        var first = Rule("1 newsletter", RuleMatch.Any, [C(RuleField.From, RuleOperator.EndsWith, "@example.com"), C(RuleField.Subject, RuleOperator.Contains, "digest")],
            [A(RuleActionKind.AddTag, "news"), A(RuleActionKind.MoveTo, "Lists")], priority: 0);
        var second = Rule("2 tagged news", RuleMatch.All, [C(RuleField.Tag, RuleOperator.HasTag, "news"), C(RuleField.Subject, RuleOperator.DoesNotContain, "urgent")],
            [A(RuleActionKind.MarkRead), A(RuleActionKind.Archive)], stop: true, priority: 1);
        var third = Rule("3 never", RuleMatch.All, [C(RuleField.Tag, RuleOperator.HasTag, "news")], [A(RuleActionKind.Flag)], priority: 2);
        var plan = RuleEngine.PlanIncoming(Facts(), [third, second, first], [], exempt: false);
        Assert.Equal(["1 newsletter", "2 tagged news"], plan.Matched.Select(r => r.Name));
        Assert.Contains("news", plan.AddTags);
        Assert.True(plan.Read);
        Assert.Equal("Archive", plan.MoveTo); // the last folder action wins
        Assert.Null(plan.Flagged); // stopped before rule 3

        // ALL: one false is false; ANY: one true is true.
        var all = Rule("all", RuleMatch.All, [C(RuleField.From, RuleOperator.Is, "news@example.com"), C(RuleField.Subject, RuleOperator.Is, "nope")], [A(RuleActionKind.Flag)]);
        Assert.Equal(Truth.False, RuleEngine.Evaluate(all, Facts()));
    }

    [Fact]
    public void Rule06_UnknownIsNeverANegativeMatch()
    {
        var notContains = Rule("no promo", RuleMatch.All, [C(RuleField.Body, RuleOperator.DoesNotContain, "promo")], [A(RuleActionKind.Flag)]);
        Assert.Equal(Truth.Unknown, RuleEngine.Evaluate(notContains, Facts(body: null)));
        Assert.Equal(Truth.Unknown, RuleEngine.Evaluate(notContains, Facts(body: "clean", truncated: true)));
        Assert.Equal(Truth.True, RuleEngine.Evaluate(notContains, Facts(body: "clean")));
        var plan = RuleEngine.PlanIncoming(Facts(body: null), [notContains], [], exempt: false);
        Assert.Empty(plan.Matched);
        Assert.Single(plan.Undecided);
        // ALL with a false condition is false even when another is unknown; ANY with a true one is true.
        var all = Rule("all", RuleMatch.All, [C(RuleField.Body, RuleOperator.Contains, "x"), C(RuleField.Subject, RuleOperator.Is, "nope")], [A(RuleActionKind.Flag)]);
        Assert.Equal(Truth.False, RuleEngine.Evaluate(all, Facts(body: null)));
        var any = Rule("any", RuleMatch.Any, [C(RuleField.Body, RuleOperator.Contains, "x"), C(RuleField.Subject, RuleOperator.Is, "hello")], [A(RuleActionKind.Flag)]);
        Assert.Equal(Truth.True, RuleEngine.Evaluate(any, Facts(body: null)));
    }

    [Fact]
    public void Block03_TheBlocklistWinsOverAConflictingRule()
    {
        var keep = Rule("keep in inbox", RuleMatch.All, [C(RuleField.From, RuleOperator.Contains, "example.com")], [A(RuleActionKind.AddTag, "vip"), A(RuleActionKind.MoveTo, "INBOX")]);
        var plan = RuleEngine.PlanIncoming(Facts(), [keep], [Block(BlockKind.Domain, "example.com")], exempt: false);
        Assert.NotNull(plan.BlockedBy);
        Assert.Empty(plan.Matched);
        Assert.Empty(plan.AddTags);
        // A restored (exempt) message is not blocked again, but the rules run.
        var exempt = RuleEngine.PlanIncoming(Facts(), [keep], [Block(BlockKind.Domain, "example.com")], exempt: true);
        Assert.Null(exempt.BlockedBy);
        Assert.Single(exempt.Matched);
    }

    [Fact]
    public void Rule02_OutgoingBlockWinsAndAConfirmationCoversOnlyThatDraft()
    {
        var warn = new MailRule { Id = "w", Name = "External", Enabled = true, Direction = RuleDirection.Outgoing, Priority = 0,
            Conditions = [C(RuleField.Recipients, RuleOperator.EndsWith, "@partner.test")], Actions = [A(RuleActionKind.Warn, "This goes outside the company.")] };
        var block = new MailRule { Id = "b", Name = "No secrets", Enabled = true, Direction = RuleDirection.Outgoing, Priority = 1,
            Conditions = [C(RuleField.Body, RuleOperator.Contains, "password")], Actions = [A(RuleActionKind.Block, "Don't send passwords.")] };
        var facts = Facts(from: "me@home.org", to: "x@partner.test", body: "hello");
        var verdict = RuleEngine.CheckOutgoing(facts, [warn, block], "draft-1");
        Assert.Empty(verdict.Blockers);
        Assert.Single(verdict.Warnings);
        Assert.NotEqual(verdict.ApprovalKey, RuleEngine.CheckOutgoing(facts, [warn, block], "draft-2").ApprovalKey); // changed draft
        warn.Revision++;
        Assert.NotEqual(verdict.ApprovalKey, RuleEngine.CheckOutgoing(facts, [warn, block], "draft-1").ApprovalKey); // changed rule
        var blocked = RuleEngine.CheckOutgoing(Facts(from: "me@home.org", to: "x@partner.test", body: "my password is"), [warn, block], "draft-3");
        Assert.Single(blocked.Blockers);
    }

    [Fact]
    public void Sec01_InvalidRulesAreRefusedWithAReason()
    {
        bool Tags(string id) => id == "news";
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("", RuleMatch.All, [C(RuleField.Subject, RuleOperator.Contains, "x")], [A(RuleActionKind.Flag)]), Tags));
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("r", RuleMatch.All, [], [A(RuleActionKind.Flag)]), Tags)); // no conditions
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("r", RuleMatch.All, [C(RuleField.Body, RuleOperator.StartsWith, "x")], [A(RuleActionKind.Flag)]), Tags));
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("r", RuleMatch.All, [C(RuleField.Subject, RuleOperator.Contains, new string('x', 201))], [A(RuleActionKind.Flag)]), Tags));
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("r", RuleMatch.All, [C(RuleField.Subject, RuleOperator.Contains, "x")], [A(RuleActionKind.Warn)]), Tags)); // outgoing only
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("r", RuleMatch.All, [C(RuleField.Subject, RuleOperator.Contains, "x")], [A(RuleActionKind.AddTag, "gone")]), Tags));
        Assert.Throws<RuleException>(() => RuleValidator.Validate(Rule("r", RuleMatch.All, [C(RuleField.Subject, RuleOperator.Contains, "x")], [A(RuleActionKind.MoveTo, "Lists")]), Tags)); // all accounts
        var ok = Rule("  ok ", RuleMatch.All, [], [A(RuleActionKind.Flag)]);
        ok.AllMessages = true;
        RuleValidator.Validate(ok, Tags);
        Assert.Equal("ok", ok.Name);
    }
}

/// <summary>The processor with the real stores and the sync coordinator (RULE-04, BLOCK-03, BLOCK-04, RULE-02, OPS-02).</summary>
public sealed class MailRuleProcessorTests : IAsyncDisposable
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly string _dir = Directory.CreateTempSubdirectory("alondra1-rules-").FullName;
    private SqlitePersonalStore _mail = null!;
    private SqliteTaskStore _tasks = null!;
    private TagBoard _tags = null!;
    private MailRuleService _rules = null!;
    private ScriptedGateway _gateway = null!;
    private SyncCoordinator _sync = null!;

    private static AccountConfig Account() => new()
    {
        Id = "acct-1", Kind = AccountKind.Manual, Mode = AccountMode.Personal, DisplayName = "Work", Address = "me@home.org",
        Incoming = new IncomingSettings { Host = "imap.home.org", Port = 993, Username = "me@home.org", Authentication = AuthenticationKind.None },
        Outgoing = new OutgoingSettings { Host = "smtp.home.org", Port = 587, Username = "me@home.org", Authentication = AuthenticationKind.None },
        Folders = new FolderMap
        {
            Inbox = "INBOX", Junk = "Junk", Archive = "Archive",
            Known = [new KnownFolder { Id = "INBOX", Name = "INBOX", SpecialUse = "inbox" }, new KnownFolder { Id = "Junk", Name = "Junk", SpecialUse = "junk" },
                new KnownFolder { Id = "Archive", Name = "Archive", SpecialUse = "archive" }],
        },
    };

    private async Task OpenAsync()
    {
        _mail = new SqlitePersonalStore(Path.Combine(_dir, "personal.db"));
        await _mail.InitializeAsync(CancellationToken.None);
        _tasks = new SqliteTaskStore(Path.Combine(_dir, "todo.db"));
        await _tasks.InitializeAsync();
        _tags = new TagBoard(_tasks, _clock);
        await _tags.LoadAsync();
        _rules = new MailRuleService(_mail, _tags, _clock);
        await _rules.LoadAsync();
        _gateway = new ScriptedGateway();
        _sync = new SyncCoordinator(_gateway, _mail, new SessionCache(), new NoSecrets(), new SameTokens(), _clock, new NoConflicts()) { Rules = _rules };
    }

    public async ValueTask DisposeAsync()
    {
        await _mail.DisposeAsync();
        await _tasks.DisposeAsync();
    }

    private static StoredMessage Mail(long uid, string from, string subject) => new()
    {
        FolderId = "INBOX", Uid = uid, MessageId = $"<m{uid}@test>", Subject = subject, FromAddress = from, FromName = from,
        Recipients = "me@home.org", ReceivedUtc = DateTimeOffset.Parse("2026-11-28T08:00:00Z"), IsUnread = true,
    };

    private Task<IReadOnlyList<StoredMessage>> Folder(string id) => _mail.ListAsync("acct-1", id, CancellationToken.None);

    [Fact]
    public async Task IncomingRulesRunOnceOnNewMailNeverOnTheFirstDownload()
    {
        await OpenAsync();
        var account = Account();
        var news = await _tags.CreateAsync("News");
        await _rules.SaveRuleAsync(new MailRule { Name = "Newsletters", Enabled = true, Direction = RuleDirection.Incoming,
            Conditions = [new() { Field = RuleField.From, Operator = RuleOperator.EndsWith, Value = "@lists.test" }],
            Actions = [new() { Kind = RuleActionKind.AddTag, Value = news.Id }, new() { Kind = RuleActionKind.MarkRead }, new() { Kind = RuleActionKind.Archive }] });
        await _rules.SaveBlockAsync(new BlockEntry { Kind = BlockKind.Domain, Value = "spam.test" });

        // First download: history, untouched.
        _gateway.Next = [Mail(1, "old@lists.test", "Old digest")];
        Assert.True((await _sync.SyncAsync(account, CancellationToken.None)).Ok);
        Assert.Single(await Folder("INBOX"));

        // New mail: the rule archives and tags, the blocklist quarantines (and wins over the rule).
        _gateway.Next = [Mail(2, "weekly@lists.test", "Weekly digest"), Mail(3, "x@spam.test", "Win big"), Mail(4, "friend@home.org", "Lunch?")];
        Assert.True((await _sync.SyncAsync(account, CancellationToken.None)).Ok);
        var archived = Assert.Single(await Folder("Archive"));
        Assert.Equal("Weekly digest", archived.Subject);
        Assert.False(archived.IsUnread);
        Assert.Equal("News", Assert.Single(_tags.TagsOf(TagRules.Mail, TagRules.MailRef("acct-1", "<m2@test>", "INBOX", 2))).Name);
        Assert.Equal("Win big", Assert.Single(await Folder("Junk")).Subject);
        Assert.Equal(["Lunch?", "Old digest"], (await Folder("INBOX")).Select(m => m.Subject).Order());
        var quarantined = Assert.Single(await _rules.QuarantineAsync());
        Assert.Equal("Blocked domain: spam.test", quarantined.Reason);
        Assert.Equal(1, _rules.Blocks.Single().HitCount);

        // RULE-04: the same messages seen again (a sync echo) do nothing more.
        var activity = await _rules.ActivityAsync();
        _gateway.Next = [Mail(2, "weekly@lists.test", "Weekly digest"), Mail(3, "x@spam.test", "Win big")];
        await _rules.ProcessIncomingAsync(account, _gateway.Next, _ => Task.FromResult<StoredBody?>(null), _ => throw new InvalidOperationException("no side effects"), CancellationToken.None);
        Assert.Equal(activity.Count, (await _rules.ActivityAsync()).Count);
        Assert.DoesNotContain(await _rules.ActivityAsync(), e => e.Summary.Contains("Win big", StringComparison.Ordinal)); // no subjects in activity

        // BLOCK-04: Restore puts it back and exempts it; the entry still blocks other messages.
        await _rules.RestoreAsync(quarantined, unblock: false, command => _sync.ApplyLocalCommandAsync(account, command, CancellationToken.None));
        Assert.Contains(await Folder("INBOX"), m => m.Subject == "Win big");
        Assert.Empty(await _rules.QuarantineAsync());
        Assert.Single(_rules.Blocks);
        Assert.True(await _mail.IsExemptAsync(quarantined.Ref, _clock.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task OutgoingGuardBlocksAndHoldsUnconfirmedWarningsWithoutCallingTheProvider()
    {
        await OpenAsync();
        var account = Account();
        await _rules.SaveRuleAsync(new MailRule { Name = "No passwords", Enabled = true, Direction = RuleDirection.Outgoing,
            Conditions = [new() { Field = RuleField.Body, Operator = RuleOperator.Contains, Value = "password" }],
            Actions = [new() { Kind = RuleActionKind.Block, Value = "Don't send passwords by mail." }] });
        await _rules.SaveRuleAsync(new MailRule { Name = "Outside", Enabled = true, Direction = RuleDirection.Outgoing,
            Conditions = [new() { Field = RuleField.Recipients, Operator = RuleOperator.EndsWith, Value = "@partner.test" }],
            Actions = [new() { Kind = RuleActionKind.Warn, Value = "This goes outside." }] });

        var secret = new OutgoingMail { To = ["a@partner.test"], Subject = "Login", TextBody = "the password is x" };
        var result = await _sync.SendAsync(account, secret, CancellationToken.None);
        Assert.Equal(SmtpAcceptance.Failed, result.Acceptance);
        Assert.Contains("No passwords", result.Error);
        Assert.Equal(0, _gateway.Sent);
        Assert.Empty(await _mail.ListOperationsAsync("acct-1", CancellationToken.None)); // nothing queued; the draft stays with the user

        var outside = new OutgoingMail { To = ["a@partner.test"], Subject = "Hi", TextBody = "hello" };
        Assert.Equal(SmtpAcceptance.Failed, (await _sync.SendAsync(account, outside, CancellationToken.None)).Acceptance);
        var verdict = _rules.CheckOutgoing(account, outside);
        Assert.Single(verdict.Warnings);
        outside.RuleApproval = verdict.ApprovalKey;
        outside.TextBody = "hello, changed after confirming";
        Assert.Equal(SmtpAcceptance.Failed, (await _sync.SendAsync(account, outside, CancellationToken.None)).Acceptance); // confirmation invalidated
        outside.RuleApproval = _rules.CheckOutgoing(account, outside).ApprovalKey;
        Assert.Equal(SmtpAcceptance.Accepted, (await _sync.SendAsync(account, outside, CancellationToken.None)).Acceptance);
        Assert.Equal(1, _gateway.Sent);
    }

    [Fact]
    public async Task ARuleWhoseTagIsDeletedIsSuspendedNotSilentlyActing()
    {
        await OpenAsync();
        var news = await _tags.CreateAsync("News");
        var rule = await _rules.SaveRuleAsync(new MailRule { Name = "Tag news", Enabled = true, Direction = RuleDirection.Incoming,
            Conditions = [new() { Field = RuleField.Subject, Operator = RuleOperator.Contains, Value = "digest" }],
            Actions = [new() { Kind = RuleActionKind.AddTag, Value = news.Id }] });
        Assert.Equal(1, _rules.RulesUsing(news.Id));
        await _tags.DeleteAsync(news.Id);
        await _rules.SuspendForMissingTagsAsync();
        var after = _rules.Rules.Single(r => r.Id == rule.Id);
        Assert.False(after.Active);
        Assert.Contains("tag", after.SuspendedReason);
        // Kept after a restart.
        var reloaded = new MailRuleService(_mail, _tags, _clock);
        await reloaded.LoadAsync();
        Assert.NotNull(reloaded.Rules.Single().SuspendedReason);
    }
}

internal sealed class ScriptedGateway : IMailGateway
{
    public List<StoredMessage> Next { get; set; } = [];
    public int Sent { get; private set; }
    private long _top;

    public Task<ProbeResult> ProbeIncomingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) => Task.FromResult(new ProbeResult(true, "ok", null));
    public Task<ProbeResult> ProbeOutgoingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) => Task.FromResult(new ProbeResult(true, "ok", null));

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteFolder>>([new RemoteFolder { Id = "INBOX", Name = "INBOX", SpecialUse = "inbox", UidValidity = 7 }]);

    public Task<RemoteSyncBatch> FetchChangesAsync(AccountConfig account, SecretBundle secrets, RemoteFolder folder, string? checkpoint, int? historyLimit, CancellationToken cancellationToken)
    {
        var upserts = Next.Select(m => new StoredMessage
        {
            FolderId = folder.Id, Uid = m.Uid, MessageId = m.MessageId, Subject = m.Subject, FromAddress = m.FromAddress, FromName = m.FromName,
            Recipients = m.Recipients, ReceivedUtc = m.ReceivedUtc, IsUnread = m.IsUnread,
        }).ToList();
        _top = Math.Max(_top, upserts.Select(m => m.Uid).DefaultIfEmpty(0).Max());
        Next = [];
        return Task.FromResult(new RemoteSyncBatch { NextCheckpoint = $"v=7;uid={_top};mod=0", Upserts = upserts, RemoteUidsComplete = false });
    }

    public Task<StoredBody> FetchBodyAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, CancellationToken cancellationToken) => Task.FromResult(new StoredBody { Text = "body" });
    public Task FetchRawAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task FetchAttachmentAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, string fileName, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ApplyAsync(AccountConfig account, SecretBundle secrets, RemoteMailCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SendResult> SendAsync(AccountConfig account, SecretBundle secrets, OutgoingMail mail, CancellationToken cancellationToken)
    {
        Sent++;
        return Task.FromResult(new SendResult(SmtpAcceptance.Accepted, MessageIdentity.Ensure(mail.MessageId), null, false));
    }

    public Task IdleAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeletePopMessagesAsync(AccountConfig account, SecretBundle secrets, IReadOnlyList<string> uidls, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class NoSecrets : ISecretStore
{
    public Task<SecretBundle> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult(new SecretBundle());
    public Task SaveAsync(string accountId, SecretBundle bundle, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class SameTokens : ITokenRefresher
{
    public Task<SecretBundle> EnsureFreshAsync(AccountConfig account, SecretBundle secrets, TokenAudience audience, CancellationToken cancellationToken) => Task.FromResult(secrets);
}

file sealed class NoConflicts : IConflictSink
{
    public void ReportPreservedBoth(string description) { }
}
