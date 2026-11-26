namespace Alondra1.Core;

/// <summary>
/// Version, environment and diagnostics for support (Dev-only spec §1.1 item 8). Never includes email addresses,
/// tokens, ids, or task, note, mail or file contents: only what helps tell an environment or a sync problem apart.
/// </summary>
public static class Diagnostics
{
    /// <summary>Where testers report problems: the Dev feedback form; none in prod until a support channel exists.</summary>
    public static string FeedbackUrl => BuildEnvironment.IsDev ? "https://github.com/alondrauno/alondra1-www/issues/new/choose" : "";

    public static string EnvironmentLabel => BuildEnvironment.IsDev ? "Development and test version (dev)" : BuildEnvironment.Name;

    public sealed record Facts(string Version, string Environment, string Windows, bool SignedIn, bool SyncEnabled,
        string SyncState, DateTimeOffset? LastSynced, int WaitingChanges);

    public static string Text(Facts f) => string.Join("\n",
        $"Alondra1 for Windows {f.Version} ({f.Environment})",
        $"Windows: {f.Windows}",
        $"Signed in: {(f.SignedIn ? "yes" : "no")}",
        $"Cloud Sync: {(f.SyncEnabled ? "on" : "off")}, state {f.SyncState}" + (f.LastSynced is { } t ? $", last synced {t:yyyy-MM-ddTHH:mm:ssZ}" : ""),
        $"Changes waiting to sync: {f.WaitingChanges}");
}
