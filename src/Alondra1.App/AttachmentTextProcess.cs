using System.Diagnostics;
using System.IO;
using System.Text;
using Alondra1.Core.Search;

namespace Alondra1.App;

/// <summary>
/// Reads a PDF or Office attachment's text in a separate process (this app started with <see cref="Flag"/>), so a
/// malformed or hostile file can only fail, hang or crash that process: it is stopped after
/// <see cref="AttachmentText.IsolatedTimeLimit"/>, runs at low priority, and its output is capped. The text comes back over
/// a pipe; nothing is written to a temporary file.
/// </summary>
public static class AttachmentTextProcess
{
    public const string Flag = "--read-attachment-text";

    /// <summary>
    /// In the child: writes the text to standard output as UTF-8. Exit code 0 when read, 2 when not. Scanned PDF pages
    /// (an image, almost no text) are read with Windows OCR; if OCR fails, the text PdfPig found is still returned.
    /// </summary>
    public static async Task<int> RunChildAsync(string kind, string path)
    {
        try
        {
            string? text;
            await using (var file = File.OpenRead(path))
            {
                switch (kind)
                {
                    case "pdf" when file.Length <= AttachmentText.MaxPdfBytes:
                        var (found, scanned) = Alondra1.Mail.PdfText.ReadWithScans(file, AttachmentText.MaxPdfPages, AttachmentText.MaxChars);
                        text = found;
                        if (scanned.Count > 0 && found.Length < AttachmentText.MaxChars)
                            text += await Ocr(path, scanned, AttachmentText.MaxChars - found.Length);
                        break;
                    case "office" when file.Length <= AttachmentText.MaxOfficeBytes:
                        text = Alondra1.Mail.OfficeText.Read(file, AttachmentText.MaxChars);
                        break;
                    default:
                        text = null;
                        break;
                }
            }

            if (text is null)
                return 2;
            using var output = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
            return 0;
        }
        catch (Exception)
        {
            // Any parser error is "could not be read"; no file content or details are reported.
            return 2;
        }
    }

    private static async Task<string> Ocr(string path, IReadOnlyList<int> pages, int maxChars)
    {
        try
        {
            return await PdfOcr.RecognizeAsync(path, pages, maxChars);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>In the app: the file's text, or null when it could not be read in time.</summary>
    public static async Task<string?> ReadAsync(AttachmentTextKind kind, string path, CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath;
        var name = kind switch { AttachmentTextKind.Pdf => "pdf", AttachmentTextKind.Office => "office", _ => null };
        if (exe is null || name is null)
            return null;
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(Flag);
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
        if (process is null)
            return null;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(AttachmentText.IsolatedTimeLimit);
        try
        {
            var buffer = new char[AttachmentText.MaxChars + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await process.StandardOutput.ReadAsync(buffer.AsMemory(read), limit.Token).ConfigureAwait(false)) > 0)
                read += n;
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? new string(buffer, 0, Math.Min(read, AttachmentText.MaxChars)) : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
