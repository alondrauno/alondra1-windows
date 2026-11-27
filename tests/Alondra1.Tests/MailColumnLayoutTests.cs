using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §5.1 / T07: table columns resize within limits, persist, adapt to width, and never overlap.</summary>
public class MailColumnLayoutTests
{
    private static double Sum(MailColumnWidths w) => w.Indicator + w.From + w.Account + w.Subject + w.Attachment + w.Received;

    [Fact]
    public void DefaultsKeepTheSenderToSubjectProportionAndShowTheAccountOnlyInUnifiedViews()
    {
        var single = MailColumnLayout.Resolve(700, MailColumnPreferences.Default, unified: false);
        Assert.False(single.ShowAccount);
        Assert.InRange(single.From / (single.From + single.Subject), 0.27, 0.29);

        var unified = MailColumnLayout.Resolve(700, MailColumnPreferences.Default, unified: true);
        Assert.True(unified.ShowAccount);
        Assert.Equal(MailColumnLayout.DefaultAccount, unified.Account);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(420)]
    [InlineData(600)]
    [InlineData(1400)]
    public void ColumnsNeverExceedTheListWidth(double width)
    {
        var prefs = new MailColumnPreferences(From: 900, Account: 250, Received: 170);
        var w = MailColumnLayout.Resolve(width, prefs, unified: true);
        Assert.True(Sum(w) <= width + 1, $"{Sum(w)} > {width}");
        Assert.True(w.From >= MailColumnLayout.MinFrom || w.From == width - w.Indicator - w.Attachment - w.Received);
    }

    [Fact]
    public void NarrowListsDropTheAccountThenTheAttachmentThenTheDate()
    {
        Assert.False(MailColumnLayout.Resolve(500, MailColumnPreferences.Default, true).ShowAccount);
        var mid = MailColumnLayout.Resolve(340, MailColumnPreferences.Default, true);
        Assert.False(mid.ShowAttachment);
        Assert.True(mid.ShowReceived);
        Assert.False(MailColumnLayout.Resolve(260, MailColumnPreferences.Default, true).ShowReceived);
    }

    [Fact]
    public void AWideSenderGivesWayBeforeTheSubjectDropsBelowItsMinimum()
    {
        var w = MailColumnLayout.Resolve(560, new MailColumnPreferences(From: 400), unified: true);
        Assert.True(w.Subject >= MailColumnLayout.MinSubject - 1);
        Assert.Equal(MailColumnLayout.MinAccount, w.Account);
    }

    [Fact]
    public void ResizingIsClampedToTheMinimumsAndTheSpareSubjectWidth()
    {
        var prefs = MailColumnLayout.Resize(MailColumnPreferences.Default, MailColumn.From, -1000, 800, unified: false);
        Assert.Equal(MailColumnLayout.MinFrom, prefs.From);

        prefs = MailColumnLayout.Resize(MailColumnPreferences.Default, MailColumn.From, 5000, 800, unified: false);
        var w = MailColumnLayout.Resolve(800, prefs, unified: false);
        Assert.True(w.Subject >= MailColumnLayout.MinSubject - 1);

        // Dragging the date column's left edge to the left widens it.
        prefs = MailColumnLayout.Resize(MailColumnPreferences.Default, MailColumn.Received, -20, 800, unified: false);
        Assert.Equal(MailColumnLayout.DefaultReceived + 20, prefs.Received);
    }

    [Fact]
    public void TheAccountColumnCannotBeResizedWhereItIsHidden()
    {
        var prefs = MailColumnLayout.Resize(MailColumnPreferences.Default, MailColumn.Account, 40, 800, unified: false);
        Assert.Null(prefs.Account);
    }

    [Fact]
    public void ListDatesShowTimeTodayDayAndMonthThisYearAndTheYearBefore()
    {
        var now = new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero).ToLocalTime();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 5, 0, now.Offset);
        Assert.Equal(today.ToString("t", System.Globalization.CultureInfo.CurrentCulture), MailColumnLayout.TimeLabel(today, now));
        var earlier = today.AddDays(-30);
        Assert.Equal(earlier.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture), MailColumnLayout.TimeLabel(earlier, now));
        var lastYear = today.AddYears(-1);
        Assert.Equal(lastYear.ToString("d", System.Globalization.CultureInfo.CurrentCulture), MailColumnLayout.TimeLabel(lastYear, now));
        Assert.Contains("(UTC", MailColumnLayout.FullDate(today));
    }

    [Fact]
    public void SenderFallsBackToAddressThenUnknownWithoutChangingTheStoredFields()
    {
        var m = new StoredMessage { FromName = "", FromAddress = "ava@example.com" };
        Assert.Equal("ava@example.com", m.SenderLabel);
        Assert.Equal("ava@example.com", m.SenderFull);
        Assert.Equal("", m.FromName);
        Assert.Equal("(unknown sender)", new StoredMessage().SenderLabel);
        Assert.Equal("Ava <ava@example.com>", new StoredMessage { FromName = "Ava", FromAddress = "ava@example.com" }.SenderFull);
        Assert.Equal("(no subject)", new StoredMessage { Subject = " " }.SubjectLabel);
    }
}
