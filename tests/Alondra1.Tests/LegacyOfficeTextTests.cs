using System.Text;
using Alondra1.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §8.3 / T19: legacy binary Office files (made by Office itself) give their text; nothing else is read.</summary>
public class LegacyOfficeTextTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy", name));

    private static string Read(string name)
    {
        using var stream = new MemoryStream(Fixture(name));
        return OfficeText.Read(stream, 200_000);
    }

    [Fact]
    public void WordTextIsReadWithFieldResultsButNotFieldCodes()
    {
        var text = Read("sample.doc");
        Assert.Contains("Quarterly budget review for the lighthouse project", text);
        Assert.Contains("Contract 4711", text);
        Assert.Contains("Visible link text", text);
        Assert.DoesNotContain("hidden-field-code", text);
        Assert.DoesNotContain("HYPERLINK", text);
        Assert.Contains("Ünïcode café naïve résumé", text);
    }

    [Fact]
    public void ExcelSheetNamesTextAndNumbersAreRead()
    {
        var text = Read("sample.xls");
        Assert.Contains("Invoices", text);
        Assert.Contains("Invoice lighthouse", text);
        Assert.Contains("Grüße aus München", text);
        Assert.Contains("4711", text);
        Assert.Contains("12.5", text);
        Assert.Contains("1234567", text);
    }

    [Fact]
    public void PowerPointSlideTextAndNotesAreReadWithoutMasterPlaceholders()
    {
        var text = Read("sample.ppt");
        Assert.Contains("Roadmap lighthouse", text);
        Assert.Contains("Budget 2027 milestones", text);
        Assert.Contains("Speaker note about Ünïcode", text);
        Assert.DoesNotContain("Click to edit Master", text);
    }

    [Fact]
    public void PasswordProtectedFilesAreRejected()
    {
        var error = Assert.ThrowsAny<Exception>(() => Read("encrypted.doc"));
        Assert.DoesNotContain("Secret", error.Message);
    }

    [Fact]
    public void DamagedCompoundFilesAreRejectedNotRead()
    {
        var doc = Fixture("sample.doc");
        // Truncated, and a sector chain that points to itself.
        Assert.ThrowsAny<Exception>(() => LegacyOfficeText.Read(doc[..2048], 10_000));
        var looped = (byte[])doc.Clone();
        BitConverter.GetBytes(0u).CopyTo(looped, 0x4C); // first FAT sector is sector 0
        BitConverter.GetBytes(0u).CopyTo(looped, 0x30); // directory starts at sector 0 too
        Assert.ThrowsAny<Exception>(() => LegacyOfficeText.Read(looped, 10_000));
        Assert.ThrowsAny<Exception>(() => LegacyOfficeText.Read(Encoding.ASCII.GetBytes("\u00d0\u00cf not really"), 10_000));
    }

    [Fact]
    public void TextIsCapped() => Assert.True(LegacyOfficeText.Read(Fixture("sample.doc"), 20).Length <= 20);

    [Fact]
    public void LegacyExtensionsAndTypesAreOffice()
    {
        foreach (var (name, type) in new[] { ("a.doc", "application/octet-stream"), ("b", "application/vnd.ms-excel"), ("c.ppt", ""), ("d", "application/msword") })
            Assert.Equal(Alondra1.Core.Search.AttachmentTextKind.Office,
                Alondra1.Core.Search.AttachmentText.KindOf(new Alondra1.Core.Mail.StoredAttachment { FileName = name, ContentType = type }));
    }
}
