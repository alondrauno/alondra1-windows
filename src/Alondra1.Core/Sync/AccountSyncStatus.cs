using Alondra1.Core.Ports;

namespace Alondra1.Core.Sync;

/// <summary>Where one mail account's sync with its provider stands (Mail UI spec §7.1). Separate from Alondra1 Cloud sync.</summary>
public enum AccountSyncState { NotSynced, Syncing, UpToDate, Offline, NeedsSignIn, Blocked, Error }

/// <summary>One account's provider sync: its state, the last success and attempt, and a short reason when it failed.</summary>
public sealed record AccountSyncStatus(string AccountId, AccountSyncState State, DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastAttemptUtc, string Detail = "")
{
    public bool NeedsAttention => State is AccountSyncState.NeedsSignIn or AccountSyncState.Blocked or AccountSyncState.Error;

    /// <summary>Short label for the sidebar; empty when there is nothing to say (up to date).</summary>
    public string Badge => State switch
    {
        AccountSyncState.Syncing => "Syncing…",
        AccountSyncState.Offline => "Offline",
        AccountSyncState.NeedsSignIn => "Sign in needed",
        AccountSyncState.Blocked => "Needs attention",
        AccountSyncState.Error => "Sync failed",
        AccountSyncState.NotSynced => "Not synced yet",
        _ => "",
    };

    /// <summary>A full sentence: the state, the last successful sync (never claimed when it failed), and the reason.</summary>
    public string Describe(DateTimeOffset now)
    {
        var last = LastSuccessUtc is { } at ? $"Last synced {Ago(at, now)}." : "Not synced yet on this PC.";
        return State switch
        {
            AccountSyncState.UpToDate => $"Up to date. {last}",
            AccountSyncState.Syncing => $"Syncing now. {last}",
            AccountSyncState.Offline => $"Offline: showing mail kept on this PC. {last}",
            AccountSyncState.NeedsSignIn => $"Sign in again to keep syncing. {last} {Detail}".TrimEnd(),
            AccountSyncState.Blocked => $"Sync is stopped: {Detail} {last}".TrimEnd(),
            AccountSyncState.Error => $"The last sync failed: {Detail} {last}".TrimEnd(),
            _ => last,
        };
    }

    internal static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1))
            return "just now";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes} min ago";
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? $"today at {local:HH:mm}" : $"on {local:d MMM yyyy, HH:mm}";
    }
}

/// <summary>The overall state the status area shows for all included accounts.</summary>
public enum SyncOverall { Ready, Syncing, Offline, Attention }

/// <summary>
/// Per-account sync states for the session. A failed attempt never erases the last success, and one account's state
/// never stands in for another's: the summary counts them ("8 up to date · 1 syncing · 1 needs attention").
/// </summary>
public sealed class AccountSyncBoard
{
    private readonly Dictionary<string, AccountSyncStatus> _states = new(StringComparer.Ordinal);

    public AccountSyncStatus Get(string accountId, DateTimeOffset? lastSuccessUtc = null) =>
        _states.TryGetValue(accountId, out var state) ? state
        : new AccountSyncStatus(accountId, lastSuccessUtc is null ? AccountSyncState.NotSynced : AccountSyncState.UpToDate, lastSuccessUtc, null);

    public AccountSyncStatus Begin(string accountId, DateTimeOffset now, DateTimeOffset? lastSuccessUtc = null) =>
        _states[accountId] = Get(accountId, lastSuccessUtc) with { State = AccountSyncState.Syncing, LastAttemptUtc = now, Detail = "" };

    public AccountSyncStatus Finish(string accountId, SyncReport report, DateTimeOffset now)
    {
        var previous = Get(accountId);
        var state = report.Ok ? AccountSyncState.UpToDate
            : report.Blocked ? AccountSyncState.Blocked
            : report.AuthRequired ? AccountSyncState.NeedsSignIn
            : report.Offline ? AccountSyncState.Offline
            : AccountSyncState.Error;
        return _states[accountId] = previous with
        {
            State = state,
            LastAttemptUtc = now,
            LastSuccessUtc = report.Ok ? now : previous.LastSuccessUtc,
            Detail = report.Ok ? "" : report.Detail,
        };
    }

    public void Remove(string accountId) => _states.Remove(accountId);

    public static SyncOverall Overall(IReadOnlyCollection<AccountSyncStatus> states) =>
        states.Any(s => s.State == AccountSyncState.Syncing) ? SyncOverall.Syncing
        : states.Count > 0 && states.All(s => s.State == AccountSyncState.Offline) ? SyncOverall.Offline
        : states.Any(s => s.NeedsAttention || s.State == AccountSyncState.Offline) ? SyncOverall.Attention
        : SyncOverall.Ready;

    /// <summary>"3 accounts up to date", "8 up to date · 1 syncing · 1 needs attention", "Offline · mail on this PC".</summary>
    public static string Summary(IReadOnlyCollection<AccountSyncStatus> states)
    {
        if (states.Count == 0)
            return "No accounts";
        if (states.All(s => s.State == AccountSyncState.Offline))
            return "Offline · showing mail kept on this PC";
        var upToDate = states.Count(s => s.State == AccountSyncState.UpToDate);
        if (upToDate == states.Count)
            return upToDate == 1 ? "1 account up to date" : $"{upToDate} accounts up to date";
        var parts = new List<string>();
        if (upToDate > 0)
            parts.Add($"{upToDate} up to date");
        void Add(int n, string text)
        {
            if (n > 0)
                parts.Add($"{n} {text}");
        }

        Add(states.Count(s => s.State == AccountSyncState.Syncing), "syncing");
        Add(states.Count(s => s.State == AccountSyncState.Offline), "offline");
        Add(states.Count(s => s.NeedsAttention), states.Count(s => s.NeedsAttention) == 1 ? "needs attention" : "need attention");
        Add(states.Count(s => s.State == AccountSyncState.NotSynced), "not synced yet");
        return string.Join(" · ", parts);
    }
}
