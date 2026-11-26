using Alondra1.Core.Tasks;

namespace Alondra1.Persistence;

/// <summary>
/// Copies files attached to tasks into Alondra1's data folder (task-files\&lt;task id&gt;\&lt;random name&gt;) so a task never
/// depends on the original file staying where it was. Uses the same naming as mail attachment files. Each file also
/// gets its SHA-256 (its id in cloud storage); files downloaded from the cloud are kept as task-files\cloud\&lt;sha256&gt;.
/// </summary>
public sealed class TaskFileStore : ITaskFiles
{
    public const long MaxFileBytes = 512L * 1024 * 1024;
    private readonly AttachmentFiles _files;

    public TaskFileStore(string root) => _files = new AttachmentFiles(root);

    public string Root => _files.Root;

    public TaskAttachment Import(string taskId, string sourcePath, DateTimeOffset nowUtc)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException("The file no longer exists.", sourcePath);
        if (info.Length > MaxFileBytes)
            throw new IOException($"The file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        var (relative, full) = _files.Reserve(SafeSegment(taskId), info.Name);
        File.Copy(info.FullName, full, overwrite: false);
        var (sha, head) = Hash(full);
        return new TaskAttachment
        {
            FileName = info.Name, RelativePath = relative, Size = info.Length, AddedUtc = nowUtc, Sha256 = sha,
            MediaType = Sniff(head) ?? MediaTypeFor(info.Extension),
        };
    }

    /// <summary>The SHA-256 (lowercase hex) of a file and its first bytes, read once.</summary>
    public static (string Sha256, byte[] Head) Hash(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[16];
        var read = stream.Read(head, 0, head.Length);
        stream.Position = 0;
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        return (sha, head[..read]);
    }

    /// <summary>Pictures and PDF from their bytes, as the server recognises them; null otherwise.</summary>
    public static string? Sniff(byte[] d) => d switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
        [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', ..] => "application/pdf",
        _ when d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P' => "image/webp",
        _ => null,
    };

    private static string MediaTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    /// <summary>Where a file downloaded from the cloud is kept.</summary>
    public string CloudPath(string sha256) => Path.Combine(_files.Root, "cloud", sha256[..2], sha256);

    public bool SaveDownloaded(string sha256, byte[] data)
    {
        if (!TaskAttachment.IsHash(sha256) || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) != sha256)
            return false;
        var target = CloudPath(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, target, overwrite: true);
        return true;
    }

    /// <summary>Returns the full path for a stored reference, or null when the reference points outside the folder or is missing.</summary>
    public string? Resolve(TaskAttachment attachment)
    {
        if (TodoTransfer.IsSafeRelativePath(attachment.RelativePath))
        {
            var root = Path.GetFullPath(_files.Root);
            var full = Path.GetFullPath(Path.Combine(root, attachment.RelativePath));
            if (full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                return full;
        }

        // Added on another device and downloaded here.
        return TaskAttachment.IsHash(attachment.Sha256) && File.Exists(CloudPath(attachment.Sha256!)) ? CloudPath(attachment.Sha256!) : null;
    }

    /// <summary>Deletes stored files that no task refers to any more. Run once at startup, not on every change, so undo can restore attachments.</summary>
    public int RemoveOrphans(IEnumerable<TodoTask> tasks)
    {
        if (!Directory.Exists(_files.Root))
            return 0;
        var root = Path.GetFullPath(_files.Root);
        var attachments = tasks.SelectMany(task => task.Attachments).ToList();
        var referenced = attachments
            .Where(item => TodoTransfer.IsSafeRelativePath(item.RelativePath))
            .Select(item => Path.GetFullPath(Path.Combine(root, item.RelativePath)))
            .Concat(attachments.Where(item => TaskAttachment.IsHash(item.Sha256)).Select(item => Path.GetFullPath(CloudPath(item.Sha256!))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (referenced.Contains(Path.GetFullPath(file)))
                continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }

        return removed;
    }

    private static string SafeSegment(string value)
    {
        var clean = new string(value.Where(char.IsAsciiLetterOrDigit).ToArray());
        return clean.Length == 0 ? "task" : clean;
    }
}
