using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Alondra1.Mail;

/// <summary>
/// Text of a PDF attachment for search (Mail UI spec §8.3), with PdfPig: a fully managed parser that only reads the
/// file's text. Nothing in the PDF runs: no JavaScript, forms, embedded files, launch actions or external references,
/// and no fonts or images are fetched. Bounded by pages and characters; the app also runs it in a separate process
/// with a time limit (see <c>--read-attachment-text</c>), so a hostile file can at worst fail or be stopped. Pages that
/// look scanned (an image and almost no text) are reported so the app can run OCR on them.
/// </summary>
public static class PdfText
{
    /// <summary>A page with fewer letters than this but with an image is treated as scanned (a candidate for OCR).</summary>
    public const int ScannedPageLetters = 20;

    public static string Read(Stream stream, int maxPages, int maxChars) => ReadWithScans(stream, maxPages, maxChars).Text;

    /// <summary>The text, and the pages (1-based) that look scanned.</summary>
    public static (string Text, IReadOnlyList<int> ScannedPages) ReadWithScans(Stream stream, int maxPages, int maxChars)
    {
        using var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var text = new StringBuilder();
        var scanned = new List<int>();
        var pages = Math.Min(document.NumberOfPages, maxPages);
        for (var number = 1; number <= pages && text.Length < maxChars; number++)
        {
            var page = document.GetPage(number);
            var letters = 0;
            // Words joined by spaces: PdfPig's page text can run words together where the PDF has no space glyphs.
            foreach (var word in page.GetWords())
            {
                if (text.Length >= maxChars)
                    break;
                letters += word.Text.Count(char.IsLetterOrDigit);
                text.Append(word.Text).Append(' ');
            }

            if (letters < ScannedPageLetters && HasImage(page))
                scanned.Add(number);
            text.Append('\n');
        }

        return (text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString(), scanned);
    }

    private static bool HasImage(Page page)
    {
        try
        {
            return page.GetImages().Any();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
