using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Persistence;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §8.2: advanced Mail filters combine, need no syntax, and attachment names are matched safely.</summary>
public class MailAdvancedFilterTests
{
    private static StoredMessage M(string account, long uid, string from, string address, string to, string subject, DateTimeOffset at, bool unread = false) => new()
    {
        AccountId = account, FolderId = "INBOX", Uid = uid, FromName = from, FromAddress = address, Recipients = to, Subject = subject, ReceivedUtc = at, IsUnread = unread,
    };

    private static readonly DateTimeOffset Nov10 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiltersCombineAndEveryWordMustMatchIgnoringCase()
    {
        var m = M("w", 1, "Maya Chen", "maya@example.com", "you@work.example, liam@example.com", "Q4 Budget review", Nov10, unread: true);
        Assert.True(MailListView.Matches(m, new MailListFilter(Sender: "maya")));
        Assert.True(MailListView.Matches(m, new MailListFilter(Sender: "chen MAYA")));
        Assert.True(MailListView.Matches(m, new MailListFilter(Sender: "example.com")));
        Assert.True(MailListView.Matches(m, new MailListFilter(Recipient: "liam")));
        Assert.False(MailListView.Matches(m, new MailListFilter(Recipient: "ava")));
        Assert.True(MailListView.Matches(m, new MailListFilter(Unread: true, Subject: "budget", Sender: "maya")));
        Assert.False(MailListView.Matches(m, new MailListFilter(Unread: true, Subject: "invoice", Sender: "maya")));
        Assert.False(MailListView.Matches(m, new MailListFilter(AccountId: "h", Sender: "maya")));
    }

    [Fact]
    public void DateRangesAreInclusiveLocalDays()
    {
        var m = M("w", 1, "A", "a@x", "", "s", Nov10);
        var day = DateOnly.FromDateTime(Nov10.ToLocalTime().Date);
        Assert.True(MailListView.Matches(m, new MailListFilter(Since: day, Until: day)));
        Assert.True(MailListView.Matches(m, new MailListFilter(Since: day.AddDays(-3))));
        Assert.False(MailListView.Matches(m, new MailListFilter(Since: day.AddDays(1))));
        Assert.False(MailListView.Matches(m, new MailListFilter(Until: day.AddDays(-1))));
    }

    [Fact]
    public void NormalizingDropsBlankTextAndOrdersAReversedRange()
    {
        var f = new MailListFilter(Sender: "  ", Subject: " budget ", Since: new DateOnly(2026, 11, 20), Until: new DateOnly(2026, 11, 1)).Normalized();
        Assert.Null(f.Sender);
        Assert.Equal("budget", f.Subject);
        Assert.Equal(new DateOnly(2026, 11, 1), f.Since);
        Assert.Equal(new DateOnly(2026, 11, 20), f.Until);
        Assert.False(new MailListFilter(Sender: " ").Normalized().IsActive);
    }

    [Fact]
    public void AttachmentNameMatchesOnlyTheListedMessages()
    {
        var a = M("w", 1, "A", "a@x", "", "s", Nov10);
        var b = M("w", 2, "B", "b@x", "", "s", Nov10);
        var keys = new HashSet<MessageKey> { new("w", "INBOX", 1) };
        var f = new MailListFilter(AttachmentName: "invoice");
        Assert.Equal([1L], MailListView.Apply([a, b], f, MailSortField.Date, true, keys).Select(m => m.Uid));
        // Without a lookup the filter matches nothing rather than everything.
        Assert.Empty(MailListView.Apply([a, b], f, MailSortField.Date, true));
    }

    [Fact]
    public void EachAdvancedFilterIsARemovableChip()
    {
        var f = new MailListFilter(Sender: "maya", Recipient: "liam", Subject: "budget", Since: new DateOnly(2026, 11, 1), Until: new DateOnly(2026, 11, 27), AttachmentName: "pdf");
        var chips = MailListView.Chips(f, id => id);
        Assert.Equal(5, chips.Count);
        Assert.Contains(chips, c => c.Label == "From: maya" && c.Without.Sender is null && c.Without.Subject == "budget");
        Assert.Contains(chips, c => c.Label.StartsWith("Received ", StringComparison.Ordinal) && c.Without.Since is null && c.Without.Until is null);
        Assert.False(chips.Aggregate(f, (acc, c) => acc with
        {
            Sender = c.Without.Sender is null ? null : acc.Sender,
            Recipient = c.Without.Recipient is null ? null : acc.Recipient,
            Subject = c.Without.Subject is null ? null : acc.Subject,
            Since = c.Without.Since is null ? null : acc.Since,
            Until = c.Without.Until is null ? null : acc.Until,
            AttachmentName = c.Without.AttachmentName is null ? null : acc.AttachmentName,
        }).IsActive);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("2026-11-03", true)]
    [InlineData("not a date", false)]
    public void TypedDatesAcceptLocalAndIsoFormats(string text, bool ok) => Assert.Equal(ok, MailListView.TryParseDate(text, out _));

    [Fact]
    public void QuickRangesEndToday()
    {
        var today = new DateOnly(2026, 11, 27);
        Assert.All(MailListView.QuickRanges(today), r => Assert.Equal(today, r.Until));
        Assert.Contains(MailListView.QuickRanges(today), r => r.Label == "Last 7 days" && r.Since == new DateOnly(2026, 11, 21));
    }

    [Fact]
    public async Task TheStoreMatchesAttachmentNamesLiterallyPerAccount()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
        await store.InitializeAsync(CancellationToken.None);
        var batch = new PersonalBatch();
        batch.Upserts.Add(M("w", 1, "A", "a@x", "", "one", Nov10));
        batch.Upserts.Add(M("w", 2, "B", "b@x", "", "two", Nov10));
        batch.Upserts.Add(M("h", 1, "C", "c@x", "", "three", Nov10));
        StoredAttachment Att(string account, long uid, string name) => new() { Id = $"{account}{uid}{name}", AccountId = account, FolderId = "INBOX", Uid = uid, FileName = name };
        batch.Attachments.Add(Att("w", 1, "Invoice November.pdf"));
        batch.Attachments.Add(Att("w", 2, "100%_done.txt"));
        batch.Attachments.Add(Att("h", 1, "invoice-copy.pdf"));
        await store.CommitAsync(batch, CancellationToken.None);

        Assert.Equal(2, (await store.FindByAttachmentNameAsync(null, "invoice", CancellationToken.None)).Count);
        Assert.Equal([new MessageKey("w", "INBOX", 1)], await store.FindByAttachmentNameAsync("w", "november INVOICE", CancellationToken.None));
        // % and _ are matched as text, not as wildcards.
        Assert.Equal([new MessageKey("w", "INBOX", 2)], await store.FindByAttachmentNameAsync(null, "0%_", CancellationToken.None));
        Assert.Equal([new MessageKey("w", "INBOX", 2)], await store.FindByAttachmentNameAsync(null, "%", CancellationToken.None));
        Assert.Empty(await store.FindByAttachmentNameAsync(null, "   ", CancellationToken.None));
        await store.DisposeAsync();
    }

    [Fact]
    public void TheSessionCacheMatchesAttachmentNamesOfDownloadedBodies()
    {
        var cache = new SessionCache();
        var body = new StoredBody();
        body.Attachments.Add(new StoredAttachment { AccountId = "w", FolderId = "INBOX", Uid = 4, FileName = "budget-2027.xlsx" });
        cache.SetBody(new MessageKey("w", "INBOX", 4), body);
        Assert.Equal([new MessageKey("w", "INBOX", 4)], cache.FindByAttachmentName(null, "budget"));
        Assert.Empty(cache.FindByAttachmentName("h", "budget"));
    }
}
