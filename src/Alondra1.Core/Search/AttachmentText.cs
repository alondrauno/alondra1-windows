using System.Text;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;

namespace Alondra1.Core.Search;

/// <summary>What kind of attachment text search can read.</summary>
public enum AttachmentTextKind { None, PlainText, Pdf }

/// <summary>
/// Attachment content search (Mail UI spec §8.3) for a small documented allowlist: plain-text files (.txt, .csv, .md,
/// .log, text/plain, up to 1 MB) and PDF files (up to 10 MB, the first 50 pages, 200,000 characters). Only files already
/// downloaded to this PC are read, once, after sync; nothing is downloaded for search. Plain text is read as text;
/// PDFs are read by a managed text parser in a separate process with a time limit. Nothing in a file is executed or
/// opened in another app, and archives are not unpacked. The text is kept with the message bodies in the local
/// database (same storage and retention) and removed when the attachment's file is removed. Other formats (Office,
/// images, archives) are searched by name only.
/// </summary>
public static class AttachmentText
{
    public const int MaxTextBytes = 1_000_000;
    public const long MaxPdfBytes = 10_000_000;
    public const int MaxPdfPages = 50;
    public const int MaxChars = 200_000;
    public static readonly TimeSpan PdfTimeLimit = TimeSpan.FromSeconds(20);

    public static readonly IReadOnlyList<string> TextExtensions = [".txt", ".csv", ".md", ".log"];

    /// <summary>Stored states: not read yet (""), read, not a readable kind, too large, or could not be read.</summary>
    public const string Pending = "", Read = "read", Unsupported = "unsupported", TooLarge = "too-large", Failed = "failed";

    public static AttachmentTextKind KindOf(StoredAttachment a)
    {
        if (MailText.IsExecutableName(a.FileName))
            return AttachmentTextKind.None;
        var extension = Path.GetExtension(a.FileName);
        if (a.ContentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return AttachmentTextKind.Pdf;
        if (a.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) || TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return AttachmentTextKind.PlainText;
        return AttachmentTextKind.None;
    }

    /// <summary>The first <see cref="MaxTextBytes"/> of a plain-text file as UTF-8, at most <see cref="MaxChars"/> characters.</summary>
    public static async Task<string> ReadPlainTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxTextBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
            read += n;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    /// <summary>True when every term of the query appears in the text (case-insensitive).</summary>
    public static bool Matches(string text, string[] terms) => terms.Length > 0 && SearchText.Matches(terms, text);
}

/// <summary>
/// Reads the text of downloaded attachments that have not been read yet, a few at a time, and stores it (or why it
/// could not be read). <paramref name="readPdf"/> returns the text, or null when the file could not be read in time.
/// </summary>
public sealed class AttachmentTextReader(IPersonalStore store, Func<string, string> resolve, Func<string, CancellationToken, Task<string?>> readPdf)
{
    /// <summary>Reads up to <paramref name="limit"/> pending attachments; returns how many were handled.</summary>
    public async Task<int> ReadPendingAsync(int limit, CancellationToken cancellationToken)
    {
        var pending = await store.ListAttachmentsToReadAsync(limit, cancellationToken).ConfigureAwait(false);
        foreach (var attachment in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, state) = await ReadOneAsync(attachment, cancellationToken).ConfigureAwait(false);
            await store.SetAttachmentTextAsync(attachment.Id, text, state, cancellationToken).ConfigureAwait(false);
        }

        return pending.Count;
    }

    private async Task<(string? Text, string State)> ReadOneAsync(StoredAttachment attachment, CancellationToken cancellationToken)
    {
        var kind = AttachmentText.KindOf(attachment);
        if (kind == AttachmentTextKind.None || string.IsNullOrEmpty(attachment.CacheRelativePath))
            return (null, AttachmentText.Unsupported);
        if ((kind == AttachmentTextKind.PlainText && attachment.Size > AttachmentText.MaxTextBytes) || (kind == AttachmentTextKind.Pdf && attachment.Size > AttachmentText.MaxPdfBytes))
            return (null, AttachmentText.TooLarge);
        var path = resolve(attachment.CacheRelativePath);
        try
        {
            if (kind == AttachmentTextKind.PlainText)
            {
                await using var stream = File.OpenRead(path);
                return (await AttachmentText.ReadPlainTextAsync(stream, cancellationToken).ConfigureAwait(false), AttachmentText.Read);
            }

            if (new FileInfo(path).Length > AttachmentText.MaxPdfBytes)
                return (null, AttachmentText.TooLarge);
            var text = await readPdf(path, cancellationToken).ConfigureAwait(false);
            return text is null ? (null, AttachmentText.Failed) : (text, AttachmentText.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, AttachmentText.Failed);
        }
    }
}
