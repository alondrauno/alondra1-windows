using Alondra1.Core;

namespace Alondra1.Tests;

// Dev-only spec §1.1 item 8: version, environment and diagnostics without personal data; feedback on dev builds only.
public class DiagnosticsTests
{
    [Fact]
    public void Diagnostics_name_version_environment_and_sync_facts_and_nothing_personal()
    {
        var text = Diagnostics.Text(new("1.3.0", "dev", "Microsoft Windows NT 10.0.26100.0", true, true, "Synced",
            new DateTimeOffset(2026, 11, 26, 18, 0, 0, TimeSpan.Zero), 2));
        Assert.Contains("Alondra1 for Windows 1.3.0 (dev)", text);
        Assert.Contains("Cloud Sync: on, state Synced, last synced 2026-11-26T18:00:00Z", text);
        Assert.Contains("Changes waiting to sync: 2", text);
        Assert.DoesNotContain("@", text);
    }

    [Fact]
    public void Feedback_link_exists_only_in_dev_builds()
    {
        Assert.Equal(BuildEnvironment.IsDev, Diagnostics.FeedbackUrl.Length > 0);
        if (BuildEnvironment.IsDev)
            Assert.StartsWith("https://github.com/alondrauno/alondra1-www/issues/", Diagnostics.FeedbackUrl);
    }
}
