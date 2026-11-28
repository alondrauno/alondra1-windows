using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Alondra1.App;
using Alondra1.Mail;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §8.3 / T19: scanned PDF pages are found and read with Windows OCR; text pages are not OCR'd.</summary>
public class PdfOcrTests
{
    /// <summary>A PNG of the text, as a scanner would produce it (black on white, large print).</summary>
    private static byte[] TextImage(string text)
    {
        byte[]? png = null;
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1600, 400));
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 72, Brushes.Black, 1.0);
                dc.DrawText(formatted, new Point(60, 150));
            }

            var bitmap = new RenderTargetBitmap(1600, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            png = stream.ToArray();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return png!;
    }

    /// <summary>Page 1: real text. Page 2: only a picture of text (a scan).</summary>
    internal static byte[] ScannedPdf(string scannedText)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Cover page with real text for the search index", 12, new PdfPoint(40, 700), font);
        var scan = builder.AddPage(PageSize.A4);
        scan.AddPng(TextImage(scannedText), new PdfRectangle(20, 500, 575, 640));
        return builder.Build();
    }

    [Fact]
    public void ImageOnlyPagesAreReportedAsScannedAndTextPagesAreNot()
    {
        using var stream = new MemoryStream(ScannedPdf("INVOICE 4711 LIGHTHOUSE"));
        var (text, scanned) = PdfText.ReadWithScans(stream, 50, 10_000);
        Assert.Contains("Cover page", text);
        Assert.Equal([2], scanned);
        Assert.DoesNotContain("LIGHTHOUSE", text);
    }

    [Fact]
    public async Task ScannedPagesAreReadWithWindowsOcr()
    {
        if (!PdfOcr.IsAvailable)
            return; // no OCR language installed on this machine: nothing to read with (the app then skips OCR too)
        var path = Path.Combine(Path.GetTempPath(), "alondra1-ocr-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, ScannedPdf("INVOICE 4711 LIGHTHOUSE"));
        try
        {
            var text = await PdfOcr.RecognizeAsync(path, [2], 10_000);
            Assert.Contains("LIGHTHOUSE", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4711", text);
            Assert.Empty(await PdfOcr.RecognizeAsync(path, [], 10_000));
            Assert.True((await PdfOcr.RecognizeAsync(path, [2], 5)).Length <= 5);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
