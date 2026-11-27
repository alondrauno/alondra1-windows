using System.Text;
using Alondra1.Core.Mail;
using Alondra1.Core.Search;
using Alondra1.Mail;
using Alondra1.Persistence;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Alondra1.Tests;

/// <summary>
/// Mail UI spec §8.3 / T19: attachment text search reads only downloaded plain-text and PDF files, bounded, once, and
/// keeps the text with the same retention as the file.
/// </summary>
public class AttachmentTextTests
{
    private static StoredAttachment A(string name, string type = "application/octet-stream", long size = 100) =>
        new() { FileName = name, ContentType = type, Size = size, Downloaded = true, CacheRelativePath = "x/y.bin" };

    [Theory]
    [InlineData("notes.txt", "application/octet-stream", AttachmentTextKind.PlainText)]
    [InlineData("data.CSV", "application/octet-stream", AttachmentTextKind.PlainText)]
    [InlineData("readme", "text/plain; charset=utf-8", AttachmentTextKind.PlainText)]
    [InlineData("report.pdf", "application/octet-stream", AttachmentTextKind.Pdf)]
    [InlineData("scan", "application/pdf", AttachmentTextKind.Pdf)]
    [InlineData("budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", AttachmentTextKind.None)]
    [InlineData("archive.zip", "application/zip", AttachmentTextKind.None)]
    [InlineData("run.cmd", "text/plain", AttachmentTextKind.None)]
    public void OnlyTheAllowlistIsRead(string name, string type, AttachmentTextKind expected) =>
        Assert.Equal(expected, AttachmentText.KindOf(A(name, type)));

    [Fact]
    public async Task PlainTextIsReadOnlyUpToTheLimit()
    {
        var late = new string('a', AttachmentText.MaxTextBytes + 10) + " needle";
        using var big = new MemoryStream(Encoding.UTF8.GetBytes(late));
        var text = await AttachmentText.ReadPlainTextAsync(big, default);
        Assert.DoesNotContain("needle", text);
        Assert.True(text.Length <= AttachmentText.MaxChars);
        Assert.True(AttachmentText.Matches("Quarterly budget for Ünïcode team", SearchText.Terms("budget ünïcode")));
        Assert.False(AttachmentText.Matches("Quarterly budget", SearchText.Terms("budget invoice")));
    }

    internal static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var line in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(line, 12, new PdfPoint(40, 700), font);
        }

        return builder.Build();
    }

    [Fact]
    public void PdfTextIsReadPageByPageWithinTheLimits()
    {
        using var file = new MemoryStream(Pdf("Invoice 4711 for the quarterly budget", "Second page mentions lighthouse"));
        var text = PdfText.Read(file, maxPages: 50, maxChars: 10_000);
        Assert.Contains("quarterly budget", text);
        Assert.Contains("lighthouse", text);

        using var again = new MemoryStream(Pdf("Invoice 4711 for the quarterly budget", "Second page mentions lighthouse"));
        Assert.DoesNotContain("lighthouse", PdfText.Read(again, maxPages: 1, maxChars: 10_000));

        using var third = new MemoryStream(Pdf(new string('x', 40) + " tail"));
        Assert.True(PdfText.Read(third, maxPages: 50, maxChars: 20).Length <= 20);
    }

    [Fact]
    public void ABrokenPdfIsRejectedNotGuessed()
    {
        using var garbage = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not really a pdf"));
        Assert.ThrowsAny<Exception>(() => PdfText.Read(garbage, 50, 10_000));
    }

    [Fact]
    public async Task TheReaderStoresTextOnceAndTheTextGoesWithTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-attext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new AttachmentFiles(Path.Combine(root, "attachments"));
            await using var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
            await store.InitializeAsync(default);
            var batch = new PersonalBatch();
            StoredAttachment Add(long uid, string name, string type, byte[] content, bool downloaded = true)
            {
                var (relative, full) = files.Reserve("w", name);
                File.WriteAllBytes(full, content);
                var a = new StoredAttachment
                {
                    Id = $"w-{uid}", AccountId = "w", FolderId = "INBOX", Uid = uid, FileName = name, ContentType = type,
                    Size = content.Length, CacheRelativePath = downloaded ? relative : null, Downloaded = downloaded,
                };
                batch.Upserts.Add(new StoredMessage { AccountId = "w", FolderId = "INBOX", Uid = uid, Subject = name, HasAttachments = true });
                batch.Attachments.Add(a);
                return a;
            }

            Add(1, "invoice.pdf", "application/pdf", Pdf("Invoice 4711 quarterly budget"));
            Add(2, "notes.txt", "text/plain", Encoding.UTF8.GetBytes("Lighthouse keeper rota"));
            Add(3, "setup.exe", "application/octet-stream", [0x4d, 0x5a]);
            Add(4, "broken.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7 broken"));
            Add(5, "remote.pdf", "application/pdf", Pdf("never read"), downloaded: false);
            await store.CommitAsync(batch, default);
            Assert.Equal(4, await store.CountAttachmentsToReadAsync(default));

            var pdfCalls = 0;
            var reader = new AttachmentTextReader(store, files.Resolve, (path, ct) =>
            {
                pdfCalls++;
                try
                {
                    using var stream = File.OpenRead(path);
                    return Task.FromResult<string?>(PdfText.Read(stream, AttachmentText.MaxPdfPages, AttachmentText.MaxChars));
                }
                catch (Exception)
                {
                    return Task.FromResult<string?>(null); // as the child process reports a failure
                }
            });
            Assert.Equal(4, await reader.ReadPendingAsync(10, default));
            Assert.Equal(0, await reader.ReadPendingAsync(10, default)); // read once
            Assert.Equal(2, pdfCalls); // the two downloaded PDFs; the one only on the server is never read
            Assert.Equal(0, await store.CountAttachmentsToReadAsync(default));

            Assert.Equal([new MessageKey("w", "INBOX", 1)], await store.FindByAttachmentTextAsync(null, "budget 4711", default));
            Assert.Equal([new MessageKey("w", "INBOX", 2)], await store.FindByAttachmentTextAsync("w", "KEEPER lighthouse", default));
            Assert.Empty(await store.FindByAttachmentTextAsync(null, "never", default));
            Assert.Empty(await store.FindByAttachmentTextAsync("h", "budget", default));
            Assert.Empty(await store.FindByAttachmentTextAsync(null, "%", default));

            // Removing the file removes its text; it is read again only if downloaded again.
            await store.ForgetAttachmentFileAsync("w-1", default);
            Assert.Empty(await store.FindByAttachmentTextAsync(null, "budget", default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AnOlderDatabaseGainsTheAttachmentTextColumns()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-attext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "personal.db");
        try
        {
            await using (var store = new SqlitePersonalStore(path))
            {
                await store.InitializeAsync(default);
                var batch = new PersonalBatch();
                batch.Upserts.Add(new StoredMessage { AccountId = "w", FolderId = "INBOX", Uid = 1, Subject = "Kept" });
                batch.Attachments.Add(new StoredAttachment { Id = "a1", AccountId = "w", FolderId = "INBOX", Uid = 1, FileName = "kept.pdf", Downloaded = true, CacheRelativePath = "w/kept.pdf" });
                await store.CommitAsync(batch, default);
            }

            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                legacy.Open();
                using var command = legacy.CreateCommand();
                command.CommandText = "ALTER TABLE attachments DROP COLUMN text_content; ALTER TABLE attachments DROP COLUMN text_state; PRAGMA user_version = 2;";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            await using var reopened = new SqlitePersonalStore(path);
            await reopened.InitializeAsync(default);
            Assert.Equal("kept.pdf", Assert.Single(await reopened.ListAttachmentsAsync(new MessageKey("w", "INBOX", 1), default)).FileName);
            Assert.Equal(1, await reopened.CountAttachmentsToReadAsync(default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
