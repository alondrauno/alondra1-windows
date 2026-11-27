using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Organizer;
using Alondra1.Core.Search;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §8 (SR-01…SR-03): scopes never broaden silently, sources stay apart, stale results are dropped.</summary>
public class GlobalSearchTests
{
    private static readonly AccountConfig Work = new() { Id = "w", DisplayName = "Work", Address = "me@work.example" };
    private static readonly AccountConfig Home = new() { Id = "h", DisplayName = "Work", Address = "me@home.example" };

    [Theory]
    [InlineData(SearchReach.All, SearchModule.Notes, SearchReach.All)]
    [InlineData(SearchReach.AllMail, SearchModule.Mail, SearchReach.AllMail)]
    [InlineData(SearchReach.AllMail, SearchModule.Todo, SearchReach.Module)]
    [InlineData(SearchReach.Module, SearchModule.Contacts, SearchReach.Module)]
    public void AnExplicitAllPersistsAcrossModulesAndAllMailNeverBroadens(SearchReach chosen, SearchModule module, SearchReach expected) =>
        Assert.Equal(expected, SearchScopes.Effective(chosen, module));

    [Fact]
    public void PlaceholdersNameTheScope()
    {
        Assert.Equal("Search mail", SearchScopes.Placeholder(SearchReach.Module, SearchModule.Mail));
        Assert.Equal("Search tasks", SearchScopes.Placeholder(SearchReach.Module, SearchModule.Todo));
        Assert.Equal("Search calendar", SearchScopes.Placeholder(SearchReach.Module, SearchModule.Calendar));
        Assert.Equal("Search all mail", SearchScopes.Placeholder(SearchReach.AllMail, SearchModule.Mail));
        Assert.Equal("Search all", SearchScopes.Placeholder(SearchReach.All, SearchModule.Contacts));
    }

    [Fact]
    public void AllSearchesTheFiveModulesAndNeverChat()
    {
        Assert.Equal([SearchModule.Mail, SearchModule.Calendar, SearchModule.Todo, SearchModule.Notes, SearchModule.Contacts],
            SearchScopes.PanelModules(SearchReach.All, SearchModule.Mail));
        Assert.Null(SearchScopes.FromWorkspace("Chat"));
        Assert.Empty(SearchScopes.PanelModules(SearchReach.Module, SearchModule.Mail));
        Assert.Equal([SearchModule.Calendar], SearchScopes.PanelModules(SearchReach.Module, SearchModule.Calendar));
    }

    [Fact]
    public void UnknownSavedReachFallsBackToTheModule() => Assert.Equal(SearchReach.Module, SearchScopes.ParseReach("Everything"));

    [Fact]
    public void EveryTermMustMatchSomeField()
    {
        var terms = SearchText.Terms("  budget   \"maya\" ");
        Assert.Equal(["budget", "maya"], terms);
        Assert.True(SearchText.Matches(terms, "Q4 Budget", "Maya Chen"));
        Assert.False(SearchText.Matches(terms, "Q4 Budget", "Liam"));
        Assert.False(SearchText.Matches([], "anything"));
    }

    [Fact]
    public void SnippetsAreSingleLinePlainTextAroundTheMatch()
    {
        var text = new string('a', 300) + "\n<b>needle</b>\r\n" + new string('z', 300);
        var snippet = SearchText.Snippet(text, ["needle"]);
        Assert.Contains("<b>needle</b>", snippet);
        Assert.DoesNotContain('\n', snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.True(snippet.Length <= 124);
    }

    private sealed class Source(SearchModule module, Func<CancellationToken, Task<SearchGroup>> run) : ISearchSource
    {
        public SearchModule Module => module;
        public Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken) => run(cancellationToken);
    }

    private static SearchGroup Group(SearchModule module, params string[] titles) =>
        new(module, titles.Select(t => new SearchHit(module, t, t, "", "", null)).ToList(), titles.Length, "test");

    [Fact]
    public async Task AFailingModuleDoesNotHideTheOthers()
    {
        var reported = new List<SearchGroup>();
        var ok = await new SearchCoordinator().RunAsync("x",
        [
            new Source(SearchModule.Mail, _ => Task.FromResult(Group(SearchModule.Mail, "m1"))),
            new Source(SearchModule.Notes, _ => throw new InvalidOperationException("index broken")),
        ], 8, reported.Add);
        Assert.True(ok);
        Assert.Equal(2, reported.Count);
        Assert.Single(reported, g => g.Module == SearchModule.Mail && g.Hits.Count == 1);
        var failed = Assert.Single(reported, g => g.Failed);
        Assert.Equal(SearchModule.Notes, failed.Module);
        Assert.DoesNotContain("index broken", failed.Coverage);
    }

    [Fact]
    public async Task ANewerQueryDropsTheOlderResults()
    {
        var coordinator = new SearchCoordinator();
        var gate = new TaskCompletionSource();
        var reported = new List<SearchGroup>();
        var slow = coordinator.RunAsync("old", [new Source(SearchModule.Mail, async ct =>
        {
            await gate.Task;
            return Group(SearchModule.Mail, "stale");
        })], 8, reported.Add);
        var fresh = await coordinator.RunAsync("new", [new Source(SearchModule.Mail, _ => Task.FromResult(Group(SearchModule.Mail, "fresh")))], 8, reported.Add);
        gate.SetResult();
        Assert.False(await slow);
        Assert.True(fresh);
        Assert.Equal("fresh", Assert.Single(reported).Hits[0].Title);
    }

    private static StoredMessage Message(string account, string folder, long uid, string subject, int day, bool body = true) => new()
    {
        AccountId = account, FolderId = folder, Uid = uid, Subject = subject, FromName = "Maya", FromAddress = "maya@example.com",
        ReceivedUtc = new DateTimeOffset(2026, 11, day, 9, 0, 0, TimeSpan.Zero), Preview = "Quarterly budget review", BodyDownloaded = body,
    };

    [Fact]
    public async Task MailResultsKeepAccountAndFolderIdentityAndNeverMergeAccounts()
    {
        var found = new[]
        {
            Message("w", "INBOX", 1, "Budget", 3),
            Message("h", "INBOX", 1, "Budget", 3),
            Message("w", "INBOX", 1, "Budget", 3), // the same message from the index and the session cache
            Message("gone", "INBOX", 9, "Budget", 4), // an account that was removed is never shown
        };
        var source = new MailSearchSource((_, _) => Task.FromResult<IReadOnlyList<StoredMessage>>(found), [Work, Home]);
        var group = await source.SearchAsync("budget", 8, CancellationToken.None);
        Assert.Equal(2, group.Total);
        Assert.Equal(["h/INBOX/1", "w/INBOX/1"], group.Hits.Select(h => h.ObjectId).Order());
        // Two accounts called "Work" are told apart by address.
        Assert.Contains(group.Hits, h => h.Source == "Work (me@work.example) · Inbox" && h.AccountId == "w" && h.FolderId == "INBOX" && h.Uid == 1);
        Assert.Contains(group.Hits, h => h.Source == "Work (me@home.example) · Inbox" && h.AccountId == "h");
    }

    [Fact]
    public async Task MailCoverageSaysWhenOnlyHeadersWereSearched()
    {
        var source = new MailSearchSource((_, _) => Task.FromResult<IReadOnlyList<StoredMessage>>([Message("w", "INBOX", 1, "Budget", 3, body: false)]), [Work]);
        var group = await source.SearchAsync("budget", 8, CancellationToken.None);
        Assert.Contains("body not downloaded", group.Coverage);
    }

    [Fact]
    public async Task MailResultsAreNewestFirstWithAStableTieBreak()
    {
        var found = new[] { Message("w", "B", 1, "x", 2), Message("w", "A", 5, "x", 2), Message("w", "A", 7, "x", 4) };
        var group = await new MailSearchSource((_, _) => Task.FromResult<IReadOnlyList<StoredMessage>>(found), [Work]).SearchAsync("x", 8, CancellationToken.None);
        Assert.Equal(["w/A/7", "w/A/5", "w/B/1"], group.Hits.Select(h => h.ObjectId));
    }

    [Fact]
    public async Task ContactsWithTheSameNameStayApartByAccount()
    {
        var contacts = new List<ContactEntry>
        {
            new() { Id = "1", AccountId = "w", AccountName = "Work", Name = "Ava Patel", Email = "ava@work.example" },
            new() { Id = "1", AccountId = "h", AccountName = "Home", Name = "Ava Patel", Email = "ava@home.example" },
            new() { Id = "2", AccountId = "h", AccountName = "Home", Name = "Liam", Email = "liam@example.com" },
        };
        var group = await new ContactSearchSource(() => contacts, () => "loaded").SearchAsync("ava", 8, CancellationToken.None);
        Assert.Equal(2, group.Total);
        Assert.Equal(["Home", "Work"], group.Hits.Select(h => h.Source).Order());
    }

    [Fact]
    public async Task CalendarCoverageIsHonestAboutWhatWasLoaded()
    {
        var group = await new CalendarSearchSource(() => [], () => "Calendar events aren't loaded on this PC yet.").SearchAsync("standup", 8, CancellationToken.None);
        Assert.Equal(0, group.Total);
        Assert.Contains("aren't loaded", group.Coverage);
    }

    [Fact]
    public async Task ResultsAreCappedButTheTotalIsKept()
    {
        var events = Enumerable.Range(1, 12).Select(i => new CalendarEntry { Id = $"e{i}", Subject = "Standup", Start = new DateTimeOffset(2026, 11, i, 9, 0, 0, TimeSpan.Zero) }).ToList();
        var group = await new CalendarSearchSource(() => events, () => "").SearchAsync("standup", 8, CancellationToken.None);
        Assert.Equal(12, group.Total);
        Assert.Equal(8, group.Hits.Count);
    }

    [Fact]
    public void FolderLabelsUseRolesThenServerNames()
    {
        var account = new AccountConfig { Id = "w", Incoming = new IncomingSettings { Protocol = MailProtocol.Imap } };
        account.Folders.Inbox = "INBOX";
        account.Folders.Sent = "INBOX.Sent";
        account.Folders.Known = [new KnownFolder { Id = "INBOX", Name = "INBOX" }, new KnownFolder { Id = "INBOX.Sent", Name = "Sent" }, new KnownFolder { Id = "INBOX.Projects", Name = "Projects" }];
        Assert.Equal("Inbox", SpecialFolders.LabelOf(account, "INBOX"));
        Assert.Equal(FolderRole.Sent, SpecialFolders.RoleOf(account, "INBOX.Sent"));
        Assert.Equal("Projects", SpecialFolders.LabelOf(account, "INBOX.Projects"));
        Assert.Equal(FolderRole.Other, SpecialFolders.RoleOf(account, "INBOX.Projects"));
    }
}
