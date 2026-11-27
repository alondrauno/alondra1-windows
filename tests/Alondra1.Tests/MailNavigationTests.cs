using Alondra1.Core.Config;
using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §3 (T03, T04): the account and folder tree, stable scopes, missing folders, unread basis.</summary>
public class MailNavigationTests
{
    private static AccountConfig Account(string id, string name, string address, params RemoteFolder[] folders)
    {
        var account = new AccountConfig
        {
            Id = id,
            DisplayName = name,
            Address = address,
            Incoming = new IncomingSettings { Protocol = MailProtocol.Imap, Host = "mail.example.com" },
            Sync = new SyncPreferences { SelectedFolders = ["Inbox", "Sent"] },
        };
        if (folders.Length > 0)
            SpecialFolders.Learn(account, folders);
        return account;
    }

    private static readonly RemoteFolder[] Standard =
    [
        new() { Id = "INBOX", Name = "INBOX", SpecialUse = "inbox", Delimiter = "/" },
        new() { Id = "Sent", Name = "Sent", SpecialUse = "sent", Delimiter = "/" },
        new() { Id = "Drafts", Name = "Drafts", SpecialUse = "drafts", Delimiter = "/" },
        new() { Id = "Trash", Name = "Trash", SpecialUse = "trash", Delimiter = "/" },
        new() { Id = "Projects", Name = "Projects", Delimiter = "/" },
        new() { Id = "Projects/2026", Name = "2026", Delimiter = "/" },
        new() { Id = "Projects/2026/Q4", Name = "Q4", Delimiter = "/" },
    ];

    [Theory]
    [InlineData("unified/Inbox")]
    [InlineData("unified/Junk")]
    [InlineData("account/abc/Sent")]
    [InlineData("account/abc/folder/Projects/2026")]
    public void ScopeKeysRoundTrip(string key) => Assert.Equal(key, MailScope.Parse(key)!.Key);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("All accounts")]
    [InlineData("unified/Other")]
    [InlineData("account//Inbox")]
    [InlineData("account/abc/folder/")]
    public void BadScopeKeysAreRejected(string? key) => Assert.Null(MailScope.Parse(key));

    [Fact]
    public void MissingArchiveIsMissingNotAnotherFolder()
    {
        var account = Account("a", "Work", "me@work.example", Standard);
        Assert.Null(SpecialFolders.TryResolve(account, FolderRole.Archive));
        Assert.Null(SpecialFolders.TryResolve(account, FolderRole.Junk));
        Assert.Equal("Trash", SpecialFolders.TryResolve(account, FolderRole.Trash));
        // Before the first sync the usual names are assumed (as before), and Junk is not.
        var fresh = Account("b", "New", "new@example.com");
        Assert.Equal("Archive", SpecialFolders.TryResolve(fresh, FolderRole.Archive));
        Assert.Null(SpecialFolders.TryResolve(fresh, FolderRole.Junk));
    }

    [Fact]
    public void LearnsJunkAndTheFolderList()
    {
        var account = Account("a", "Work", "me@work.example", [.. Standard, new RemoteFolder { Id = "Spam", Name = "Spam", SpecialUse = "junk" }]);
        Assert.Equal("Spam", SpecialFolders.TryResolve(account, FolderRole.Junk));
        Assert.Equal(8, account.Folders.Known.Count);
        Assert.False(SpecialFolders.Learn(account, [.. Standard, new RemoteFolder { Id = "Spam", Name = "Spam", SpecialUse = "junk" }]));
    }

    [Fact]
    public void EveryAccountIsListedEvenWithoutMail()
    {
        var accounts = Enumerable.Range(1, 15).Select(i => Account($"id{i}", $"Account {i}", $"a{i}@example.com", Standard)).ToList();
        var tree = MailNavigation.Build(accounts, []);
        Assert.Equal(16, tree.Count); // All accounts + 15
        Assert.All(tree.Skip(1), node => Assert.True(node.IsAccount));
        Assert.Equal(accounts.Select(a => a.Id), tree.Skip(1).Select(n => n.AccountId));
        // Nothing synced yet: Inbox and Sent are kept here, so their count is 0, not unknown.
        Assert.Equal(0, tree[1].Unread);
    }

    [Fact]
    public void UnifiedUnreadAddsInboxesWithoutDoubleCounting()
    {
        var a = Account("a", "Work", "me@work.example", Standard);
        var b = Account("b", "Home", "me@home.example", Standard);
        var counts = new[]
        {
            new FolderCount("a", "INBOX", 10, 3),
            new FolderCount("b", "INBOX", 5, 2),
            new FolderCount("a", "Sent", 4, 0),
            new FolderCount("a", "Projects", 7, 7), // not synced, and not part of the inbox basis
        };
        var tree = MailNavigation.Build([a, b], counts);
        var unifiedInbox = tree[0].Children.Single(n => n.Role == FolderRole.Inbox);
        Assert.Equal(5, unifiedInbox.Unread);
        Assert.Equal(3, tree[1].Unread);
        Assert.Equal(2, tree[2].Unread);
        Assert.Equal("5", unifiedInbox.UnreadText);
    }

    [Fact]
    public void UnknownCountIsNotZero()
    {
        var a = Account("a", "Work", "me@work.example", Standard);
        var tree = MailNavigation.Build([a], []);
        var drafts = tree[1].Children.Single(n => n.Role == FolderRole.Drafts);
        Assert.Null(drafts.Unread); // not synced on this PC
        Assert.True(drafts.NotSynced);
        Assert.Contains("not known", drafts.AccessName);
    }

    [Fact]
    public void ArchiveInUnifiedSaysWhichAccountsHaveIt()
    {
        var a = Account("a", "Work", "me@work.example", Standard);
        var b = Account("b", "Home", "me@home.example", [.. Standard, new RemoteFolder { Id = "Archive", Name = "Archive", SpecialUse = "archive" }]);
        var tree = MailNavigation.Build([a, b], []);
        var archive = tree[0].Children.Single(n => n.Role == FolderRole.Archive);
        Assert.Equal("1 of 2 accounts have this folder", archive.Detail);
        Assert.DoesNotContain(tree[1].Children, n => n.Role == FolderRole.Archive);
        Assert.Contains(tree[2].Children, n => n.Role == FolderRole.Archive);
    }

    [Fact]
    public void CustomFoldersNestAndKeepTheirIds()
    {
        var a = Account("a", "Work", "me@work.example", Standard);
        var tree = MailNavigation.Build([a], []);
        var projects = tree[1].Children.Single(n => n.Label == "Projects");
        var q4 = projects.Children.Single().Children.Single();
        Assert.Equal("Q4", q4.Label);
        Assert.Equal(new MailScope("a", FolderRole.Other, "Projects/2026/Q4"), q4.Scope);
        Assert.Equal(3, q4.Depth);
    }

    [Fact]
    public void FoldersNestedUnderInboxSitUnderTheAccount()
    {
        var a = Account("a", "Work", "me@work.example",
            new RemoteFolder { Id = "INBOX", Name = "INBOX", SpecialUse = "inbox", Delimiter = "." },
            new RemoteFolder { Id = "INBOX.Clients", Name = "Clients", Delimiter = "." },
            new RemoteFolder { Id = "INBOX.Sent", Name = "Sent", SpecialUse = "sent", Delimiter = "." });
        var tree = MailNavigation.Build([a], []);
        var clients = tree[1].Children.Single(n => n.Label == "Clients");
        Assert.Equal(1, clients.Depth);
        Assert.Equal("INBOX.Clients", clients.Scope!.FolderId);
    }

    [Fact]
    public void DuplicateAccountNamesAreDistinguishedByAddress()
    {
        var a = Account("a", "Nikita", "n@work.example", Standard);
        var b = Account("b", "Nikita", "n@home.example", Standard);
        var tree = MailNavigation.Build([a, b], []);
        Assert.Equal("Nikita (n@work.example)", tree[1].Label);
        Assert.Equal("Nikita (n@home.example)", tree[2].Label);
        Assert.NotEqual(tree[1].Scope, tree[2].Scope);
    }

    [Fact]
    public void NoAccountsStillShowsTheUnifiedFolders()
    {
        var tree = MailNavigation.Build([], []);
        Assert.Single(tree);
        Assert.Equal(0, tree[0].Children.Single(n => n.Role == FolderRole.Inbox).Unread);
    }
}
