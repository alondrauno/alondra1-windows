using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Alondra1.Core.Notes;

/// <summary>The bytes were not a supported picture or broke a limit; the message is shown to the user.</summary>
public sealed class NoteAssetException(string message) : Exception(message);

/// <summary>
/// Pictures used in notes (Notes spec §6.3): checked from their bytes (PNG, JPEG, GIF, WebP; the file name is never
/// trusted), limited in size, stored once per content hash below the notes-assets folder, and referenced from note
/// documents by that hash. Never embedded in the document.
/// </summary>
public sealed class NoteAssets(string directory, INoteStore store, Func<DateTimeOffset> now)
{
    public const long DefaultMaxBytes = 20L << 20;
    public static readonly string[] Types = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public string Directory { get; } = directory;
    public long MaxBytes { get; init; } = DefaultMaxBytes;
    /// <summary>Total picture bytes one note may reference (checked when pictures are added).</summary>
    public long MaxBytesPerNote { get; init; } = 100L << 20;

    public string PathOf(string sha256) => Path.Combine(Directory, sha256[..2], sha256);

    public bool HasFile(string sha256) => File.Exists(PathOf(sha256));

    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The picture type from the first bytes, or null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[..8].SequenceEqual(PngSignature))
            return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
            return "image/jpeg";
        if (d.Length >= 6 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    /// <summary>Pixel size read from the header (no decoding), or nulls.</summary>
    public static (int? Width, int? Height) Dimensions(string mime, ReadOnlySpan<byte> d)
    {
        try
        {
            switch (mime)
            {
                case "image/png" when d.Length >= 24:
                    return (BinaryPrimitives.ReadInt32BigEndian(d[16..20]), BinaryPrimitives.ReadInt32BigEndian(d[20..24]));
                case "image/gif" when d.Length >= 10:
                    return (BinaryPrimitives.ReadUInt16LittleEndian(d[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(d[8..10]));
                case "image/webp" when d.Length >= 30:
                    var chunk = System.Text.Encoding.ASCII.GetString(d[12..16]);
                    if (chunk == "VP8X")
                        return (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
                    if (chunk == "VP8 ")
                        return (BinaryPrimitives.ReadUInt16LittleEndian(d[26..28]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(d[28..30]) & 0x3fff);
                    if (chunk == "VP8L")
                    {
                        var b = BinaryPrimitives.ReadUInt32LittleEndian(d[21..25]);
                        return ((int)(b & 0x3fff) + 1, (int)((b >> 14) & 0x3fff) + 1);
                    }

                    break;
                case "image/jpeg":
                    for (var i = 2; i + 9 < d.Length;)
                    {
                        if (d[i] != 0xFF)
                        {
                            i++;
                            continue;
                        }

                        var marker = d[i + 1];
                        if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
                            return (BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..(i + 9)]), BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..(i + 7)]));
                        if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                        {
                            i += 2;
                            continue;
                        }

                        i += 2 + BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..(i + 4)]);
                    }

                    break;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Truncated header: size unknown.
        }

        return (null, null);
    }

    /// <summary>
    /// Stores a picture (or returns the stored one with the same content). Throws <see cref="NoteAssetException"/>
    /// with a user-facing reason when the bytes are not a supported picture or are too large.
    /// </summary>
    public async Task<NoteAsset> AddAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data.LongLength > MaxBytes)
            throw new NoteAssetException($"The picture is larger than {MaxBytes >> 20} MB.");
        var mime = Sniff(data) ?? throw new NoteAssetException("Only PNG, JPEG, GIF and WebP pictures can be added.");
        var sha = Hash(data);
        var existing = await store.GetAssetAsync(sha, cancellationToken).ConfigureAwait(false);
        var path = PathOf(sha);
        if (existing is not null && File.Exists(path))
            return existing;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        var (w, h) = Dimensions(mime, data);
        var asset = existing ?? new NoteAsset(sha, mime, data.LongLength, w, h, now(), Uploaded: false);
        await store.SaveAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        return asset;
    }

    /// <summary>Bytes of a stored picture, or null when the file is missing (not downloaded yet).</summary>
    public async Task<(NoteAsset Asset, byte[] Data)?> ReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (sha256.Length != 64 || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return null;
        var asset = await store.GetAssetAsync(sha256, cancellationToken).ConfigureAwait(false);
        var path = PathOf(sha256);
        if (asset is null || !File.Exists(path))
            return null;
        return (asset, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A picture downloaded from the cloud: kept only when its bytes match the hash and are a picture.</summary>
    public async Task<bool> AddDownloadedAsync(string sha256, byte[] data, CancellationToken cancellationToken = default)
    {
        if (Hash(data) != sha256 || Sniff(data) is null)
            return false;
        var asset = await AddAsync(data, cancellationToken).ConfigureAwait(false);
        await store.MarkAssetUploadedAsync(asset.Sha256, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
