using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Alondra1.Core.Cloud;

namespace Alondra1.Core.Tags;

/// <summary>
/// A reusable personal label for mail, notes, tasks and calendar events (FEATURES_EXTENSION Part B §2). Same shape as
/// the cloud's tags-v1 schema. Assignments are separate <see cref="TagLink"/> records naming the tag by id.
/// </summary>
public sealed class Tag
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>A theme token (see <see cref="TagRules.Colors"/>) or null for the neutral default.</summary>
    public string? Color { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string ModifiedUtc { get; set; } = "";

    public Tag Clone() => (Tag)MemberwiseClone();
}

/// <summary>One tag on one item (cloud tag-links-v1). Unassigning sets Removed; the id is derived (<see cref="TagRules.LinkId"/>).</summary>
public sealed class TagLink
{
    public string Id { get; set; } = "";
    public string TagId { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string ObjectRef { get; set; } = "";
    public bool Removed { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string ModifiedUtc { get; set; } = "";

    [JsonIgnore]
    public bool Live => !Removed;

    public TagLink Clone() => (TagLink)MemberwiseClone();
}

/// <summary>Where a tagged mail message is on this PC (local only, never synced): lets the Tags view open it again.</summary>
public sealed record MailTagTarget(string Ref, string AccountId, string FolderId, long Uid, string Subject, DateTimeOffset ReceivedUtc);

public sealed class TagSnapshot
{
    public List<Tag> Tags { get; } = [];
    public List<TagLink> Links { get; } = [];
    public List<MailTagTarget> MailTargets { get; } = [];
}

/// <summary>
/// One local change to tags. Upserts and deletes are queued for Cloud Sync when it records; <see cref="DropLinks"/> are
/// removed on this PC only (the cloud already removed them: links of a deleted tag or item).
/// </summary>
public sealed class TagChangeSet
{
    public List<Tag> UpsertTags { get; } = [];
    public List<string> DeleteTags { get; } = [];
    public List<TagLink> UpsertLinks { get; } = [];
    public List<string> DeleteLinks { get; } = [];
    public List<string> DropLinks { get; } = [];
    public List<string> DropTags { get; } = [];
    public List<MailTagTarget> MailTargets { get; } = [];

    public bool IsEmpty => UpsertTags.Count == 0 && DeleteTags.Count == 0 && UpsertLinks.Count == 0 && DeleteLinks.Count == 0
        && DropLinks.Count == 0 && DropTags.Count == 0 && MailTargets.Count == 0;
}

/// <summary>Persistence of tags (implemented by SqliteTaskStore in todo.db, next to the Cloud Sync outbox).</summary>
public interface ITagStore
{
    Task<TagSnapshot> LoadTagsAsync(CancellationToken cancellationToken);
    /// <summary>Writes the changes in one transaction; queued for Cloud Sync when recording (see <see cref="TagRules.IsSynced"/>).</summary>
    Task ApplyTagsAsync(TagChangeSet changes, CancellationToken cancellationToken);
    /// <summary>Writes changes from the cloud and their versions in one transaction, without queueing them.</summary>
    Task ApplyRemoteTagsAsync(TagChangeSet changes, IReadOnlyCollection<SyncVersion> versions, CancellationToken cancellationToken);
}

public sealed class TagException(string message) : Exception(message);

/// <summary>The rules shared with the cloud (tags-v1, tag-links-v1) and the other apps.</summary>
public static class TagRules
{
    public const int MaxNameLength = 64;
    public const int MaxTags = 500;
    public const int MaxTagsPerItem = 50;

    public const string Mail = "mail";
    public const string Note = "note";
    public const string Task = "task";
    public const string Event = "event";

    public static readonly IReadOnlyList<string> Colors = ["gray", "red", "orange", "yellow", "green", "teal", "blue", "purple", "pink"];

    /// <summary>
    /// Whether a link goes to the cloud. Calendar events in this app come from mail accounts and are not in the cloud
    /// (the cloud would refuse a link to them), so their tags stay on this PC.
    /// </summary>
    public static bool IsSynced(TagLink link) => link.ObjectType != Event;

    /// <summary>The display name as stored: trimmed and NFC. Throws <see cref="TagException"/> with a plain reason if invalid.</summary>
    public static string CleanName(string? name)
    {
        var clean = (name ?? "").Trim().Normalize(NormalizationForm.FormC);
        if (clean.Length == 0)
            throw new TagException("Enter a name for the tag.");
        if (clean.EnumerateRunes().Count() > MaxNameLength)
            throw new TagException($"Tag names can be at most {MaxNameLength} characters.");
        if (clean.Any(char.IsControl))
            throw new TagException("Tag names can't contain control characters.");
        return clean;
    }

    /// <summary>
    /// The uniqueness key: NFC, case-folded (ToLowerInvariant plus the full folds the cloud applies, such as ß → ss).
    /// The cloud's key is the authority; a name it refuses is merged into the existing tag after sync.
    /// </summary>
    public static string NameKey(string name)
    {
        var lower = name.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return lower.Replace("ß", "ss", StringComparison.Ordinal).Replace("ẞ", "ss", StringComparison.Ordinal)
            .Replace("ς", "σ", StringComparison.Ordinal).Normalize(NormalizationForm.FormC);
    }

    /// <summary>The derived link id: 'tl_' + the first 40 characters of base64url(SHA-256(tag U+001F type U+001F ref)).</summary>
    public static string LinkId(string tagId, string objectType, string objectRef)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(tagId + "\u001f" + objectType + "\u001f" + objectRef));
        return "tl_" + Base64Url(sum)[..40];
    }

    /// <summary>
    /// The stable reference of a mail message for tags: a hash of the account and the Message-ID, so a tag stays with
    /// the message when it moves between folders, and the cloud never sees message ids or addresses. Without a
    /// Message-ID the account, folder and uid are used (such a tag does not follow a move to another folder).
    /// </summary>
    public static string MailRef(string accountId, string? messageId, string folderId, long uid)
    {
        var id = (messageId ?? "").Trim().Trim('<', '>').Trim();
        var source = id.Length > 0 ? "m\u001f" + accountId + "\u001f" + id : "u\u001f" + accountId + "\u001f" + folderId + "\u001f" + uid.ToString(CultureInfo.InvariantCulture);
        return (id.Length > 0 ? "m_" : "u_") + Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..43];
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
