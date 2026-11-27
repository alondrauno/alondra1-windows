using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §5.1 (ML-01): filters, sort with stable ties, sender fallback and filter chips.</summary>
public class MailListViewTests
{
    private static StoredMessage M(string account, long uid, int day, string from = "", string address = "", string subject = "", bool unread = false, bool flagged = false, bool attach = false) => new()
    {
        AccountId = account, FolderId = "INBOX", Uid = uid, ReceivedUtc = new DateTimeOffset(2026, 11, day, 8, 0, 0, TimeSpan.Zero),
        FromName = from, FromAddress = address, Subject = subject, IsUnread = unread, IsFlagged = flagged, HasAttachments = attach,
    };

    private static readonly StoredMessage[] Rows =
    [
        M("w", 1, 3, "Maya", subject: "Re: Budget", unread: true),
        M("h", 2, 3, "", "zoe@example.com", "Agenda", attach: true),
        M("w", 3, 5, "Liam", subject: "Fwd: Contract", flagged: true, unread: true),
        M("h", 4, 1, "", "", "Budget"),
    ];

    [Fact]
    public void FiltersCombine()
    {
        Assert.Equal([3L, 1L], MailListView.Apply(Rows, new MailListFilter(Unread: true), MailSortField.Date, true).Select(m => m.Uid));
        Assert.Equal([3L], MailListView.Apply(Rows, new MailListFilter(Unread: true, Flagged: true), MailSortField.Date, true).Select(m => m.Uid));
        Assert.Equal([2L], MailListView.Apply(Rows, new MailListFilter(Attachments: true), MailSortField.Date, true).Select(m => m.Uid));
        Assert.Equal([2L, 4L], MailListView.Apply(Rows, new MailListFilter(AccountId: "h"), MailSortField.Date, true).Select(m => m.Uid));
        Assert.False(MailListFilter.None.IsActive);
    }

    [Fact]
    public void DateSortIsStableForEqualTimes()
    {
        // uid 1 (w) and 2 (h) arrive at the same time: account order breaks the tie, the same way every time.
        Assert.Equal([3L, 2L, 1L, 4L], MailListView.Apply(Rows, MailListFilter.None, MailSortField.Date, true).Select(m => m.Uid));
        Assert.Equal([4L, 2L, 1L, 3L], MailListView.Apply(Rows, MailListFilter.None, MailSortField.Date, false).Select(m => m.Uid));
    }

    [Fact]
    public void SenderSortUsesNameThenAddressThenUnknown()
    {
        Assert.Equal("zoe@example.com", MailListView.SenderName(Rows[1]));
        Assert.Equal("(unknown sender)", MailListView.SenderName(Rows[3]));
        Assert.Equal([4L, 3L, 1L, 2L], MailListView.Apply(Rows, MailListFilter.None, MailSortField.From, false).Select(m => m.Uid));
    }

    [Fact]
    public void SubjectSortIgnoresReplyAndForwardPrefixes()
    {
        Assert.Equal("Budget", MailListView.SubjectKey(Rows[0]));
        Assert.Equal("Contract", MailListView.SubjectKey(Rows[2]));
        Assert.Equal("x", MailListView.SubjectKey(new StoredMessage { Subject = "RE: Fw: AW: x" }));
        Assert.Equal("Ticket: 42", MailListView.SubjectKey(new StoredMessage { Subject = "Ticket: 42" }));
        // Budget (newer "Re: Budget" first on equal keys), Agenda, Contract
        Assert.Equal([2L, 1L, 4L, 3L], MailListView.Apply(Rows, MailListFilter.None, MailSortField.Subject, false).Select(m => m.Uid));
    }

    [Fact]
    public void ChipsRemoveOneFilterEach()
    {
        var filter = new MailListFilter(Unread: true, Attachments: true, AccountId: "w");
        var chips = MailListView.Chips(filter, id => id == "w" ? "Work" : id);
        Assert.Equal(["Unread", "Has attachments", "Account: Work"], chips.Select(c => c.Label));
        Assert.Equal(filter with { Unread = false }, chips[0].Without);
        Assert.Null(chips[2].Without.AccountId);
    }
}
