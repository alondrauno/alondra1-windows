using System.Text;
using UglyToad.PdfPig;

namespace Alondra1.Mail;

/// <summary>
/// Text of a PDF attachment for search (Mail UI spec §8.3), with PdfPig: a fully managed parser that only reads the
/// file's text. Nothing in the PDF runs: no JavaScript, forms, embedded files, launch actions or external references,
/// and no fonts or images are fetched. Bounded by pages and characters; the app also runs it in a separate process
/// with a time limit (see <c>--read-pdf-text</c>), so a hostile file can at worst fail or be stopped.
/// </summary>
public static class PdfText
{
    public static string Read(Stream stream, int maxPages, int maxChars)
    {
        using var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var text = new StringBuilder();
        var pages = Math.Min(document.NumberOfPages, maxPages);
        for (var number = 1; number <= pages && text.Length < maxChars; number++)
        {
            var page = document.GetPage(number);
            // Words joined by spaces: PdfPig's page text can run words together where the PDF has no space glyphs.
            foreach (var word in page.GetWords())
            {
                if (text.Length >= maxChars)
                    break;
                text.Append(word.Text).Append(' ');
            }

            text.Append('\n');
        }

        return text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
    }
}
