using System.Collections.Concurrent;
using System.Diagnostics;
using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Core.Search;
using Alondra1.Core.Sync;
using Alondra1.Persistence;
using Xunit.Abstractions;

namespace Alondra1.Tests;

/// <summary>
/// Mail UI spec §7.2 and §10 (PF-01): many accounts sync independently; 10 and 15 account fixtures with synthetic
/// content stay correct; the opt-in benchmark measures the §10 budgets (set ALONDRA1_MAIL_UI_BENCH=1).
/// </summary>
public sealed class MailUiScaleTests(ITestOutputHelper output)
{
    // ---------- Independent sync (§7.2) ----------

    [Fact]
    public async Task AFailingAndASlowAccountDoNotHoldUpTheOthers()
    {
        var accounts = Enumerable.Range(1, 15).Select(i => $"a{i}").ToList();
        var running = 0;
        var peak = 0;
        var order = new ConcurrentQueue<string>();
        var reports = new ConcurrentDictionary<string, SyncReport>();
        var slowGate = new TaskCompletionSource();
        var run = AccountSyncScheduler.RunAsync(accounts, 3, TimeSpan.FromSeconds(30), async (account, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try
            {
                if (account == "a1")
                {
                    await slowGate.Task.WaitAsync(ct);
                    return SyncReport.Done("slow");
                }

                await Task.Delay(5, ct);
                if (account == "a2")
                    throw new InvalidOperationException("server said: password=hunter2");
                return SyncReport.Done("ok");
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }, (account, report) =>
        {
            reports[account] = report;
            order.Enqueue(account);
            return Task.CompletedTask;
        });

        // Every other account finishes while the slow one is still running.
        var deadline = Stopwatch.StartNew();
        while (reports.Count < 14 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert.Equal(14, reports.Count);
        Assert.False(reports.ContainsKey("a1"));
        slowGate.SetResult();
        await run;

        Assert.Equal("a1", order.Last());
        Assert.True(peak <= 3, $"peak concurrency {peak}");
        var failed = reports["a2"];
        Assert.False(failed.Ok);
        Assert.DoesNotContain("hunter2", failed.Detail);
        Assert.Equal(14, reports.Values.Count(r => r.Ok));
    }

    [Fact]
    public async Task AHungProviderIsReportedAsNotAnsweringAndFreesItsSlot()
    {
        var reports = new ConcurrentDictionary<string, SyncReport>();
        await AccountSyncScheduler.RunAsync(["hung", "b", "c"], 1, TimeSpan.FromMilliseconds(200), async (account, ct) =>
        {
            if (account == "hung")
                await Task.Delay(Timeout.Infinite, CancellationToken.None); // ignores cancellation, like a stuck socket
            return SyncReport.Done("ok");
        }, (account, report) =>
        {
            reports[account] = report;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reports["hung"].Ok);
        Assert.Contains("did not answer", reports["hung"].Detail);
        Assert.True(reports["b"].Ok && reports["c"].Ok);
    }

    [Fact]
    public void TheSummaryCountsEveryAccountSeparately()
    {
        var board = new AccountSyncBoard();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 15; i++)
            board.Begin($"a{i}", now);
        for (var i = 0; i < 13; i++)
            board.Finish($"a{i}", SyncReport.Done(""), now);
        board.Finish("a13", SyncReport.Failed("x", authRequired: true), now);
        var states = Enumerable.Range(0, 15).Select(i => board.Get($"a{i}")).ToList();
        Assert.Equal("13 up to date · 1 syncing · 1 needs attention", AccountSyncBoard.Summary(states));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    // ---------- Fixtures (§10): synthetic content only ----------

    /// <summary>
    /// Accounts with duplicate display names across providers, nested folders, unread mail, attachments on every 25th
    /// message, a few large bodies, and <paramref name="perAccount"/> headers in each Inbox.
    /// </summary>
    internal static async Task<List<AccountConfig>> SeedAsync(SqlitePersonalStore store, int accountCount, Func<int, int> perAccount, int bodyEvery = 10)
    {
        var accounts = new List<AccountConfig>();
        var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var senders = new[] { "Maya Chen", "Liam Weber", "Ава Петрова", "李伟", "Owen Clark", "Lea Hoffmann", "سارة" };
        for (var a = 0; a < accountCount; a++)
        {
            var account = new AccountConfig
            {
                Id = $"acct{a:00}",
                // Every third account shares the name "Work": they must stay apart by address.
                DisplayName = a % 3 == 0 ? "Work" : $"Account {a}",
                Address = $"user{a}@{(a % 2 == 0 ? "imap" : "graph")}.example",
            };
            accounts.Add(account);
            var count = perAccount(a);
            var folders = new[] { "INBOX", "INBOX/Projects", "INBOX/Projects/2026", "Sent", "Archive" };
            for (var offset = 0; offset < count; offset += 500)
            {
                var batch = new PersonalBatch();
                for (var i = offset; i < Math.Min(count, offset + 500); i++)
                {
                    var folder = i % 10 < 7 ? "INBOX" : folders[1 + i % 4];
                    var uid = i + 1L;
                    var key = new MessageKey(account.Id, folder, uid);
                    batch.Upserts.Add(new StoredMessage
                    {
                        AccountId = account.Id, FolderId = folder, Uid = uid,
                        Subject = i % 50 == 0 ? $"Quarterly budget review {i}" : $"Status update {i} for project {i % 37}",
                        FromName = senders[i % senders.Length], FromAddress = $"sender{i % 40}@example.test",
                        Recipients = account.Address + (i % 4 == 0 ? ", team@example.test" : ""),
                        ReceivedUtc = start.AddMinutes(i * 7 + a),
                        IsUnread = i % 9 == 0, IsFlagged = i % 31 == 0, HasAttachments = i % 25 == 0,
                        BodyDownloaded = i % bodyEvery == 0, Preview = "Synthetic preview text for fixture",
                    });
                    if (i % bodyEvery == 0)
                    {
                        var large = i % 990 == 0;
                        batch.Bodies.Add((key, large ? string.Concat(Enumerable.Repeat("Large synthetic body paragraph. ", 8000)) : $"Body {i} mentions lighthouse {i % 13}", null));
                    }

                    if (i % 25 == 0)
                        batch.Attachments.Add(new StoredAttachment { Id = $"{account.Id}-{uid}", AccountId = account.Id, FolderId = folder, Uid = uid, FileName = $"invoice-{i}.pdf", Size = 40_000 });
                }

                await store.CommitAsync(batch, default);
            }
        }

        return accounts;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-mailui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task FifteenAccountsStayApartInListsCountsAndSearch()
    {
        var root = NewRoot();
        try
        {
            await using var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
            await store.InitializeAsync(default);
            var accounts = await SeedAsync(store, 15, a => a == 14 ? 0 : 60);

            // Counts: one row per account and folder; the empty account is still an account (no rows, not an error).
            var counts = await store.CountByFolderAsync(default);
            Assert.Equal(14, counts.Select(c => c.AccountId).Distinct().Count());
            Assert.All(counts, c => Assert.True(c.Unread <= c.Total));

            // Unified Inbox: every account's own rows, no merging of identical uids across accounts, stable order.
            var inbox = new List<StoredMessage>();
            foreach (var account in accounts)
                inbox.AddRange(await store.ListAsync(account.Id, "INBOX", default));
            var first = MailListView.Apply(inbox, MailListFilter.None, MailSortField.Date, true);
            var second = MailListView.Apply(inbox.AsEnumerable().Reverse(), MailListFilter.None, MailSortField.Date, true);
            Assert.Equal(first.Select(m => (m.AccountId, m.FolderId, m.Uid)), second.Select(m => (m.AccountId, m.FolderId, m.Uid)));
            Assert.Equal(first.Count, first.Select(m => (m.AccountId, m.FolderId, m.Uid)).Distinct().Count());
            Assert.Equal(14, first.Select(m => m.AccountId).Distinct().Count());

            // Duplicate names are told apart by address.
            var names = MailSearchSource.AccountNames(accounts);
            Assert.Equal(accounts.Count, names.Values.Distinct().Count());
            Assert.Contains("Work (user0@imap.example)", names.Values);

            // Search keeps account identity and finds the same subject in several accounts as separate results.
            var group = await new MailSearchSource((q, ct) => store.SearchAsync(null, null, q, ct), accounts).SearchAsync("quarterly budget", 50, default);
            Assert.True(group.Total >= 14);
            Assert.Equal(group.Hits.Count, group.Hits.Select(h => h.ObjectId).Distinct().Count());

            // Nested folders are separate scopes.
            Assert.All(await store.ListAsync("acct01", "INBOX/Projects/2026", default), m => Assert.Equal("INBOX/Projects/2026", m.FolderId));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------- Benchmark (§10), opt-in ----------

    [MailUiBenchFact]
    public async Task Budgets_with_ten_and_fifteen_accounts_and_100k_headers()
    {
        var root = NewRoot();
        try
        {
            output.WriteLine($"Machine: {Environment.ProcessorCount} logical cores, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB available to .NET, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}.");
            await using var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
            await store.InitializeAsync(default);
            var seed = Stopwatch.StartNew();
            // 15 accounts: 10 with 1,000 headers each, 5 with 18,000 each: 100,000 headers in total.
            var accounts = await SeedAsync(store, 15, a => a < 10 ? 1_000 : 18_000);
            var total = await store.CountMessagesAsync(null, default);
            output.WriteLine($"Seed: {total:N0} headers in {accounts.Count} accounts, {seed.Elapsed.TotalSeconds:F1} s (one-time; not a budget).");
            Assert.Equal(100_000, total);
            var ten = accounts.Take(10).ToList();

            var results = new List<(string Name, double P95, double Budget)>();
            // budget 0: recorded for the acceptance record, not a §10 target (for example a full load behind a first page).
            async Task Measure(string name, double budget, Func<int, Task> run, int runs = 40)
            {
                await run(0); // warm
                var times = new List<double>();
                for (var i = 1; i <= runs; i++)
                {
                    var t = Stopwatch.StartNew();
                    await run(i);
                    times.Add(t.Elapsed.TotalMilliseconds);
                }

                times.Sort();
                var p95 = times[(int)Math.Ceiling(runs * 0.95) - 1];
                if (budget > 0)
                    results.Add((name, p95, budget));
                output.WriteLine($"{name}: p50 {times[runs / 2]:F1} ms, p95 {p95:F1} ms, max {times[^1]:F1} ms ({(budget > 0 ? $"target {budget} ms" : "informational")}).");
            }

            // Switch account / folder with cached headers: list + filter/sort, first usable rows (§10: 300 ms p95).
            await Measure("Switch to one account's Inbox (1,000 headers)", 300, async i =>
            {
                var account = ten[i % ten.Count];
                var rows = MailListView.Apply(await store.ListAsync(account.Id, "INBOX", default), MailListFilter.None, MailSortField.Date, true);
                Assert.NotEmpty(rows);
            });
            await Measure("Switch to a large account's Inbox (18,000 headers)", 300, async i =>
            {
                var account = accounts[10 + i % 5];
                Assert.NotEmpty(MailListView.Apply(await store.ListAsync(account.Id, "INBOX", default), MailListFilter.None, MailSortField.Date, true));
            }, 20);
            await Measure("Unified Inbox, 10 accounts", 300, async _ =>
            {
                var all = new List<StoredMessage>();
                foreach (var account in ten)
                    all.AddRange(await store.ListAsync(account.Id, "INBOX", default));
                Assert.Equal(7_000, MailListView.Apply(all, MailListFilter.None, MailSortField.Date, true).Count);
            });
            var allInboxes = accounts.Select(a => (a.Id, "INBOX")).ToList();
            await Measure("Unified Inbox, 15 accounts: first 300 rows (shown first)", 300, async _ =>
                Assert.Equal(300, MailListView.Apply(await store.ListPageAsync(allInboxes, 300, default), MailListFilter.None, MailSortField.Date, true).Count));
            await Measure("Unified Inbox, 15 accounts: all 70,000 rows (loads behind the first page)", 0, async _ =>
            {
                var all = new List<StoredMessage>();
                foreach (var account in accounts)
                    all.AddRange(await store.ListAsync(account.Id, "INBOX", default));
                Assert.Equal(70_000, MailListView.Apply(all, new MailListFilter(Unread: true), MailSortField.Date, true).Count + all.Count(m => !m.IsUnread));
            }, 10);
            await Measure("Sidebar counts, 15 accounts", 300, async _ => Assert.NotEmpty(await store.CountByFolderAsync(default)));

            // Open a cached message (§10: 500 ms p95), including large bodies.
            await Measure("Open cached message body", 500, async i =>
            {
                var account = accounts[i % accounts.Count];
                var uid = i % 2 == 0 ? 1L : 991L; // uid 991 is index 990: a large body
                Assert.NotNull(await store.GetBodyAsync(new MessageKey(account.Id, "INBOX", uid), default));
            });

            // Local Mail search over 100k indexed headers (§10: first page 500 ms p95).
            await Measure("Search all mail, rare term", 500, async _ => Assert.NotEmpty(await store.SearchAsync(null, null, "quarterly budget", default)));
            await Measure("Search all mail, frequent term: first page of 200 and the total", 500, async _ =>
            {
                var (page, count) = await store.SearchPageAsync(null, null, "status", 200, default);
                Assert.Equal(200, page.Count);
                Assert.True(count > 90_000);
            });
            await Measure("Search all mail, frequent term: every match (list search, informational)", 0, async _ => Assert.NotEmpty(await store.SearchAsync(null, null, "status", default)), 10);
            await Measure("Search all mail, no match", 500, async _ => Assert.Empty(await store.SearchAsync(null, null, "absenttoken", default)));
            await Measure("Attachment name lookup", 500, async _ => Assert.NotEmpty(await store.FindByAttachmentNameAsync(null, "invoice-1", default)));

            // All-application search: first useful group (§10: 700 ms p95); the mail adapter is the heavy one.
            await Measure("All search, first group", 700, async _ =>
            {
                var coordinator = new SearchCoordinator();
                var first = new TaskCompletionSource<double>();
                var t = Stopwatch.StartNew();
                ISearchSource[] sources =
                [
                    new MailSearchSource(async (q, ct) =>
                    {
                        var (page, count) = await store.SearchPageAsync(null, null, q, 200, ct);
                        return new MailSearchPage(page, count);
                    }, accounts),
                    new CalendarSearchSource(() => [], () => ""),
                    new ContactSearchSource(() => [], () => ""),
                ];
                await coordinator.RunAsync("quarterly budget", sources, 8, _ => first.TrySetResult(t.Elapsed.TotalMilliseconds));
                Assert.True(first.Task.IsCompleted);
            });

            // Repeated switches: no unbounded growth (§10).
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalMemory(true);
            for (var i = 0; i < 300; i++)
                _ = MailListView.Apply(await store.ListAsync(ten[i % 10].Id, "INBOX", default), MailListFilter.None, MailSortField.Date, true);
            var after = GC.GetTotalMemory(true);
            output.WriteLine($"Managed memory after 300 account switches: {(after - before) / 1024.0 / 1024.0:+0.0;-0.0} MB.");
            Assert.True(after - before < 50L * 1024 * 1024, "memory grew by more than 50 MB over 300 switches");

            var over = results.Where(r => r.P95 > r.Budget).ToList();
            foreach (var r in over)
                output.WriteLine($"OVER BUDGET: {r.Name} p95 {r.P95:F1} ms > {r.Budget} ms");
            Assert.Empty(over);
        }
        finally
        {
            TryDelete(root);
        }
    }
}

public sealed class MailUiBenchFactAttribute : FactAttribute
{
    public MailUiBenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ALONDRA1_MAIL_UI_BENCH") != "1")
            Skip = "Set ALONDRA1_MAIL_UI_BENCH=1 to run the Mail UI §10 benchmark (100,000 synthetic headers, about a minute).";
    }
}
