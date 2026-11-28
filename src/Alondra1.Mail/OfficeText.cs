using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Alondra1.Mail;

/// <summary>
/// Text of Office attachments for search (Mail UI spec §8.3): Word, Excel and PowerPoint (.docx/.xlsx/.pptx and the
/// macro-enabled variants) and OpenDocument (.odt/.ods/.odp). These are ZIP archives of XML; only the text parts are
/// read, with the .NET ZIP and XML readers:
/// <list type="bullet">
/// <item>Nothing runs: macros (vbaProject.bin), embedded objects and media are never opened; no file inside is unpacked
/// to disk; no DTD, entity or external reference is resolved (XML DTD processing is prohibited, no resolver).</item>
/// <item>Bounded against ZIP bombs: at most <see cref="MaxEntries"/> parts, each text part at most
/// <see cref="MaxPartBytes"/> unpacked and at most <see cref="MaxRatio"/> times its packed size, all parts together at
/// most <see cref="MaxTotalBytes"/>, and at most the requested characters of text.</item>
/// </list>
/// The format is recognised from the parts present, not from the file name. Anything else (legacy binary .doc/.xls/.ppt,
/// password-protected files) is rejected: those attachments stay searchable by name. The app runs this in a separate
/// process with a time limit, as for PDFs.
/// </summary>
public static class OfficeText
{
    public const int MaxEntries = 5000;
    public const long MaxPartBytes = 20_000_000;
    public const long MaxTotalBytes = 60_000_000;
    public const int MaxRatio = 200;
    public const int MaxParts = 300;

    public static string Read(Stream stream, int maxChars)
    {
        // Legacy binary formats (.doc, .xls, .ppt) are compound files, recognised by their signature.
        if (stream.CanSeek)
        {
            if (stream.Length > MaxTotalBytes)
                throw new InvalidDataException("File too large.");
            var head = new byte[8];
            var got = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Seek(-got, SeekOrigin.Current);
            if (LegacyOfficeText.IsCompoundFile(head.AsSpan(0, got)))
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return LegacyOfficeText.Read(copy.ToArray(), maxChars);
            }
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("Too many parts.");
        var byName = zip.Entries.GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var (parts, textElements) = Plan(byName);
        if (parts.Count == 0)
            throw new InvalidDataException("Not a supported Office document.");

        var text = new StringBuilder();
        var budget = MaxTotalBytes;
        foreach (var entry in parts.Take(MaxParts))
        {
            if (text.Length >= maxChars)
                break;
            if (entry.Length > MaxPartBytes || (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio) || entry.Length > budget)
                throw new InvalidDataException("A part is too large.");
            budget -= entry.Length;
            using var raw = entry.Open();
            using var limited = new LimitedStream(raw, Math.Min(MaxPartBytes, budget + entry.Length));
            AppendText(limited, textElements, text, maxChars);
            text.Append('\n');
        }

        return text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
    }

    /// <summary>The text parts of the document in reading order, and which elements hold text (null: all text).</summary>
    private static (List<ZipArchiveEntry> Parts, HashSet<string>? Elements) Plan(Dictionary<string, ZipArchiveEntry> entries)
    {
        List<ZipArchiveEntry> Matching(string prefix, string suffix) => entries.Values
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => Number(e.FullName)).ThenBy(e => e.FullName, StringComparer.Ordinal).ToList();

        // Word: body, then footnotes, endnotes and comments. Text runs are <w:t>.
        if (entries.TryGetValue("word/document.xml", out var document))
        {
            var parts = new List<ZipArchiveEntry> { document };
            foreach (var name in new[] { "word/footnotes.xml", "word/endnotes.xml", "word/comments.xml" })
                if (entries.TryGetValue(name, out var extra))
                    parts.Add(extra);
            return (parts, ["t"]);
        }

        // Excel: shared strings (most cell text), then each sheet's inline strings and values. <t> and <v>.
        if (entries.ContainsKey("xl/workbook.xml"))
        {
            var parts = new List<ZipArchiveEntry>();
            if (entries.TryGetValue("xl/sharedStrings.xml", out var shared))
                parts.Add(shared);
            parts.AddRange(Matching("xl/worksheets/sheet", ".xml"));
            return (parts, ["t", "v"]);
        }

        // PowerPoint: slides in order, then notes. Text runs are <a:t>.
        if (entries.ContainsKey("ppt/presentation.xml"))
        {
            var parts = Matching("ppt/slides/slide", ".xml");
            parts.AddRange(Matching("ppt/notesSlides/notesSlide", ".xml"));
            return (parts, ["t"]);
        }

        // OpenDocument: all text of content.xml.
        if (entries.ContainsKey("mimetype") && entries.TryGetValue("content.xml", out var content))
            return ([content], null);

        return ([], null);
    }

    private static int Number(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    private static void AppendText(Stream xml, HashSet<string>? elements, StringBuilder text, int maxChars)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaxPartBytes,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(xml, settings);
        var depthOfText = -1;
        // Excel: a cell of type "s" holds an index into the shared strings (already read), not text.
        var sharedStringCell = false;
        while (reader.Read() && text.Length < maxChars)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.LocalName == "c")
                        sharedStringCell = reader.GetAttribute("t") == "s";
                    if (reader.LocalName == "v" && sharedStringCell)
                        break;
                    if (elements is null || elements.Contains(reader.LocalName))
                        depthOfText = reader.IsEmptyElement ? -1 : reader.Depth;
                    // Paragraph-like elements end a line: Word w:p, PowerPoint a:p, Excel rows, OpenDocument text:p/h.
                    else if (reader.LocalName is "p" or "row" or "h" or "tab" or "br")
                        text.Append(' ');
                    break;
                case XmlNodeType.EndElement:
                    if (reader.Depth == depthOfText)
                    {
                        text.Append(' ');
                        depthOfText = -1;
                    }

                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    if (elements is null || depthOfText >= 0)
                    {
                        text.Append(reader.Value);
                        if (elements is null)
                            text.Append(' ');
                    }

                    break;
            }
        }
    }

    /// <summary>A read-only stream that fails once more than <c>limit</c> bytes are read (a part larger than it claimed).</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > limit)
                throw new InvalidDataException("A part is larger than allowed.");
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
