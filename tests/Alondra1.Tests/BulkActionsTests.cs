using Alondra1.Core.Config;
using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §3.2 / T05: mixed-account bulk moves use each message's own account and never a guessed folder.</summary>
public class BulkActionsTests
{
    private static AccountConfig Imap(string id, string name, string? archive)
    {
        var a = new AccountConfig { Id = id, DisplayName = name, Address = $"{id}@example.test", Incoming = new IncomingSettings { Protocol = MailProtocol.Imap } };
        a.Folders.Inbox = "INBOX";
        a.Folders.Trash = "INBOX.Trash";
        a.Folders.Archive = archive;
        a.Folders.Known = [new KnownFolder { Id = "INBOX", Name = "INBOX" }, new KnownFolder { Id = "INBOX.Trash", Name = "Trash" }];
        if (archive is not null)
            a.Folders.Known.Add(new KnownFolder { Id = archive, Name = "Archive" });
        return a;
    }

    private static StoredMessage M(string account, long uid, string folder = "INBOX") => new() { AccountId = account, FolderId = folder, Uid = uid };

    [Fact]
    public void EachMessageGoesToItsOwnAccountsFolder()
    {
        var work = Imap("w", "Work", "INBOX.Archive");
        var home = Imap("h", "Home", "Archives/2026");
        var plan = BulkActions.Plan([M("w", 1), M("h", 1), M("w", 2)], [work, home], FolderRole.Archive);
        Assert.Empty(plan.Skipped);
        Assert.Equal(3, plan.Steps.Count);
        Assert.All(plan.Steps, s => Assert.Equal(s.Message.AccountId, s.Account.Id));
        Assert.Equal("Archives/2026", plan.Steps.Single(s => s.Account.Id == "h").Destination);
    }

    [Fact]
    public void AMissingFolderOrRemovedAccountIsReportedNotGuessedOrDropped()
    {
        var work = Imap("w", "Work", "INBOX.Archive");
        var personal = Imap("p", "Personal", archive: null);
        var plan = BulkActions.Plan([M("w", 1), M("p", 1), M("gone", 1), M("w", 9, "INBOX.Archive")], [work, personal], FolderRole.Archive);
        Assert.Single(plan.Steps);
        Assert.Contains(plan.Skipped, s => s.Message.AccountId == "p" && s.Reason == "Personal has no Archive folder on its server");
        Assert.Contains(plan.Skipped, s => s.Message.AccountId == "gone" && s.Reason == "its account was removed");
        Assert.Contains(plan.Skipped, s => s.Reason == "it is already in Archive");
    }

    [Fact]
    public void TheSummarySaysWhatWasAndWasNotDone()
    {
        Assert.Equal("Archived 3 messages.", BulkActions.Summary("Archived", 3, 3, []));
        Assert.Equal("Archived 1 of 3 messages. Not archived: 2: Personal has no Archive folder on its server.",
            BulkActions.Summary("Archived", 3, 1, ["Personal has no Archive folder on its server", "Personal has no Archive folder on its server"]));
    }
}
