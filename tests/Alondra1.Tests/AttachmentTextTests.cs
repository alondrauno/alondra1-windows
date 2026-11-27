using System.Text;
using Alondra1.Core.Mail;
using Alondra1.Core.Search;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §8.3 / T19: attachment text search is limited to downloaded plain-text files of bounded size.</summary>
public class AttachmentTextTests
{
    private static StoredAttachment A(string name, string type = "application/octet-stream", long size = 100, bool downloaded = true) =>
        new() { FileName = name, ContentType = type, Size = size, Downloaded = downloaded, CacheRelativePath = downloaded ? "x/y.bin" : null };

    [Theory]
    [InlineData("notes.txt", "application/octet-stream", true)]
    [InlineData("data.CSV", "application/octet-stream", true)]
    [InlineData("readme", "text/plain; charset=utf-8", true)]
    [InlineData("report.pdf", "application/pdf", false)]
    [InlineData("archive.zip", "application/zip", false)]
    [InlineData("run.cmd", "text/plain", false)]
    public void OnlyAllowlistedPlainTextIsSearched(string name, string type, bool expected) =>
        Assert.Equal(expected, AttachmentText.IsSearchable(A(name, type)));

    [Fact]
    public void FilesNotOnThisPcOrTooLargeAreNotRead()
    {
        Assert.False(AttachmentText.IsSearchable(A("notes.txt", downloaded: false)));
        Assert.False(AttachmentText.IsSearchable(A("notes.txt", size: AttachmentText.MaxBytes + 1)));
    }

    [Fact]
    public async Task EveryTermMustAppearAndOnlyTheBoundedStartIsRead()
    {
        using var small = new MemoryStream(Encoding.UTF8.GetBytes("Quarterly budget for Ünïcode team"));
        Assert.True(await AttachmentText.MatchesAsync(small, SearchText.Terms("budget ünïcode"), default));
        using var other = new MemoryStream(Encoding.UTF8.GetBytes("Quarterly budget"));
        Assert.False(await AttachmentText.MatchesAsync(other, SearchText.Terms("budget invoice"), default));
        var late = new string('a', AttachmentText.MaxBytes + 10) + " needle";
        using var big = new MemoryStream(Encoding.UTF8.GetBytes(late));
        Assert.False(await AttachmentText.MatchesAsync(big, SearchText.Terms("needle"), default));
    }
}
