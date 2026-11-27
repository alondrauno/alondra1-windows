using System.Text.RegularExpressions;
using Alondra1.Core.Layout;
using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §5.1 / §9 (T07, T21): date headings, snippets, and high-contrast coverage of every theme brush.</summary>
public class MailListPresentationTests
{
    private static DateTimeOffset Local(int y, int m, int d, int h = 10) => new(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Local));

    [Fact]
    public void DateHeadingsFollowLocalDays()
    {
        var now = Local(2026, 11, 27, 15); // a Friday
        Assert.Equal("Today", MailListView.DateGroup(Local(2026, 11, 27, 1), now));
        Assert.Equal("Yesterday", MailListView.DateGroup(Local(2026, 11, 26), now));
        Assert.Equal("Last month", MailListView.DateGroup(Local(2026, 10, 3), now));
        Assert.Equal(new DateTime(2026, 9, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture), MailListView.DateGroup(Local(2026, 9, 14), now));
        var earlier = MailListView.DateGroup(Local(2026, 11, 2), now);
        Assert.Equal("Earlier this month", earlier);
        // A sorted list gives each heading once, in order (no repeated or interleaved headings).
        var days = Enumerable.Range(0, 120).Select(i => now.AddDays(-i)).ToList();
        var headings = days.Select(d => MailListView.DateGroup(d, now)).ToList();
        var runs = headings.Where((h, i) => i == 0 || headings[i - 1] != h).ToList();
        Assert.Equal(runs.Count, runs.Distinct().Count());
    }

    [Fact]
    public void SnippetsAreOneTrimmedLineOrNothing()
    {
        Assert.Equal("", MailListView.Snippet("  \n "));
        Assert.Equal(" — Hello there team", MailListView.Snippet("Hello\r\n  there\tteam"));
        Assert.True(MailListView.Snippet(new string('x', 500)).Length <= 163);
    }

    [Theory]
    [InlineData("Dark.xaml")]
    [InlineData("Light.xaml")]
    public void HighContrastMapsEveryThemeBrush(string theme)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Alondra1.App", "Themes")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!, "src", "Alondra1.App", "Themes", theme));
        var keys = Regex.Matches(xaml, "x:Key=\"([A-Za-z]+Brush)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.True(HighContrast.Roles.ContainsKey(key), $"{key} has no high-contrast color"));
        // Text never uses a surface color, and surfaces never use the text color.
        Assert.Equal(ContrastRole.WindowText, HighContrast.Roles["MutedBrush"]);
        Assert.Equal(ContrastRole.Window, HighContrast.Roles["SelectedBrush"]);
    }
}
