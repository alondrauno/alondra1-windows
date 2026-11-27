using System.Diagnostics;
using System.IO;
using System.Text;
using Alondra1.Core.Search;

namespace Alondra1.App;

/// <summary>
/// Reads a PDF's text in a separate process (this app started with <see cref="Flag"/>), so a malformed or hostile PDF
/// can only fail, hang or crash that process: it is stopped after <see cref="AttachmentText.PdfTimeLimit"/>, runs at
/// low priority, and its output is capped. The text comes back over a pipe; nothing is written to a temporary file.
/// </summary>
public static class PdfTextProcess
{
    public const string Flag = "--read-pdf-text";

    /// <summary>In the child: writes the text to standard output as UTF-8. Exit code 0 when read, 2 when not.</summary>
    public static int RunChild(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > AttachmentText.MaxPdfBytes)
                return 2;
            var text = Alondra1.Mail.PdfText.Read(file, AttachmentText.MaxPdfPages, AttachmentText.MaxChars);
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

    /// <summary>In the app: the PDF's text, or null when it could not be read in time.</summary>
    public static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return null;
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(Flag);
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
        limit.CancelAfter(AttachmentText.PdfTimeLimit);
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
