using Alondra1.Core.Ports;
using Alondra1.Core.Sync;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §7 (SY-01): per-account sync states, honest last-success, and a summary that counts accounts.</summary>
public class AccountSyncStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFailedAttemptKeepsTheLastSuccess()
    {
        var board = new AccountSyncBoard();
        board.Begin("w", T0);
        board.Finish("w", SyncReport.Done("ok"), T0);
        board.Begin("w", T0.AddMinutes(5));
        var after = board.Finish("w", SyncReport.Failed("Server said no."), T0.AddMinutes(5));
        Assert.Equal(AccountSyncState.Error, after.State);
        Assert.Equal(T0, after.LastSuccessUtc);
        Assert.Contains("failed", after.Describe(T0.AddMinutes(6)));
        Assert.DoesNotContain("Up to date", after.Describe(T0.AddMinutes(6)));
    }

    [Theory]
    [InlineData(false, false, false, AccountSyncState.Error)]
    [InlineData(true, false, false, AccountSyncState.Offline)]
    [InlineData(false, true, false, AccountSyncState.NeedsSignIn)]
    [InlineData(false, false, true, AccountSyncState.Blocked)]
    public void FailuresAreCategorized(bool offline, bool auth, bool blocked, AccountSyncState expected)
    {
        var report = blocked ? SyncReport.Stopped("Unsafe connection") : SyncReport.Failed("x", offline, auth);
        Assert.Equal(expected, new AccountSyncBoard().Finish("a", report, T0).State);
    }

    [Fact]
    public void TheSummaryCountsEachState()
    {
        var board = new AccountSyncBoard();
        var states = new List<AccountSyncStatus>();
        for (var i = 0; i < 8; i++)
            states.Add(board.Finish($"ok{i}", SyncReport.Done(""), T0));
        states.Add(board.Begin("slow", T0));
        states.Add(board.Finish("bad", SyncReport.Failed("auth", authRequired: true), T0));
        Assert.Equal("8 up to date · 1 syncing · 1 needs attention", AccountSyncBoard.Summary(states));
        Assert.Equal(SyncOverall.Syncing, AccountSyncBoard.Overall(states));
    }

    [Fact]
    public void AllOfflineIsTheAppBeingOfflineNotAProviderFailure()
    {
        var board = new AccountSyncBoard();
        var states = new[] { board.Finish("a", SyncReport.Failed("net", offline: true), T0), board.Finish("b", SyncReport.Failed("net", offline: true), T0) };
        Assert.Equal(SyncOverall.Offline, AccountSyncBoard.Overall(states));
        Assert.StartsWith("Offline", AccountSyncBoard.Summary(states));
        var mixed = new[] { states[0], board.Finish("c", SyncReport.Done(""), T0) };
        Assert.Equal(SyncOverall.Attention, AccountSyncBoard.Overall(mixed));
        Assert.Equal("1 up to date · 1 offline", AccountSyncBoard.Summary(mixed));
    }

    [Fact]
    public void ARememberedSuccessShowsAsUpToDateUntilTheNextAttempt()
    {
        var board = new AccountSyncBoard();
        Assert.Equal(AccountSyncState.NotSynced, board.Get("new").State);
        var known = board.Get("old", T0);
        Assert.Equal(AccountSyncState.UpToDate, known.State);
        Assert.Equal("", known.Badge);
        Assert.Equal("Syncing…", board.Begin("old", T0.AddHours(1), T0).Badge);
        Assert.Equal(T0, board.Get("old").LastSuccessUtc);
    }
}
