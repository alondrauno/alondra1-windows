namespace Alondra1.Core.Mail;

/// <summary>The message list columns the user can resize (Mail UI spec §5.1, table mode).</summary>
public enum MailColumn { From, Account, Received }

/// <summary>
/// The widths the user chose for the resizable columns, kept per device. Null means "not resized": the column then
/// follows the default proportions, so a fresh install adapts to any window width.
/// </summary>
public sealed record MailColumnPreferences(double? From = null, double? Account = null, double? Received = null)
{
    public static readonly MailColumnPreferences Default = new();
}

/// <summary>Resolved pixel widths for one list width. A hidden column has width 0.</summary>
public sealed record MailColumnWidths(double Indicator, double From, double Account, double Subject, double Attachment, double Received)
{
    public bool ShowAccount => Account > 0;
    public bool ShowAttachment => Attachment > 0;
    public bool ShowReceived => Received > 0;
}

/// <summary>
/// Column widths for the message list table. Subject takes the remaining space and never drops below its minimum
/// while the other columns can still give way, so sender and subject truncate with an ellipsis instead of overlapping.
/// </summary>
public static class MailColumnLayout
{
    /// <summary>Unread dot and flag: reserved on every row and on the header.</summary>
    public const double IndicatorWidth = 26;
    public const double AttachmentWidth = 18;

    public const double MinFrom = 80;
    public const double MinAccount = 60;
    public const double MinSubject = 120;
    public const double MinReceived = 52;
    public const double MaxReceived = 180;
    public const double MaxAccount = 260;

    public const double DefaultAccount = 110;
    public const double DefaultReceived = 64;
    /// <summary>Sender share of the space left for sender and subject when not resized (about 1 : 2.55).</summary>
    public const double DefaultFromShare = 0.28;

    /// <summary>Below these list widths the date, attachment and account columns are dropped (in that reverse order).</summary>
    public const double ReceivedFromWidth = 285;
    public const double AttachmentFromWidth = 360;
    public const double AccountFromWidth = 520;

    public static MailColumnWidths Resolve(double width, MailColumnPreferences prefs, bool unified)
    {
        width = Math.Max(0, width);
        var received = width > ReceivedFromWidth ? Math.Clamp(prefs.Received ?? DefaultReceived, MinReceived, MaxReceived) : 0;
        var attachment = width > AttachmentFromWidth ? AttachmentWidth : 0;
        var showAccount = unified && width > AccountFromWidth;
        var available = Math.Max(0, width - IndicatorWidth - attachment - received);

        var account = showAccount ? Math.Clamp(prefs.Account ?? DefaultAccount, MinAccount, MaxAccount) : 0;
        var from = Math.Max(MinFrom, prefs.From ?? (available - account) * DefaultFromShare);

        // Not enough room: the account column, then the sender give way down to their minimums.
        var shortBy = from + account + MinSubject - available;
        if (shortBy > 0 && showAccount)
        {
            var give = Math.Min(shortBy, account - MinAccount);
            account -= give;
            shortBy -= give;
        }

        if (shortBy > 0)
            from = Math.Max(MinFrom, from - shortBy);
        from = Math.Min(from, available);
        account = Math.Min(account, Math.Max(0, available - from));
        var subject = Math.Max(0, available - from - account);
        return new MailColumnWidths(IndicatorWidth, Math.Round(from), Math.Round(account), Math.Floor(subject), attachment, received);
    }

    /// <summary>
    /// The user dragged a column edge by <paramref name="delta"/> px (or pressed an arrow key). Returns the new
    /// preferences, clamped so the subject keeps its minimum and no column goes below its own.
    /// </summary>
    public static MailColumnPreferences Resize(MailColumnPreferences prefs, MailColumn column, double delta, double width, bool unified)
    {
        var now = Resolve(width, prefs, unified);
        // Room the subject can still give up.
        var spare = Math.Max(0, now.Subject - MinSubject);
        switch (column)
        {
            case MailColumn.From:
                return prefs with { From = Math.Round(Math.Max(MinFrom, now.From + Math.Min(delta, spare))) };
            case MailColumn.Account when now.ShowAccount:
                return prefs with { Account = Math.Round(Math.Clamp(now.Account + Math.Min(delta, spare), MinAccount, MaxAccount)) };
            case MailColumn.Received when now.ShowReceived:
                // The date column's handle sits on its left edge: dragging left makes it wider.
                return prefs with { Received = Math.Round(Math.Clamp(now.Received + Math.Min(-delta, spare), MinReceived, MaxReceived)) };
            default:
                return prefs;
        }
    }

    /// <summary>The list date: local time for today, day and month this year, the short date before that.</summary>
    public static string TimeLabel(DateTimeOffset received, DateTimeOffset now)
    {
        var local = received.ToLocalTime();
        var today = now.ToLocalTime();
        if (local.Date == today.Date)
            return local.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        return local.Year == today.Year
            ? local.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>The full date for tooltips and details: weekday, date, time and the UTC offset.</summary>
    public static string FullDate(DateTimeOffset received)
    {
        var local = received.ToLocalTime();
        return $"{local.ToString("dddd d MMMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)} (UTC{local:zzz})";
    }
}
