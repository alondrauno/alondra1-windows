using System.IO.Compression;
using System.Text;
using Alondra1.Core.Search;
using Alondra1.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §8.3 / T19: Office text is read from the text parts only, bounded, and nothing in the file runs.</summary>
public class OfficeTextTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    internal static byte[] Zip(params (string Name, string Content)[] parts) => Zip(parts.Select(p => (p.Name, Encoding.UTF8.GetBytes(p.Content))).ToArray());

    internal static byte[] Zip(params (string Name, byte[] Content)[] parts)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(content);
            }
        }

        return memory.ToArray();
    }

    internal static byte[] Docx(string body) => Zip(
        ("[Content_Types].xml", "<Types/>"),
        ("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>{body}</w:t></w:r><w:r><w:t xml:space=\"preserve\"> review</w:t></w:r></w:p></w:body></w:document>"),
        ("word/comments.xml", $"<w:comments xmlns:w=\"{W}\"><w:comment><w:p><w:r><w:t>Check with Maya</w:t></w:r></w:p></w:comment></w:comments>"),
        ("word/vbaProject.bin", "secretmacro AutoOpen Shell"));

    private static string Read(byte[] file, int maxChars = 10_000)
    {
        using var stream = new MemoryStream(file);
        return OfficeText.Read(stream, maxChars);
    }

    [Fact]
    public void WordBodyAndCommentsAreReadButNeverTheMacroPart()
    {
        var text = Read(Docx("Quarterly budget"));
        Assert.Contains("Quarterly budget", text);
        Assert.Contains("review", text);
        Assert.Contains("Check with Maya", text);
        Assert.DoesNotContain("secretmacro", text);
    }

    [Fact]
    public void ExcelReadsSharedStringsInlineTextAndValuesButNotStringIndexes()
    {
        var text = Read(Zip(
            ("xl/workbook.xml", $"<workbook xmlns=\"{S}\"/>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{S}\"><si><t>Invoice</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{S}\"><sheetData><row><c t=\"s\"><v>987654</v></c><c><v>4711</v></c><c t=\"inlineStr\"><is><t>lighthouse</t></is></c></row></sheetData></worksheet>")));
        Assert.Contains("Invoice", text);
        Assert.Contains("4711", text);
        Assert.Contains("lighthouse", text);
        Assert.DoesNotContain("987654", text);
    }

    [Fact]
    public void PowerPointSlidesAreInNumericOrderThenNotes()
    {
        var text = Read(Zip(
            ("ppt/presentation.xml", "<p:presentation xmlns:p=\"x\"/>"),
            ("ppt/slides/slide10.xml", $"<p:sld xmlns:p=\"x\" xmlns:a=\"{A}\"><a:p><a:r><a:t>tenth</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld xmlns:p=\"x\" xmlns:a=\"{A}\"><a:p><a:r><a:t>second</a:t></a:r></a:p></p:sld>"),
            ("ppt/notesSlides/notesSlide1.xml", $"<p:notes xmlns:p=\"x\" xmlns:a=\"{A}\"><a:p><a:r><a:t>speaker note</a:t></a:r></a:p></p:notes>")));
        Assert.True(text.IndexOf("second", StringComparison.Ordinal) < text.IndexOf("tenth", StringComparison.Ordinal));
        Assert.Contains("speaker note", text);
    }

    [Fact]
    public void OpenDocumentTextIsRead()
    {
        var text = Read(Zip(
            ("mimetype", "application/vnd.oasis.opendocument.text"),
            ("content.xml", "<office:document-content xmlns:office=\"o\" xmlns:text=\"t\"><office:body><office:text><text:p>Minutes of the meeting</text:p><text:p>Second line</text:p></office:text></office:body></office:document-content>")));
        Assert.Contains("Minutes of the meeting", text);
        Assert.Contains("Second line", text);
    }

    [Fact]
    public void EntitiesAndExternalReferencesAreRefused()
    {
        var hostile = Zip(("word/document.xml", $"<!DOCTYPE d [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>"));
        Assert.ThrowsAny<Exception>(() => Read(hostile));
    }

    [Fact]
    public void ZipBombsAndOversizedPartsAreStopped()
    {
        // 5 MB of zeros packs to a few KB: far beyond the allowed ratio.
        var bomb = Zip(("word/document.xml", Encoding.ASCII.GetBytes($"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>" + new string('0', 5_000_000) + "</w:t></w:r></w:p></w:body></w:document>")));
        Assert.ThrowsAny<InvalidDataException>(() => Read(bomb));
    }

    [Fact]
    public void TextIsCappedAndNonOfficeFilesAreRejected()
    {
        Assert.True(Read(Docx(new string('x', 5000)), maxChars: 100).Length <= 100);
        Assert.ThrowsAny<Exception>(() => Read(Zip(("readme.txt", "just a zip"))));
        Assert.ThrowsAny<Exception>(() => Read(Encoding.ASCII.GetBytes("\u00d0\u00cf\u0011\u00e0 legacy binary doc")));
    }

    [Fact]
    public void OfficeFilesHaveTheirOwnSizeLimit()
    {
        Assert.Equal(AttachmentText.MaxOfficeBytes, AttachmentText.MaxBytes(AttachmentTextKind.Office));
        Assert.Equal(0, AttachmentText.MaxBytes(AttachmentTextKind.None));
    }
}

public class AttachmentTextUpgradeTests
{
    [Fact]
    public async Task AttachmentsMarkedUnsupportedEarlierAreReadAgainAfterTheUpgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "personal.db");
        try
        {
            await using (var store = new Alondra1.Persistence.SqlitePersonalStore(path))
            {
                await store.InitializeAsync(default);
                var batch = new Alondra1.Core.Mail.PersonalBatch();
                batch.Upserts.Add(new Alondra1.Core.Mail.StoredMessage { AccountId = "w", FolderId = "INBOX", Uid = 1 });
                batch.Attachments.Add(new Alondra1.Core.Mail.StoredAttachment { Id = "a1", AccountId = "w", FolderId = "INBOX", Uid = 1, FileName = "plan.docx", Downloaded = true, CacheRelativePath = "w/plan.docx" });
                await store.CommitAsync(batch, default);
                await store.SetAttachmentTextAsync("a1", null, AttachmentText.Unsupported, default);
                Assert.Equal(0, await store.CountAttachmentsToReadAsync(default));
            }

            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                legacy.Open();
                using var command = legacy.CreateCommand();
                command.CommandText = "PRAGMA user_version = 3";
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var reopened = new Alondra1.Persistence.SqlitePersonalStore(path);
            await reopened.InitializeAsync(default);
            Assert.Equal(1, await reopened.CountAttachmentsToReadAsync(default));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
