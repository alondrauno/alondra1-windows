using System.Globalization;
using System.Text;

namespace Alondra1.Core.Rules;

/// <summary>
/// Normalization and matching used by rules and the blocklist (Part B §3.2, §5.1), documented here once:
/// - text is compared after Unicode NFC, invariant lower-casing and collapsing white space, so "Limited  OFFER" matches
///   "limited offer";
/// - a whole word or phrase must not have a letter or digit (any script) right before or after it;
/// - addresses are the parsed mailbox only (never the display name); the domain is compared in ASCII (IDNA) and lower
///   case, the local part case-insensitively (as nearly all providers treat it); plus addressing and dots are kept;
/// - a domain matches exactly, or with "include subdomains" also any name ending in "." + domain (so blocking
///   example.com never matches notexample.com or example.com.evil).
/// </summary>
public static class MailMatch
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        var space = false;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Whether <paramref name="phrase"/> occurs in <paramref name="text"/> (both folded) as a whole word or phrase, or anywhere.</summary>
    public static bool ContainsText(string foldedText, string foldedPhrase, bool wholeWord)
    {
        if (foldedPhrase.Length == 0)
            return false;
        var start = 0;
        while (true)
        {
            var at = foldedText.IndexOf(foldedPhrase, start, StringComparison.Ordinal);
            if (at < 0)
                return false;
            if (!wholeWord || (!WordCharBefore(foldedText, at) && !WordCharAt(foldedText, at + foldedPhrase.Length)))
                return true;
            start = at + 1;
        }
    }

    private static bool WordCharBefore(string text, int index)
    {
        if (index <= 0)
            return false;
        var i = index - 1;
        if (char.IsLowSurrogate(text[i]) && i > 0)
            i--;
        return Rune.TryGetRuneAt(text, i, out var rune) && IsWordRune(rune);
    }

    private static bool WordCharAt(string text, int index) =>
        index < text.Length && Rune.TryGetRuneAt(text, index, out var rune) && IsWordRune(rune);

    private static bool IsWordRune(Rune rune) => Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    /// <summary>The mailbox of "Name &lt;a@b&gt;" or "a@b", normalized (null when it is not an address).</summary>
    public static string? Address(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim();
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        if (open >= 0 && close > open)
            text = text[(open + 1)..close];
        text = text.Trim().Trim('"', '\'').Trim();
        var at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl))
            return null;
        var domain = Domain(text[(at + 1)..]);
        return domain is null ? null : text[..at].Normalize(NormalizationForm.FormC).ToLowerInvariant() + "@" + domain;
    }

    /// <summary>Every address in a recipient list ("a@b, Name &lt;c@d&gt;; e@f"), normalized; unparseable parts are skipped.</summary>
    public static IReadOnlyList<string> Addresses(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
            return [];
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var angle = 0;
        foreach (var c in list)
        {
            if (c == '"')
                quoted = !quoted;
            else if (c == '<')
                angle++;
            else if (c == '>' && angle > 0)
                angle--;
            if ((c == ',' || c == ';') && !quoted && angle == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts.Select(Address).Where(a => a is not null).Cast<string>().ToList();
    }

    /// <summary>A domain in ASCII (IDNA) lower case without a trailing dot; null when malformed or a wildcard.</summary>
    public static string? Domain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim();
        if (text.StartsWith('@'))
            text = text[1..];
        if (text.EndsWith('.'))
            text = text[..^1];
        if (text.Length == 0 || text.Contains('*') || text.Contains('@') || text.Any(char.IsWhiteSpace) || text.Contains(".."))
            return null;
        try
        {
            var ascii = Idn.GetAscii(text.Normalize(NormalizationForm.FormC)).ToLowerInvariant();
            return ascii.Split('.').All(label => label.Length is > 0 and <= 63) && ascii.Length <= 253 ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string? DomainOf(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at < 0 ? null : address![(at + 1)..];
    }

    /// <summary>Exact domain, or with <paramref name="includeSubdomains"/> any name below it (label boundary).</summary>
    public static bool DomainMatches(string? domain, string blocked, bool includeSubdomains) =>
        domain is not null && (domain == blocked || (includeSubdomains && domain.EndsWith("." + blocked, StringComparison.Ordinal)));
}
