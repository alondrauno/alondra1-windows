using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Alondra1.Mail;

/// <summary>
/// Text of legacy binary Office attachments for search (Mail UI spec §8.3): Word 97–2003 (.doc), Excel 97–2003 (.xls,
/// BIFF8) and PowerPoint 97–2003 (.ppt). These are Compound File Binary containers ([MS-CFB]); only the documented text
/// structures are read ([MS-DOC] piece table, [MS-XLS] shared strings and cells, [MS-PPT] text atoms):
/// <list type="bullet">
/// <item>Nothing runs: macro storage, embedded objects and OLE links are never opened or followed.</item>
/// <item>Encrypted files are rejected (their text is not readable without the password).</item>
/// <item>Bounded: the whole file is at most the caller's size limit; every offset is checked; sector chains cannot loop;
/// record nesting and counts are capped; the text stops at the requested number of characters.</item>
/// </list>
/// Anything malformed throws, and the attachment stays searchable by name. The app runs this in a separate process with
/// a time limit.
/// </summary>
public static class LegacyOfficeText
{
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool IsCompoundFile(ReadOnlySpan<byte> start) => start.Length >= 8 && start[..8].SequenceEqual(Signature);

    public static string Read(byte[] file, int maxChars)
    {
        var cfb = new CompoundFile(file);
        if (cfb.Has("EncryptedPackage") || cfb.Has("EncryptionInfo"))
            throw new InvalidDataException("Encrypted document.");
        string text;
        if (cfb.Get("WordDocument") is { } word)
            text = Word(cfb, word, maxChars);
        else if (cfb.Get("Workbook") is { } workbook)
            text = Excel(workbook, maxChars);
        else if (cfb.Get("PowerPoint Document") is { } powerpoint)
            text = PowerPoint(powerpoint, maxChars);
        else
            throw new InvalidDataException("Not a supported Office document.");
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    // ------------------------------------------------------------------ Word ([MS-DOC] 2.4.1, retrieving text)

    private static string Word(CompoundFile cfb, byte[] doc, int maxChars)
    {
        if (Bytes.U16(doc, 0) != 0xA5EC)
            throw new InvalidDataException("Not a Word document.");
        var flags = Bytes.U16(doc, 0x0A);
        if ((flags & 0x0100) != 0)
            throw new InvalidDataException("Encrypted document.");
        var table = cfb.Get((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("No table stream.");

        // FIB: FibBase (32 bytes), csw + FibRgW, cslw + FibRgLw, cbRgFcLcb + FibRgFcLcb; fcClx/lcbClx is pair 33.
        var pos = 0x20;
        pos += 2 + (Bytes.U16(doc, pos) * 2);
        pos += 2 + (Bytes.U16(doc, pos) * 4);
        var pairs = Bytes.U16(doc, pos);
        pos += 2;
        if (pairs <= 33)
            throw new InvalidDataException("No piece table.");
        var fcClx = (int)Bytes.U32(doc, pos + (33 * 8));
        var lcbClx = (int)Bytes.U32(doc, pos + (33 * 8) + 4);
        Bytes.Check(table, fcClx, lcbClx);

        // Clx: Prc entries (0x01), then the Pcdt (0x02) holding the PlcPcd.
        var at = fcClx;
        var end = fcClx + lcbClx;
        while (at < end && table[at] == 0x01)
            at += 3 + Math.Max(0, (int)Bytes.I16(table, at + 1));
        if (at >= end || table[at] != 0x02)
            throw new InvalidDataException("No piece table.");
        var lcb = (int)Bytes.U32(table, at + 1);
        var plc = at + 5;
        Bytes.Check(table, plc, lcb);
        var pieces = (lcb - 4) / 12;
        if (pieces <= 0 || pieces > 100_000)
            throw new InvalidDataException("Bad piece table.");

        var text = new StringBuilder();
        var cp1252 = Windows1252();
        for (var i = 0; i < pieces && text.Length < maxChars; i++)
        {
            var cpStart = (int)Bytes.U32(table, plc + (i * 4));
            var cpEnd = (int)Bytes.U32(table, plc + ((i + 1) * 4));
            var chars = Math.Min(cpEnd - cpStart, maxChars - text.Length);
            if (chars <= 0)
                continue;
            var pcd = plc + ((pieces + 1) * 4) + (i * 8);
            var fc = Bytes.U32(table, pcd + 2);
            if ((fc & 0x40000000) != 0)
            {
                var offset = (int)((fc & 0x3FFFFFFF) / 2);
                Bytes.Check(doc, offset, chars);
                text.Append(cp1252.GetString(doc, offset, chars));
            }
            else
            {
                var offset = (int)(fc & 0x3FFFFFFF);
                Bytes.Check(doc, offset, chars * 2);
                text.Append(Encoding.Unicode.GetString(doc, offset, chars * 2));
            }
        }

        return CleanWordText(text.ToString());
    }

    /// <summary>Paragraph and cell marks become breaks; field codes (between 0x13 and 0x14) are dropped, their results kept.</summary>
    internal static string CleanWordText(string raw)
    {
        var text = new StringBuilder(raw.Length);
        var fieldDepth = 0;
        var inCode = new Stack<bool>();
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\u0013':
                    fieldDepth++;
                    inCode.Push(true);
                    continue;
                case '\u0014':
                    if (inCode.Count > 0)
                    {
                        inCode.Pop();
                        inCode.Push(false);
                    }

                    continue;
                case '\u0015':
                    if (fieldDepth > 0)
                    {
                        fieldDepth--;
                        inCode.Pop();
                    }

                    continue;
            }

            if (inCode.Count > 0 && inCode.Peek())
                continue;
            text.Append(c switch
            {
                '\r' or '\v' or '\u000C' => '\n',
                '\u0007' or '\t' => ' ',
                < ' ' => ' ',
                _ => c,
            });
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ Excel ([MS-XLS] BIFF8 records)

    private static string Excel(byte[] book, int maxChars)
    {
        var text = new StringBuilder();
        var at = 0;
        var records = 0;
        while (at + 4 <= book.Length && text.Length < maxChars && ++records < 2_000_000)
        {
            var type = Bytes.U16(book, at);
            var length = Bytes.U16(book, at + 2);
            var data = at + 4;
            Bytes.Check(book, data, length);
            at = data + length;
            switch (type)
            {
                case 0x0809 when records == 1 && length >= 2 && Bytes.U16(book, data) != 0x0600:
                    throw new InvalidDataException("Not a BIFF8 workbook.");
                case 0x002F: // FILEPASS
                    throw new InvalidDataException("Encrypted workbook.");
                case 0x0085: // BoundSheet8: sheet name (ShortXLUnicodeString at offset 6)
                    if (length > 8)
                        text.Append(ShortString(book, data + 6, data + length)).Append('\n');
                    break;
                case 0x00FC: // SST and its CONTINUE records
                {
                    var segments = new List<(int Start, int Length)> { (data, length) };
                    while (at + 4 <= book.Length && Bytes.U16(book, at) == 0x003C)
                    {
                        var continueLength = Bytes.U16(book, at + 2);
                        Bytes.Check(book, at + 4, continueLength);
                        segments.Add((at + 4, continueLength));
                        at += 4 + continueLength;
                    }

                    new SharedStrings(book, segments).ReadAll(text, maxChars);
                    break;
                }

                case 0x0204 when length > 8: // Label (BIFF8 rarely): XLUnicodeString at offset 6
                    text.Append(LongString(book, data + 6, data + length)).Append(' ');
                    break;
                case 0x0203 when length >= 14: // Number
                    text.Append(Number(BitConverter.Int64BitsToDouble((long)Bytes.U64(book, data + 6)))).Append(' ');
                    break;
                case 0x027E when length >= 10: // RK
                    text.Append(Number(Rk(Bytes.U32(book, data + 6)))).Append(' ');
                    break;
                case 0x00BD when length >= 12: // MulRk: (ixfe, rk) pairs
                    for (var p = data + 4; p + 6 <= data + length - 2; p += 6)
                        text.Append(Number(Rk(Bytes.U32(book, p + 2)))).Append(' ');
                    break;
            }
        }

        return text.ToString();
    }

    private static double Rk(uint rk)
    {
        var value = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)((ulong)(rk & 0xFFFFFFFC) << 32));
        return (rk & 0x01) != 0 ? value / 100 : value;
    }

    private static string Number(double value) =>
        double.IsFinite(value) ? value.ToString("0.##########", CultureInfo.InvariantCulture) : "";

    private static string ShortString(byte[] b, int at, int end)
    {
        var cch = Bytes.Byte(b, at);
        var high = (Bytes.Byte(b, at + 1) & 0x01) != 0;
        var bytes = cch * (high ? 2 : 1);
        Bytes.Check(b, at + 2, Math.Min(bytes, end - at - 2));
        return high ? Encoding.Unicode.GetString(b, at + 2, Math.Min(bytes, end - at - 2)) : Encoding.Latin1.GetString(b, at + 2, Math.Min(bytes, end - at - 2));
    }

    private static string LongString(byte[] b, int at, int end)
    {
        var cch = Bytes.U16(b, at);
        var high = (Bytes.Byte(b, at + 2) & 0x01) != 0;
        var bytes = Math.Min(cch * (high ? 2 : 1), end - at - 3);
        Bytes.Check(b, at + 3, bytes);
        return high ? Encoding.Unicode.GetString(b, at + 3, bytes) : Encoding.Latin1.GetString(b, at + 3, bytes);
    }

    /// <summary>
    /// The SST's strings across its CONTINUE records. A string's characters may continue in the next record, which then
    /// starts with a new flags byte (8- or 16-bit characters); rich-text runs and phonetic data are skipped.
    /// </summary>
    private sealed class SharedStrings(byte[] book, List<(int Start, int Length)> segments)
    {
        private int _segment;
        private int _pos;

        private bool AtSegmentEnd => _pos >= segments[_segment].Length;

        private byte ReadByte()
        {
            while (AtSegmentEnd)
                Next();
            return book[segments[_segment].Start + _pos++];
        }

        private void Next()
        {
            if (++_segment >= segments.Count)
                throw new InvalidDataException("Shared strings end early.");
            _pos = 0;
        }

        private ushort ReadU16() => (ushort)(ReadByte() | (ReadByte() << 8));
        private uint ReadU32() => (uint)(ReadU16() | (ReadU16() << 16));

        private void Skip(long count)
        {
            while (count > 0)
            {
                while (AtSegmentEnd)
                    Next();
                var step = (int)Math.Min(count, segments[_segment].Length - _pos);
                _pos += step;
                count -= step;
            }
        }

        public void ReadAll(StringBuilder text, int maxChars)
        {
            ReadU32(); // total
            var unique = ReadU32();
            for (var i = 0u; i < unique && text.Length < maxChars; i++)
            {
                var cch = ReadU16();
                var flags = ReadByte();
                var high = (flags & 0x01) != 0;
                var runs = (flags & 0x08) != 0 ? ReadU16() : 0;
                var ext = (flags & 0x04) != 0 ? ReadU32() : 0;
                for (var c = 0; c < cch; c++)
                {
                    if (AtSegmentEnd)
                    {
                        Next();
                        high = (ReadByte() & 0x01) != 0;
                    }

                    text.Append(high ? (char)ReadU16() : (char)ReadByte());
                }

                text.Append('\n');
                Skip((runs * 4L) + ext);
            }
        }
    }

    // ------------------------------------------------------------------ PowerPoint ([MS-PPT] records)

    private static string PowerPoint(byte[] stream, int maxChars)
    {
        var text = new StringBuilder();
        var records = 0;
        void Walk(int start, int end, int depth)
        {
            if (depth > 32)
                throw new InvalidDataException("Records nested too deeply.");
            var at = start;
            while (at + 8 <= end && text.Length < maxChars)
            {
                if (++records > 1_000_000)
                    throw new InvalidDataException("Too many records.");
                var version = Bytes.U16(stream, at) & 0x000F;
                var type = Bytes.U16(stream, at + 2);
                var length = (int)Math.Min(Bytes.U32(stream, at + 4), (uint)(end - at - 8));
                var data = at + 8;
                switch (type)
                {
                    case 0x2F14: // CryptSession10Container
                        throw new InvalidDataException("Encrypted presentation.");
                    case 0x03F8: // MainMaster: placeholder text such as "Click to edit Master title style"
                    case 0x0FC9: // HandoutContainer
                        break;
                    case 0x03F0 when IsNotesMaster(data, data + length): // the notes master: its placeholders only
                        break;
                    case 0x0FA0: // TextCharsAtom (UTF-16)
                        AppendText(Encoding.Unicode.GetString(stream, data, length & ~1));
                        break;
                    case 0x0FA8: // TextBytesAtom (8-bit)
                        AppendText(Encoding.Latin1.GetString(stream, data, length));
                        break;
                    default:
                        if (version == 0x0F)
                            Walk(data, data + length, depth + 1);
                        break;
                }

                at = data + length;
            }
        }

        // A NotesContainer whose NotesAtom (0x03F1) refers to no slide is the notes master: slide ids are below
        // 0x80000000, master ids from there up (PowerPoint writes 0x80000000; 0 is also treated as "no slide").
        bool IsNotesMaster(int start, int end) =>
            start + 12 <= end && Bytes.U16(stream, start + 2) == 0x03F1 && Bytes.U32(stream, start + 8) is 0 or >= 0x80000000;

        // "*" alone is the slide-number placeholder of a notes page, not text.
        void AppendText(string value)
        {
            if (value.Trim() != "*")
                text.Append(value).Append('\n');
        }

        Walk(0, stream.Length, 0);
        return text.ToString().Replace('\r', '\n').Replace('\v', '\n');
    }

    private static Encoding Windows1252()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    // ------------------------------------------------------------------ Compound File Binary ([MS-CFB])

    /// <summary>Reads named streams of a compound file held in memory. Chains are followed at most once per sector.</summary>
    private sealed class CompoundFile
    {
        private const uint EndOfChain = 0xFFFFFFFE;
        private readonly byte[] _file;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly uint _miniCutoff;
        private readonly uint[] _fat;
        private readonly uint[] _miniFat;
        private readonly byte[] _miniStream;
        private readonly List<(string Name, int Type, uint Start, long Size)> _entries = [];

        public CompoundFile(byte[] file)
        {
            _file = file;
            if (!IsCompoundFile(file))
                throw new InvalidDataException("Not a compound file.");
            var shift = Bytes.U16(file, 0x1E);
            var miniShift = Bytes.U16(file, 0x20);
            if (shift is not (9 or 12) || miniShift != 6)
                throw new InvalidDataException("Unsupported sector size.");
            _sectorSize = 1 << shift;
            _miniSectorSize = 1 << miniShift;
            _miniCutoff = Bytes.U32(file, 0x38);
            var sectorCount = Math.Max(0, (file.Length / _sectorSize) - 1);

            // DIFAT: 109 entries in the header, then chained DIFAT sectors.
            var fatSectors = new List<uint>();
            for (var i = 0; i < 109; i++)
                AddFatSector(Bytes.U32(file, 0x4C + (i * 4)));
            var difat = Bytes.U32(file, 0x44);
            for (var hops = 0; difat < EndOfChain && hops <= sectorCount; hops++)
            {
                var at = Offset(difat);
                for (var i = 0; i < (_sectorSize / 4) - 1; i++)
                    AddFatSector(Bytes.U32(file, at + (i * 4)));
                difat = Bytes.U32(file, at + _sectorSize - 4);
            }

            _fat = new uint[fatSectors.Count * (_sectorSize / 4)];
            for (var s = 0; s < fatSectors.Count; s++)
            {
                var at = Offset(fatSectors[s]);
                for (var i = 0; i < _sectorSize / 4; i++)
                    _fat[(s * (_sectorSize / 4)) + i] = Bytes.U32(file, at + (i * 4));
            }

            var directory = Chain(Bytes.U32(file, 0x30), long.MaxValue);
            for (var at = 0; at + 128 <= directory.Length; at += 128)
            {
                var nameLength = Math.Clamp((int)Bytes.U16(directory, at + 0x40), 2, 64);
                var name = Encoding.Unicode.GetString(directory, at, nameLength - 2);
                var type = directory[at + 0x42];
                var start = Bytes.U32(directory, at + 0x74);
                var size = (long)(Bytes.U64(directory, at + 0x78) & (shift == 9 ? 0xFFFFFFFFUL : ulong.MaxValue));
                _entries.Add((name, type, start, size));
            }

            if (_entries.Count == 0 || _entries[0].Type != 5)
                throw new InvalidDataException("No root entry.");
            _miniFat = ToUInts(Chain(Bytes.U32(file, 0x3C), long.MaxValue));
            _miniStream = Chain(_entries[0].Start, _entries[0].Size);
            return;

            void AddFatSector(uint sector)
            {
                if (sector < EndOfChain && fatSectors.Count <= sectorCount)
                    fatSectors.Add(sector);
            }
        }

        public bool Has(string name) => _entries.Any(e => e.Type == 2 && e.Name == name);

        public byte[]? Get(string name)
        {
            var entry = _entries.FirstOrDefault(e => e.Type == 2 && e.Name == name);
            if (entry.Name is null)
                return null;
            if (entry.Size > _file.Length)
                throw new InvalidDataException("Stream larger than the file.");
            return entry.Size < _miniCutoff ? MiniChain(entry.Start, entry.Size) : Chain(entry.Start, entry.Size);
        }

        private int Offset(uint sector)
        {
            var offset = ((long)sector + 1) * _sectorSize;
            if (offset + _sectorSize > _file.Length)
                throw new InvalidDataException("Sector outside the file.");
            return (int)offset;
        }

        private byte[] Chain(uint start, long size)
        {
            using var output = new MemoryStream();
            var seen = new HashSet<uint>();
            for (var sector = start; sector < EndOfChain && output.Length < size; sector = sector < _fat.Length ? _fat[sector] : throw new InvalidDataException("Bad chain."))
            {
                if (!seen.Add(sector))
                    throw new InvalidDataException("Chain loops.");
                output.Write(_file, Offset(sector), _sectorSize);
            }

            var bytes = output.ToArray();
            return size < bytes.Length ? bytes[..(int)size] : bytes;
        }

        private byte[] MiniChain(uint start, long size)
        {
            using var output = new MemoryStream();
            var seen = new HashSet<uint>();
            for (var sector = start; sector < EndOfChain && output.Length < size; sector = sector < _miniFat.Length ? _miniFat[sector] : throw new InvalidDataException("Bad mini chain."))
            {
                if (!seen.Add(sector))
                    throw new InvalidDataException("Mini chain loops.");
                var at = (long)sector * _miniSectorSize;
                if (at + _miniSectorSize > _miniStream.Length)
                    throw new InvalidDataException("Mini sector outside the mini stream.");
                output.Write(_miniStream, (int)at, _miniSectorSize);
            }

            var bytes = output.ToArray();
            return size < bytes.Length ? bytes[..(int)size] : bytes;
        }

        private static uint[] ToUInts(byte[] bytes)
        {
            var result = new uint[bytes.Length / 4];
            for (var i = 0; i < result.Length; i++)
                result[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
            return result;
        }
    }

    /// <summary>Little-endian reads that throw on any out-of-range offset instead of reading past the data.</summary>
    private static class Bytes
    {
        public static void Check(byte[] b, int at, int count)
        {
            if (at < 0 || count < 0 || (long)at + count > b.Length)
                throw new InvalidDataException("Offset outside the data.");
        }

        public static byte Byte(byte[] b, int at)
        {
            Check(b, at, 1);
            return b[at];
        }

        public static ushort U16(byte[] b, int at)
        {
            Check(b, at, 2);
            return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
        }

        public static short I16(byte[] b, int at)
        {
            Check(b, at, 2);
            return BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(at));
        }

        public static uint U32(byte[] b, int at)
        {
            Check(b, at, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        }

        public static ulong U64(byte[] b, int at)
        {
            Check(b, at, 8);
            return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
        }
    }
}
