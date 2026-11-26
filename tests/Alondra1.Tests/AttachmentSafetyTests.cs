using System.Text;
using Alondra1.Core.Tasks;
using Alondra1.Persistence;

namespace Alondra1.Tests;

/// <summary>Dev-only spec WIN-01 (review S1): cloud files are never run, only passive files with matching bytes open.</summary>
public sealed class AttachmentSafetyTests : IDisposable
{
    private static readonly byte[] Pdf = "%PDF-1.7\n%test\n"u8.ToArray();
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Exe = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0];
    private static readonly byte[] Zip = [(byte)'P', (byte)'K', 3, 4, 20, 0, 0, 0];
    private static readonly byte[] Text = "plain words\r\nsecond line"u8.ToArray();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alondra1-safety-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("script.ps1")]
    [InlineData("shortcut.lnk")]
    [InlineData("package.msi")]
    [InlineData("code.js")]
    [InlineData("code.vbs")]
    [InlineData("saver.scr")]
    [InlineData("app.hta")]
    [InlineData("report.pdf.exe")]
    [InlineData("macro.docm")]
    [InlineData("old.doc")]
    [InlineData("noextension")]
    [InlineData("archive.zip")]
    public void Programs_scripts_and_unknown_types_are_save_only(string name)
    {
        foreach (var bytes in new[] { Exe, Text, Pdf, Zip })
            Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor(name, bytes));
    }

    [Fact]
    public void Passive_files_open_only_when_their_bytes_match()
    {
        Assert.Equal(AttachmentOpenPolicy.Open, AttachmentSafety.PolicyFor("Ticket.PDF", Pdf));
        Assert.Equal(AttachmentOpenPolicy.Open, AttachmentSafety.PolicyFor("photo.png", Png));
        Assert.Equal(AttachmentOpenPolicy.Open, AttachmentSafety.PolicyFor("notes.txt", Text));
        Assert.Equal(AttachmentOpenPolicy.Open, AttachmentSafety.PolicyFor("plan.xlsx", Zip));
        // A program renamed to a passive type, or a picture that is really something else.
        Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor("photo.jpg", Exe));
        Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor("invoice.pdf", Exe));
        Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor("readme.txt", Exe));
        Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor("readme.txt", Zip));
        Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor("photo.png", Pdf));
        Assert.Equal(AttachmentOpenPolicy.SaveOnly, AttachmentSafety.PolicyFor("empty.pdf", []));
        // Double extension where the last one is passive and the bytes agree: it is that document.
        Assert.Equal(AttachmentOpenPolicy.Open, AttachmentSafety.PolicyFor("setup.exe.pdf", Pdf));
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil.pdf", "evil.pdf")]
    [InlineData("C:/Users/me/report.pdf", "report.pdf")]
    [InlineData("a<b>c:d|e?f*g.txt", "abcdefg.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("...", "file")]
    [InlineData("trailing. ", "trailing")]
    public void Names_are_clean(string name, string expected) => Assert.Equal(expected, AttachmentSafety.CleanName(name));

    [Fact]
    public void Cloud_copies_are_staged_with_the_mark_of_the_web_and_never_the_stored_file()
    {
        var store = new TaskFileStore(Path.Combine(_directory, "task-files"));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Pdf));
        Assert.True(store.SaveDownloaded(sha, Pdf));
        var attachment = new TaskAttachment { FileName = "..\\Trip ticket.PDF", Sha256 = sha, Size = Pdf.Length };
        var source = store.Resolve(attachment)!;
        Assert.True(store.IsFromCloud(attachment, source));

        var staged = store.StageForOpen(attachment, source, fromCloud: true);
        Assert.NotNull(staged);
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(staged));
        Assert.Equal("Trip ticket.pdf", Path.GetFileName(staged));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(store.Root, "open")), Path.GetFullPath(staged), StringComparison.OrdinalIgnoreCase);
        Assert.True(MarkOfTheWeb.IsMarked(staged!));
        Assert.Equal(Pdf, File.ReadAllBytes(staged!));

        // Saved copies are marked too; the staging folder is not mistaken for orphaned task files.
        var saved = Path.Combine(_directory, "saved.pdf");
        Assert.True(TaskFileStore.SaveCopy(source, saved, fromCloud: true));
        Assert.True(MarkOfTheWeb.IsMarked(saved));
        store.RemoveOrphans([new TodoTask { Attachments = [attachment] }]);
        Assert.True(File.Exists(staged));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Files_added_on_this_pc_are_not_marked()
    {
        var original = Path.Combine(_directory, "mine.txt");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(original, "my own words", Encoding.UTF8);
        var store = new TaskFileStore(Path.Combine(_directory, "task-files"));
        var attachment = store.Import("t1", original, DateTimeOffset.UtcNow);
        var source = store.Resolve(attachment)!;
        Assert.False(store.IsFromCloud(attachment, source));
        var staged = store.StageForOpen(attachment, source, fromCloud: false)!;
        Assert.False(MarkOfTheWeb.IsMarked(staged));
    }
}
