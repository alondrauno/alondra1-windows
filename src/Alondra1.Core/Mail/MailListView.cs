namespace Alondra1.Core.Mail;

/// <summary>What the message list can be sorted by (Mail UI spec §5.1). Date, newest first, is the default.</summary>
public enum MailSortField { Date, From, Subject }

/// <summary>
/// Message list filters (Mail UI spec §5.1): unread, flagged, with attachments, and one account in a unified view.
/// They apply to the messages on this PC in the current folder and account scope.
/// </summary>
public sealed record MailListFilter(bool Unread = false, bool Flagged = false, bool Attachments = false, string? AccountId = null)
{
    public static readonly MailListFilter None = new();

    public bool IsActive => Unread || Flagged || Attachments || AccountId is not null;
}

/// <summary>One visible filter chip: its label and the filter without it (for its remove button).</summary>
public sealed record MailFilterChip(string Label, MailListFilter Without);

public static class MailListView
{
    public static bool Matches(StoredMessage m, MailListFilter f) =>
        (!f.Unread || m.IsUnread)
        && (!f.Flagged || m.IsFlagged)
        && (!f.Attachments || m.HasAttachments)
        && (f.AccountId is null || m.AccountId == f.AccountId);

    /// <summary>Filters and sorts with a stable tie-break (date, account, folder, uid), so equal rows never swap places.</summary>
    public static List<StoredMessage> Apply(IEnumerable<StoredMessage> messages, MailListFilter filter, MailSortField sort, bool descending)
    {
        var rows = messages.Where(m => Matches(m, filter));
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

    public static string SortLabel(MailSortField sort, bool descending) => sort switch
    {
        MailSortField.From => descending ? "From (Z to A)" : "From (A to Z)",
        MailSortField.Subject => descending ? "Subject (Z to A)" : "Subject (A to Z)",
        _ => descending ? "Date (newest first)" : "Date (oldest first)",
    };

    /// <summary>The active filters as removable chips.</summary>
    public static IReadOnlyList<MailFilterChip> Chips(MailListFilter f, Func<string, string> accountName)
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
        return chips;
    }
}
