using System.Net.Mail;
using Alondra1.Core.Config;

namespace Alondra1.Core.Mail;

/// <summary>
/// Which account a new message, reply or forward is sent from (Mail UI spec §3.2). The choice is always shown as an
/// explicit From before sending; when there is no safe default the user must choose. Never the first account in the
/// list by accident, never matched by display name.
/// </summary>
public static class ComposeIdentity
{
    /// <summary>
    /// The account id to preselect, or null when the user has to choose. Reply and forward use the message's own
    /// account; a new message in an account view uses that account; in the unified view the configured default
    /// account is used, or the only account when there is just one.
    /// </summary>
    public static string? DefaultAccount(IReadOnlyList<AccountConfig> accounts, MailScope scope, string? messageAccountId, string? defaultAccountId)
    {
        bool Exists(string? id) => id is not null && accounts.Any(a => a.Id == id);
        if (messageAccountId is not null)
            return Exists(messageAccountId) ? messageAccountId : null; // the receiving account is gone: choose
        if (!scope.IsUnified)
            return Exists(scope.AccountId) ? scope.AccountId : null;
        if (Exists(defaultAccountId))
            return defaultAccountId;
        return accounts.Count == 1 ? accounts[0].Id : null;
    }

    /// <summary>The From line shown for an account: "Name &lt;address&gt;".</summary>
    public static string Label(AccountConfig account) =>
        string.IsNullOrWhiteSpace(account.DisplayName) || account.DisplayName == account.Address
            ? account.Address
            : $"{account.DisplayName} <{account.Address}>";

    /// <summary>
    /// Reply all: the sender plus every other recipient of the message, without the replying account's own address and
    /// without duplicates (case-insensitive on the address).
    /// </summary>
    public static List<string> ReplyAllRecipients(StoredMessage message, string ownAddress)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownAddress.Trim() };
        void Add(string candidate)
        {
            var address = Address(candidate);
            if (address.Length > 0 && seen.Add(address))
                result.Add(address);
        }

        Add(message.FromAddress);
        foreach (var part in SplitAddresses(message.Recipients))
            Add(part);
        return result;
    }

    public static IEnumerable<string> SplitAddresses(string list)
    {
        if (string.IsNullOrWhiteSpace(list))
            yield break;
        MailAddressCollection parsed;
        try
        {
            parsed = [];
            parsed.Add(list);
        }
        catch (FormatException)
        {
            parsed = [];
        }

        if (parsed.Count > 0)
        {
            foreach (var address in parsed)
                yield return address.Address;
            yield break;
        }

        foreach (var part in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    private static string Address(string candidate)
    {
        var text = candidate.Trim();
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        if (open >= 0 && close > open)
            text = text[(open + 1)..close].Trim();
        return text.Contains('@', StringComparison.Ordinal) ? text : "";
    }

    /// <summary>
    /// Up to two letters for an avatar or account marker, made on this PC: the first letters of the first two words,
    /// ignoring a "(address)" suffix and punctuation; "?" when there is no letter or digit.
    /// </summary>
    public static string Initials(string? name)
    {
        var text = name ?? "";
        var paren = text.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
            text = text[..paren];
        text = text.Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal);
        var words = new string(text.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : words.Length == 1 ? words[0][..1] : "?";
        return letters.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
    }
}
