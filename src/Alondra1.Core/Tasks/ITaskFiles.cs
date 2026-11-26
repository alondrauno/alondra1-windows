namespace Alondra1.Core.Tasks;

/// <summary>
/// This PC's copies of files attached to tasks, for Cloud Sync: where an attachment's bytes are, and keeping bytes
/// downloaded from the cloud (verified against their SHA-256). Implemented by Alondra1.Persistence.TaskFileStore.
/// </summary>
public interface ITaskFiles
{
    /// <summary>The full path of the attachment's bytes on this PC, or null.</summary>
    string? Resolve(TaskAttachment attachment);

    /// <summary>Keeps downloaded bytes when they match <paramref name="sha256"/>; false otherwise.</summary>
    bool SaveDownloaded(string sha256, byte[] data);
}
