using System.Text;
using Alondra1.Core.Mail;

namespace Alondra1.Core.Search;

/// <summary>
/// Attachment content search (Mail UI spec §8.3) for a small documented allowlist: plain-text files (.txt, .csv, .md,
/// .log, text/plain) that are already downloaded to this PC, up to <see cref="MaxBytes"/> each and
/// <see cref="MaxFiles"/> per query. Files are read as text only: nothing is executed, opened in another app, unpacked
/// or downloaded for search, and no second copy of the text is kept (no separate index). Other formats (PDF, Office,
/// archives) are searched by name only.
/// </summary>
public static class AttachmentText
{
    public const int MaxBytes = 1_000_000;
    public const int MaxFiles = 200;

    public static readonly IReadOnlyList<string> Extensions = [".txt", ".csv", ".md", ".log"];

    public static bool IsSearchable(StoredAttachment a) =>
        a.Downloaded && !string.IsNullOrEmpty(a.CacheRelativePath) && a.Size <= MaxBytes
        && (a.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
            || Extensions.Contains(Path.GetExtension(a.FileName), StringComparer.OrdinalIgnoreCase))
        && !MailText.IsExecutableName(a.FileName);

    /// <summary>True when every term of the query appears in the first <see cref="MaxBytes"/> of the file (UTF-8).</summary>
    public static async Task<bool> MatchesAsync(Stream stream, string[] terms, CancellationToken cancellationToken)
    {
        if (terms.Length == 0)
            return false;
        var buffer = new byte[MaxBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
            read += n;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        return SearchText.Matches(terms, text);
    }
}
