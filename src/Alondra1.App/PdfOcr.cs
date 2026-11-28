using System.IO;
using System.Text;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Alondra1.App;

/// <summary>
/// OCR for scanned PDF pages (Mail UI spec §8.3), with what Windows already has: the Windows PDF renderer draws the page
/// as an image and Windows OCR reads it, in the user's installed OCR languages. Everything stays on this PC.
/// It runs only inside the attachment text process (separate, time-limited, low priority), only for pages PdfPig has
/// already parsed and found to hold an image and almost no text, and for at most <see cref="MaxPages"/> pages.
/// Without an installed OCR language it does nothing.
/// </summary>
public static class PdfOcr
{
    public const int MaxPages = 10;

    /// <summary>About 250 dpi (page sizes are in 1/96 inch): enough for printed text, small enough to stay quick.</summary>
    private const double Scale = 2.6;

    public static bool IsAvailable => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    public static async Task<string> RecognizeAsync(string path, IReadOnlyList<int> pages, int maxChars)
    {
        if (pages.Count == 0 || OcrEngine.TryCreateFromUserProfileLanguages() is not { } engine)
            return "";
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var document = await PdfDocument.LoadFromFileAsync(file);
        if (document.IsPasswordProtected)
            return "";
        var text = new StringBuilder();
        foreach (var number in pages.Take(MaxPages))
        {
            if (number < 1 || number > document.PageCount || text.Length >= maxChars)
                continue;
            using var page = document.GetPage((uint)(number - 1));
            var longest = Math.Max(page.Size.Width, page.Size.Height);
            if (longest <= 0)
                continue;
            var scale = Math.Min(Scale, OcrEngine.MaxImageDimension / longest);
            using var image = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(image, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
            });
            var decoder = await BitmapDecoder.CreateAsync(image);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            text.Append(result.Text).Append('\n');
        }

        return text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
    }
}
