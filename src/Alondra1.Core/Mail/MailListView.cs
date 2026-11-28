namespace Alondra1.Core.Mail;

/// <summary>What the message list can be sorted by (Mail UI spec §5.1). Date, newest first, is the default.</summary>
public enum MailSortField { Date, From, Subject }

/// <summary>
/// Message list filters (Mail UI spec §5.1, §8.2 advanced filters): unread, flagged, with attachments, one account in a
/// unified view, sender, recipient, subject, a received date range and an attachment name. They combine (all must
/// match) and apply to the messages on this PC in the current folder and account scope, and to All mail search.
/// Text fields match every word, ignoring case; no syntax is needed.
/// </summary>
public sealed record MailListFilter(
    bool Unread = false,
    bool Flagged = false,
    bool Attachments = false,
    string? AccountId = null,
    string? Sender = null,
    string? Recipient = null,
    string? Subject = null,
    DateOnly? Since = null,
    DateOnly? Until = null,
    string? AttachmentName = null,
    string? TagId = null)
{
    public static readonly MailListFilter None = new();

    public bool IsActive => Unread || Flagged || Attachments || AccountId is not null || HasText(Sender) || HasText(Recipient)
        || HasText(Subject) || Since is not null || Until is not null || HasText(AttachmentName) || TagId is not null;

    /// <summary>Blank text fields become null, and a reversed date range is put in order.</summary>
    public MailListFilter Normalized()
    {
        static string? Clean(string? s) => HasText(s) ? s!.Trim() : null;
        var (since, until) = Since is { } a && Until is { } b && a > b ? (Until, Since) : (Since, Until);
        return this with { Sender = Clean(Sender), Recipient = Clean(Recipient), Subject = Clean(Subject), AttachmentName = Clean(AttachmentName), Since = since, Until = until };
    }

    public static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);
}

/// <summary>One visible filter chip: its label and the filter without it (for its remove button).</summary>
public sealed record MailFilterChip(string Label, MailListFilter Without);

public static class MailListView
{
    /// <summary>
    /// True when the message passes every filter. <paramref name="attachmentMatches"/> lists the messages with an
    /// attachment whose name matches <see cref="MailListFilter.AttachmentName"/>; without it, that filter matches nothing.
    /// </summary>
    public static bool Matches(StoredMessage m, MailListFilter f, IReadOnlySet<MessageKey>? attachmentMatches = null, IReadOnlySet<string>? taggedRefs = null)
    {
        // A tag filter needs the tagged messages (by their tag reference); without them it matches nothing.
        if (f.TagId is not null && (taggedRefs is null || !taggedRefs.Contains(Alondra1.Core.Tags.TagRules.MailRef(m.AccountId, m.MessageId, m.FolderId, m.Uid))))
            return false;
        if ((f.Unread && !m.IsUnread) || (f.Flagged && !m.IsFlagged) || (f.Attachments && !m.HasAttachments)
            || (f.AccountId is not null && m.AccountId != f.AccountId))
            return false;
        if (MailListFilter.HasText(f.Sender) && !Contains(f.Sender!, m.FromName, m.FromAddress))
            return false;
        if (MailListFilter.HasText(f.Recipient) && !Contains(f.Recipient!, m.Recipients))
            return false;
        if (MailListFilter.HasText(f.Subject) && !Contains(f.Subject!, m.Subject))
            return false;
        var day = DateOnly.FromDateTime(m.ReceivedUtc.ToLocalTime().Date);
        if ((f.Since is { } since && day < since) || (f.Until is { } until && day > until))
            return false;
        return !MailListFilter.HasText(f.AttachmentName)
            || (attachmentMatches is not null && attachmentMatches.Contains(new MessageKey(m.AccountId, m.FolderId, m.Uid)));
    }

    /// <summary>Every word of <paramref name="text"/> appears in one of the fields, ignoring case.</summary>
    public static bool Contains(string text, params string?[] fields)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length > 0 && words.All(w => fields.Any(f => f?.Contains(w, StringComparison.CurrentCultureIgnoreCase) == true));
    }

    /// <summary>Filters and sorts with a stable tie-break (date, account, folder, uid), so equal rows never swap places.</summary>
    public static List<StoredMessage> Apply(IEnumerable<StoredMessage> messages, MailListFilter filter, MailSortField sort, bool descending,
        IReadOnlySet<MessageKey>? attachmentMatches = null, IReadOnlySet<string>? taggedRefs = null)
    {
        var rows = messages.Where(m => Matches(m, filter, attachmentMatches, taggedRefs));
        IOrderedEnumerable<StoredMessage> ordered = sort switch
        {
            MailSortField.From => descending
                ? rows.OrderByDescending(SenderName, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(SenderName, StringComparer.CurrentCultureIgnoreCase),
            MailSortField.Subject => descending
                ? rows.OrderByDescending(SubjectKey, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(SubjectKey, StringComparer.CurrentCultureIgnoreCase),
            _ => descending ? rows.OrderByDescending(m => m.ReceivedUtc) : rows.OrderBy(m => m.ReceivedUtc),
        };
        if (sort != MailSortField.Date)
            ordered = ordered.ThenByDescending(m => m.ReceivedUtc);
        return ordered.ThenBy(m => m.AccountId, StringComparer.Ordinal).ThenBy(m => m.FolderId, StringComparer.Ordinal).ThenByDescending(m => m.Uid).ToList();
    }

    /// <summary>The sender as shown: display name, else address, else "(unknown sender)".</summary>
    public static string SenderName(StoredMessage m) =>
        !string.IsNullOrWhiteSpace(m.FromName) ? m.FromName.Trim() : !string.IsNullOrWhiteSpace(m.FromAddress) ? m.FromAddress.Trim() : "(unknown sender)";

    /// <summary>Subject for sorting without reply and forward prefixes ("Re: Budget" sorts with "Budget").</summary>
    public static string SubjectKey(StoredMessage m)
    {
        var s = (m.Subject ?? "").Trim();
        while (true)
        {
            var colon = s.IndexOf(':');
            if (colon is > 0 and <= 4 && s[..colon].Trim().ToLowerInvariant() is "re" or "fw" or "fwd" or "aw" or "wg" or "sv" or "tr")
                s = s[(colon + 1)..].TrimStart();
            else
                return s;
        }
    }

    /// <summary>
    /// Date heading for a message (local days; weeks start on the culture's first day): Today, Yesterday, Earlier this
    /// week, Last week, Earlier this month, Last month, then the month and year ("September 2026").
    /// </summary>
    public static string DateGroup(DateTimeOffset received, DateTimeOffset now)
    {
        var day = received.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (day >= today)
            return "Today";
        if (day == today.AddDays(-1))
            return "Yesterday";
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7));
        if (day >= weekStart)
            return "Earlier this week";
        if (day >= weekStart.AddDays(-7))
            return "Last week";
        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (day >= monthStart)
            return "Earlier this month";
        if (day >= monthStart.AddMonths(-1))
            return "Last month";
        return day.ToString("MMMM yyyy", culture);
    }

    /// <summary>" — " and the start of the preview as one plain line (whitespace collapsed), or "" without a preview.</summary>
    public static string Snippet(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
            return "";
        var text = string.Join(' ', preview.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return " — " + (text.Length > 160 ? text[..160] : text);
    }

    public static string SortLabel(MailSortField sort, bool descending) => sort switch
    {
        MailSortField.From => descending ? "From (Z to A)" : "From (A to Z)",
        MailSortField.Subject => descending ? "Subject (Z to A)" : "Subject (A to Z)",
        _ => descending ? "Date (newest first)" : "Date (oldest first)",
    };

    /// <summary>The active filters as removable chips.</summary>
    public static IReadOnlyList<MailFilterChip> Chips(MailListFilter f, Func<string, string> accountName, Func<string, string>? tagName = null)
    {
        var chips = new List<MailFilterChip>();
        if (f.Unread)
            chips.Add(new("Unread", f with { Unread = false }));
        if (f.Flagged)
            chips.Add(new("Flagged", f with { Flagged = false }));
        if (f.Attachments)
            chips.Add(new("Has attachments", f with { Attachments = false }));
        if (f.AccountId is { } id)
            chips.Add(new($"Account: {accountName(id)}", f with { AccountId = null }));
        if (MailListFilter.HasText(f.Sender))
            chips.Add(new($"From: {f.Sender}", f with { Sender = null }));
        if (MailListFilter.HasText(f.Recipient))
            chips.Add(new($"To: {f.Recipient}", f with { Recipient = null }));
        if (MailListFilter.HasText(f.Subject))
            chips.Add(new($"Subject: {f.Subject}", f with { Subject = null }));
        if (f.Since is not null || f.Until is not null)
            chips.Add(new(DateRangeLabel(f.Since, f.Until), f with { Since = null, Until = null }));
        if (MailListFilter.HasText(f.AttachmentName))
            chips.Add(new($"Attachment: {f.AttachmentName}", f with { AttachmentName = null }));
        if (f.TagId is { } tag)
            chips.Add(new($"Tag: {tagName?.Invoke(tag) ?? "tag"}", f with { TagId = null }));
        return chips;
    }

    /// <summary>"Received 1 Nov 2026 – 27 Nov 2026", "Received since …", "Received until …", "Received on …".</summary>
    public static string DateRangeLabel(DateOnly? since, DateOnly? until)
    {
        static string D(DateOnly d) => d.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        return (since, until) switch
        {
            ({ } a, { } b) when a == b => $"Received on {D(a)}",
            ({ } a, { } b) => $"Received {D(a)} – {D(b)}",
            ({ } a, null) => $"Received since {D(a)}",
            (null, { } b) => $"Received until {D(b)}",
            _ => "",
        };
    }

    /// <summary>A short description of the active filters for search coverage ("Unread, From: maya").</summary>
    public static string Describe(MailListFilter f, Func<string, string> accountName, Func<string, string>? tagName = null) =>
        string.Join(", ", Chips(f, accountName, tagName).Select(c => c.Label));

    /// <summary>The quick date ranges offered in the filter menu, relative to <paramref name="today"/>.</summary>
    public static IReadOnlyList<(string Label, DateOnly Since, DateOnly Until)> QuickRanges(DateOnly today) =>
    [
        ("Today", today, today),
        ("Last 7 days", today.AddDays(-6), today),
        ("Last 30 days", today.AddDays(-29), today),
        ("This year", new DateOnly(today.Year, 1, 1), today),
    ];

    /// <summary>Reads a date typed in the dialog (local format, or yyyy-mm-dd); null when blank or not a date.</summary>
    public static bool TryParseDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (DateOnly.TryParse(text.Trim(), System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out var d)
            || DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d))
        {
            date = d;
            return true;
        }

        return false;
    }
}
