using Alondra1.Core.Mail;
using Alondra1.Persistence;
using Microsoft.Data.Sqlite;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §5.2 / T08 and §3.2 / T05: conversations from thread headers only; Cc and Reply-To stored and used.</summary>
public class MailThreadsTests
{
    private static StoredMessage M(string account, long uid, string? id, int minute, string inReplyTo = "", string refs = "", string subject = "s") => new()
    {
        AccountId = account, FolderId = "INBOX", Uid = uid, MessageId = id, InReplyTo = inReplyTo, References = refs, Subject = subject,
        ReceivedUtc = new DateTimeOffset(2026, 11, 20, 9, minute, 0, TimeSpan.Zero),
    };

    private static List<StoredMessage> NewestFirst(params StoredMessage[] messages) =>
        MailListView.Apply(messages, MailListFilter.None, MailSortField.Date, true);

    [Fact]
    public void RepliesJoinTheirConversationByHeaders()
    {
        var root = M("w", 1, "root@x", 0, subject: "Budget");
        var reply = M("w", 2, "r1@x", 5, inReplyTo: "<root@x>", subject: "Re: Budget");
        var second = M("w", 3, "r2@x", 9, refs: "<root@x> <r1@x>", subject: "Re: Re: Budget");
        var other = M("w", 4, "other@x", 7, subject: "Lunch");
        var rows = MailThreads.Arrange(NewestFirst(root, reply, second, other));
        Assert.Equal([3L, 2L, 1L, 4L], rows.Select(m => m.Uid));
        Assert.Equal(3, rows[0].ThreadCount);
        Assert.True(rows[0].ShowThreadCount);
        Assert.True(rows[1].IsThreadReply && rows[2].IsThreadReply);
        Assert.False(rows[3].IsThreadReply);
        Assert.Equal(1, rows[3].ThreadCount);
    }

    [Fact]
    public void SameSubjectWithoutHeadersIsNeverGrouped()
    {
        var a = M("w", 1, "a@x", 0, subject: "Weekly update");
        var b = M("w", 2, "b@x", 1, subject: "Weekly update");
        var c = M("w", 3, null, 2, subject: "Re: Weekly update");
        var rows = MailThreads.Arrange(NewestFirst(a, b, c));
        Assert.All(rows, m => Assert.Equal(1, m.ThreadCount));
    }

    [Fact]
    public void TheSameIdsInTwoAccountsStayTwoConversations()
    {
        var places = MailThreads.Build([M("w", 1, "root@x", 0), M("w", 2, "r@x", 1, "<root@x>"), M("h", 1, "root@x", 0), M("h", 2, "r@x", 1, "<root@x>")]);
        Assert.Equal(2, places.Values.Select(p => p.Key).Distinct().Count());
        Assert.All(places.Values, p => Assert.Equal(2, p.Count));
    }

    [Fact]
    public void SiblingsLinkThroughAParentThatIsNotInTheList()
    {
        var rows = MailThreads.Arrange(NewestFirst(M("w", 1, "a@x", 0, "<gone@x>"), M("w", 2, "b@x", 3, refs: "<gone@x>")));
        Assert.Equal(2, rows[0].ThreadCount);
    }

    [Fact]
    public void ReferencesForAReplyAppendTheParentAndStayBounded()
    {
        var parent = M("w", 1, "<p@x>", 0, refs: string.Join(' ', Enumerable.Range(1, 25).Select(i => $"<id{i}@x>")));
        var refs = MailThreads.Ids(MailThreads.ReferencesFor(parent));
        Assert.Equal(20, refs.Count);
        Assert.Equal("p@x", refs[^1]);
        Assert.Equal("<a@x> <p@x>", MailThreads.ReferencesFor(M("w", 1, "p@x", 0, inReplyTo: "<a@x>")));
    }

    [Fact]
    public void ReplyGoesToReplyToAndReplyAllKeepsCcInCc()
    {
        var m = new StoredMessage
        {
            FromAddress = "news@example.com", ReplyTo = "support@example.com",
            Recipients = "me@work.example, liam@example.com", Cc = "ava@example.com, ME@work.example, liam@example.com",
        };
        Assert.Equal(["support@example.com"], ComposeIdentity.ReplyTargets(m));
        var to = ComposeIdentity.ReplyAllRecipients(m, "me@work.example");
        Assert.Equal(["support@example.com", "liam@example.com"], to);
        Assert.Equal(["ava@example.com"], ComposeIdentity.ReplyAllCc(m, "me@work.example", to));
        Assert.Equal(["news@example.com"], ComposeIdentity.ReplyTargets(new StoredMessage { FromAddress = "news@example.com" }));
    }

    [Fact]
    public async Task AnOlderDatabaseGainsTheColumnsAndKeepsItsMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-threads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "personal.db");
        try
        {
            // A schema-1 database: the messages table without the new columns, one message in it.
            await using (var store = new SqlitePersonalStore(path))
            {
                await store.InitializeAsync(default);
                var batch = new PersonalBatch();
                batch.Upserts.Add(M("w", 1, "old@x", 0, subject: "Kept"));
                await store.CommitAsync(batch, default);
            }

            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                legacy.Open();
                using var command = legacy.CreateCommand();
                command.CommandText = """
                    ALTER TABLE messages DROP COLUMN cc; ALTER TABLE messages DROP COLUMN reply_to;
                    ALTER TABLE messages DROP COLUMN in_reply_to; ALTER TABLE messages DROP COLUMN refs;
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            await using var reopened = new SqlitePersonalStore(path);
            await reopened.InitializeAsync(default);
            var kept = Assert.Single(await reopened.ListAsync("w", "INBOX", default));
            Assert.Equal("Kept", kept.Subject);
            Assert.Equal("", kept.Cc);

            var update = new PersonalBatch();
            var full = M("w", 2, "new@x", 1, "<old@x>", "<old@x>");
            full.Cc = "ava@example.com";
            full.ReplyTo = "desk@example.com";
            update.Upserts.Add(full);
            await reopened.CommitAsync(update, default);
            var read = (await reopened.ListAsync("w", "INBOX", default)).Single(m => m.Uid == 2);
            Assert.Equal(("ava@example.com", "desk@example.com", "<old@x>", "<old@x>"), (read.Cc, read.ReplyTo, read.InReplyTo, read.References));

            // A later refresh without the headers (flags only) keeps them.
            var refresh = new PersonalBatch();
            refresh.Upserts.Add(M("w", 2, "new@x", 1));
            await reopened.CommitAsync(refresh, default);
            Assert.Equal("ava@example.com", (await reopened.ListAsync("w", "INBOX", default)).Single(m => m.Uid == 2).Cc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
