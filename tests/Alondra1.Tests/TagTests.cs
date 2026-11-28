using Alondra1.Core.Cloud;
using Alondra1.Core.Tags;
using Alondra1.Persistence;

namespace Alondra1.Tests;

/// <summary>FEATURES_EXTENSION Part B §2 (TAG-01..03, Windows side): shared rules with the cloud, board, store and outbox.</summary>
public sealed class TagTests : IAsyncDisposable
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
    private string _dir = null!;
    private SqliteTaskStore _store = null!;
    private TagBoard _tags = null!;

    private async Task OpenAsync(bool recording = false)
    {
        _dir ??= Directory.CreateTempSubdirectory("alondra1-tags-").FullName;
        _store = new SqliteTaskStore(Path.Combine(_dir, "todo.db"));
        await _store.InitializeAsync();
        if (recording)
            await _store.SetRecordingAsync(true, CancellationToken.None);
        _tags = new TagBoard(_store, _clock);
        await _tags.LoadAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
            await _store.DisposeAsync();
    }

    [Fact]
    public void LinkIdsAndNameKeysMatchTheCloud()
    {
        // Reference values computed by the backend (internal/tasks.TagLinkID / TagNameKey).
        Assert.Equal("tl__JvvuZgM8P4jKZwUvr_viMw3QiHi74LQF5fzj6XE", TagRules.LinkId("travel", "mail", "acct-1|INBOX|42"));
        Assert.Equal("tl_Y8jUgutivQVAStEgDYiFl-mZ8FENm3IBSoS6-vCD", TagRules.LinkId("t1", "note", "itinerary"));
        Assert.Equal("strasse", TagRules.NameKey("  Straße "));
        Assert.Equal(TagRules.NameKey("Café"), TagRules.NameKey("CAFÉ"));
    }

    [Fact]
    public void MailRefsFollowTheMessageNotTheFolderAndHideIt()
    {
        var inbox = TagRules.MailRef("acct-1", "<abc@example.com>", "INBOX", 42);
        Assert.Equal(inbox, TagRules.MailRef("acct-1", "abc@example.com", "Archive", 7));
        Assert.NotEqual(inbox, TagRules.MailRef("acct-2", "abc@example.com", "INBOX", 42));
        Assert.DoesNotContain("example", inbox);
        Assert.Matches("^[!-~]{1,256}$", inbox);
        Assert.NotEqual(TagRules.MailRef("acct-1", null, "INBOX", 1), TagRules.MailRef("acct-1", null, "Archive", 1));
    }

    [Theory]
    [InlineData("", "Enter a name")]
    [InlineData("a\u0007b", "control")]
    public void BadNamesGetAPlainReason(string name, string reason) =>
        Assert.Contains(reason, Assert.Throws<TagException>(() => TagRules.CleanName(name)).Message);

    [Fact]
    public async Task OneTagAcrossItemTypesRenameAndDelete()
    {
        await OpenAsync();
        var travel = await _tags.CreateAsync("  Travel ", "blue");
        Assert.Equal("Travel", travel.Name);
        await Assert.ThrowsAsync<TagException>(() => _tags.CreateAsync("TRAVEL"));
        await Assert.ThrowsAsync<TagException>(() => _tags.SetColorAsync(travel.Id, "#ff0000"));

        var mail = TagRules.MailRef("acct-1", "<b@example.com>", "INBOX", 1);
        await _tags.SetTagsAsync(TagRules.Mail, [mail], [travel.Id], [new MailTagTarget(mail, "acct-1", "INBOX", 1, "Booking", _clock.UtcNow)]);
        await _tags.SetTagsAsync(TagRules.Note, ["itinerary"], [travel.Id]);
        await _tags.SetTagAsync(TagRules.Task, ["pack", "passport"], travel.Id, on: true);
        await _tags.SetTagsAsync(TagRules.Event, ["acct-1|flight"], [travel.Id]);
        Assert.Equal(5, _tags.UsageCount(travel.Id));
        Assert.Equal(["pack", "passport"], _tags.ItemsWith(TagRules.Task, [travel.Id]).Order());

        await _tags.RenameAsync(travel.Id, "Trips");
        Assert.Equal("Trips", Assert.Single(_tags.TagsOf(TagRules.Note, "itinerary")).Name);

        // Reload from disk: everything is there, including where the mail is.
        await _store.DisposeAsync();
        await OpenAsync();
        Assert.Equal("Trips", Assert.Single(_tags.Tags).Name);
        Assert.Equal("Booking", _tags.MailTarget(mail)!.Subject);

        await _tags.DeleteAsync(travel.Id);
        Assert.Empty(_tags.Tags);
        Assert.Empty(_tags.AllLinks);
    }

    [Fact]
    public async Task UnassignKeepsTheLinkAsRemovedSoItCanComeBack()
    {
        await OpenAsync();
        var home = await _tags.CreateAsync("Home");
        var work = await _tags.CreateAsync("Work");
        await _tags.SetTagsAsync(TagRules.Note, ["n1"], [home.Id, work.Id]);
        await _tags.SetTagsAsync(TagRules.Note, ["n1"], [work.Id]);
        Assert.Equal("Work", Assert.Single(_tags.TagsOf(TagRules.Note, "n1")).Name);
        var link = _tags.FindLink(TagRules.LinkId(home.Id, TagRules.Note, "n1"))!;
        Assert.True(link.Removed);
        await _tags.SetTagAsync(TagRules.Note, ["n1"], home.Id, on: true);
        Assert.False(_tags.FindLink(link.Id)!.Removed);
        Assert.Equal(2, _tags.TagsOf(TagRules.Note, "n1").Count);
        // Match all vs any.
        await _tags.SetTagsAsync(TagRules.Note, ["n2"], [home.Id]);
        Assert.Equal(["n1", "n2"], _tags.ItemsWith(TagRules.Note, [home.Id, work.Id]).Order());
        Assert.Equal(["n1"], _tags.ItemsWith(TagRules.Note, [home.Id, work.Id], matchAll: true));
    }

    [Fact]
    public async Task OnlyCloudKindsAreQueuedAndTagDeletionSendsOnlyTheTag()
    {
        await OpenAsync(recording: true);
        var t = await _tags.CreateAsync("Family");
        await _tags.SetTagsAsync(TagRules.Note, ["n1"], [t.Id]);
        await _tags.SetTagsAsync(TagRules.Event, ["acct|ev1"], [t.Id]);
        var outbox = await _store.OutboxAsync(CancellationToken.None);
        Assert.Contains(outbox, o => o.EntityType == CloudKinds.Tag && o.EntityId == t.Id);
        Assert.Contains(outbox, o => o.EntityType == CloudKinds.TagLink && o.EntityId == TagRules.LinkId(t.Id, TagRules.Note, "n1"));
        Assert.DoesNotContain(outbox, o => o.EntityId == TagRules.LinkId(t.Id, TagRules.Event, "acct|ev1")); // account events stay on this PC

        // Pretend the tag and its note link reached the cloud, then delete the tag.
        await _store.ApplyRemoteTagsAsync(new TagChangeSet(), [new SyncVersion(CloudKinds.Tag, t.Id, 1, null),
            new SyncVersion(CloudKinds.TagLink, TagRules.LinkId(t.Id, TagRules.Note, "n1"), 1, null)], CancellationToken.None);
        foreach (var item in outbox)
            await _store.CompleteAsync(item, null, CancellationToken.None);
        await _tags.DeleteAsync(t.Id);
        outbox = await _store.OutboxAsync(CancellationToken.None);
        var only = Assert.Single(outbox);
        Assert.Equal((CloudKinds.Tag, t.Id, true), (only.EntityType, only.EntityId, only.Deleted)); // the cloud removes the links itself
    }

    [Fact]
    public async Task AnOfflineDuplicateNameIsMergedIntoTheCloudTag()
    {
        await OpenAsync();
        var mine = await _tags.CreateAsync("Travel");
        await _tags.SetTagsAsync(TagRules.Note, ["n1"], [mine.Id]);
        // The cloud already had "travel" (another device made it first).
        var cloud = new Tag { Id = "cloud-travel", Name = "travel", CreatedUtc = "2026-11-27T10:00:00Z", ModifiedUtc = "2026-11-27T10:00:00Z" };
        var remote = new TagChangeSet();
        remote.UpsertTags.Add(cloud);
        await _tags.ApplyRemoteAsync(remote, [new SyncVersion(CloudKinds.Tag, cloud.Id, 1, null)]);
        Assert.Equal(1, await _tags.MergeUnsyncedDuplicatesAsync(new HashSet<string> { cloud.Id }));
        Assert.Equal("travel", Assert.Single(_tags.Tags).Name);
        Assert.Equal(cloud.Id, Assert.Single(_tags.TagsOf(TagRules.Note, "n1")).Id);
        // Different names are never merged.
        await _tags.CreateAsync("Travels");
        Assert.Equal(0, await _tags.MergeUnsyncedDuplicatesAsync(new HashSet<string> { cloud.Id }));
    }

    [Fact]
    public async Task ADeletedItemTakesItsTagsAlongAndLimitsHold()
    {
        await OpenAsync();
        var t = await _tags.CreateAsync("Work");
        await _tags.SetTagsAsync(TagRules.Task, ["t1"], [t.Id]);
        await _tags.ItemDeletedAsync(TagRules.Task, "t1");
        Assert.Empty(_tags.TagsOf(TagRules.Task, "t1"));
        Assert.Equal("Work", Assert.Single(_tags.Tags).Name); // the tag stays

        var many = new List<string>();
        for (var i = 0; i < TagRules.MaxTagsPerItem; i++)
            many.Add((await _tags.CreateAsync("Tag " + i)).Id);
        await _tags.SetTagsAsync(TagRules.Note, ["busy"], many);
        await Assert.ThrowsAsync<TagException>(() => _tags.SetTagAsync(TagRules.Note, ["busy"], t.Id, on: true));
        await Assert.ThrowsAsync<TagException>(() => _tags.SetTagsAsync(TagRules.Note, ["x"], ["deleted-tag"]));
    }
}
