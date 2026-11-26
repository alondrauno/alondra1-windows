namespace Alondra1.Core.Tasks;

/// <summary>How a task attachment may be handed to Windows (Dev-only spec WIN-01, review S1).</summary>
public enum AttachmentOpenPolicy
{
    /// <summary>A passive document or picture whose bytes match its extension: a copy may be opened with its default app.</summary>
    Open,
    /// <summary>Anything else (programs, scripts, installers, shortcuts, unknown or mismatched types): Save as… only.</summary>
    SaveOnly,
}

/// <summary>
/// Decides whether an attachment may be opened directly. A file opens only when its extension is on a short allowlist
/// of passive types AND its first bytes agree with that extension, so a renamed program ("photo.jpg" that is really an
/// executable) or a double extension ("report.pdf.exe") is never started. The stored file is never shell-executed:
/// callers open a copy named <see cref="OpenName"/>.
/// </summary>
public static class AttachmentSafety
{
    private enum Kind { Pdf, Png, Jpeg, Gif, Webp, Bmp, Text, OfficeZip }

    private static readonly Dictionary<string, Kind> Passive = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Kind.Pdf,
        [".png"] = Kind.Png,
        [".jpg"] = Kind.Jpeg,
        [".jpeg"] = Kind.Jpeg,
        [".gif"] = Kind.Gif,
        [".webp"] = Kind.Webp,
        [".bmp"] = Kind.Bmp,
        [".txt"] = Kind.Text,
        [".csv"] = Kind.Text,
        [".md"] = Kind.Text,
        [".log"] = Kind.Text,
        // Office Open XML without macros (.docm/.xlsm/.pptm and the legacy binary formats are save-only).
        [".docx"] = Kind.OfficeZip,
        [".xlsx"] = Kind.OfficeZip,
        [".pptx"] = Kind.OfficeZip,
    };

    /// <summary>The policy for a file named <paramref name="fileName"/> whose first bytes are <paramref name="head"/> (16 or more when the file is that long).</summary>
    public static AttachmentOpenPolicy PolicyFor(string fileName, ReadOnlySpan<byte> head)
    {
        var extension = Path.GetExtension(CleanName(fileName));
        if (!Passive.TryGetValue(extension, out var kind))
            return AttachmentOpenPolicy.SaveOnly;
        return Matches(kind, head) ? AttachmentOpenPolicy.Open : AttachmentOpenPolicy.SaveOnly;
    }

    /// <summary>A file name safe to create in a folder of Alondra1's: no folders, no reserved or invalid characters, at most 120 characters, extension kept.</summary>
    public static string CleanName(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        name = new string(name.Where(ch => !invalid.Contains(ch) && !char.IsControl(ch)).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.Trim('.').Length == 0)
            name = "file";
        var stem = Path.GetFileNameWithoutExtension(name);
        if (IsReservedDeviceName(stem))
            name = "_" + name;
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            ext = ext.Length <= 10 ? ext : "";
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }

    /// <summary>The name for the copy handed to Windows: the clean name, with the extension lower-cased (it decided the policy).</summary>
    public static string OpenName(string fileName)
    {
        var clean = CleanName(fileName);
        return Path.GetFileNameWithoutExtension(clean) + Path.GetExtension(clean).ToLowerInvariant();
    }

    private static bool IsReservedDeviceName(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47];
    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> ZipMagic => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> ElfMagic => [0x7F, 0x45, 0x4C, 0x46];

    private static bool Matches(Kind kind, ReadOnlySpan<byte> d) => kind switch
    {
        Kind.Pdf => d.StartsWith("%PDF-"u8),
        Kind.Png => d.StartsWith(PngMagic),
        Kind.Jpeg => d.StartsWith(JpegMagic),
        Kind.Gif => d.StartsWith("GIF8"u8),
        Kind.Webp => d.Length >= 12 && d.StartsWith("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8),
        Kind.Bmp => d.StartsWith("BM"u8),
        Kind.OfficeZip => d.StartsWith(ZipMagic),
        // Text: no NUL bytes and not a program or archive in disguise.
        Kind.Text => !d.Contains((byte)0) && !d.StartsWith("MZ"u8) && !d.StartsWith("PK"u8) && !d.StartsWith(ElfMagic),
        _ => false,
    };
}
