using Alondra1.Core.Config;

namespace Alondra1.Core.Mail;

/// <summary>A standard mailbox role; <see cref="Other"/> is a custom (or nested) folder addressed by its id.</summary>
public enum FolderRole
{
    Inbox,
    Sent,
    Drafts,
    Archive,
    Trash,
    Junk,
    Other,
}

/// <summary>
/// What the message list shows (Mail UI spec §3): a unified standard folder across the included accounts (AccountId
/// null), a standard folder of one account, or one custom folder of one account. Identity is the stable account id
/// and the server's folder id, never a display name or a position in a list.
/// </summary>
public sealed record MailScope(string? AccountId, FolderRole Role, string? FolderId = null)
{
    public static readonly MailScope UnifiedInbox = new(null, FolderRole.Inbox);

    public bool IsUnified => AccountId is null;

    /// <summary>A stable text form for the settings file: "unified/Inbox", "account/{id}/Sent", "account/{id}/folder/{folder id}".</summary>
    public string Key => IsUnified ? $"unified/{Role}"
        : Role == FolderRole.Other ? $"account/{AccountId}/folder/{FolderId}"
        : $"account/{AccountId}/{Role}";

    public static MailScope? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var parts = key.Split('/', 4);
        if (parts.Length == 2 && parts[0] == "unified" && Enum.TryParse<FolderRole>(parts[1], out var role) && role != FolderRole.Other)
            return new MailScope(null, role);
        if (parts.Length == 3 && parts[0] == "account" && parts[1].Length > 0 && Enum.TryParse(parts[2], out role) && role != FolderRole.Other)
            return new MailScope(parts[1], role);
        if (parts.Length == 4 && parts[0] == "account" && parts[1].Length > 0 && parts[2] == "folder" && parts[3].Length > 0)
            return new MailScope(parts[1], FolderRole.Other, parts[3]);
        return null;
    }

    public static string RoleLabel(FolderRole role) => role switch
    {
        FolderRole.Junk => "Junk",
        _ => role.ToString(),
    };
}

/// <summary>One row of the account and folder tree.</summary>
public sealed class MailNavNode
{
    public required MailScope? Scope { get; init; }
    public required string Label { get; init; }
    /// <summary>Second line or tooltip: the account address, or the folder's full path.</summary>
    public string Detail { get; init; } = "";
    public int Depth { get; init; }
    public bool IsHeading { get; init; }
    public bool IsAccount { get; init; }
    public string? AccountId { get; init; }
    public FolderRole Role { get; init; } = FolderRole.Other;
    /// <summary>Unread messages on this PC; null when unknown (never synced here), which is not the same as zero.</summary>
    public int? Unread { get; init; }
    /// <summary>The folder is on the server but not kept on this PC (not in the account's synced folders).</summary>
    public bool NotSynced { get; init; }
    public List<MailNavNode> Children { get; } = [];

    public string UnreadText => Unread is > 0 ? Unread.Value.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

    public string AccessName
    {
        get
        {
            var parts = new List<string> { Label };
            if (IsAccount && Detail.Length > 0)
                parts.Add(Detail);
            if (Unread is > 0)
                parts.Add(Unread == 1 ? "1 unread" : $"{Unread} unread");
            else if (Unread is null && !IsHeading && !IsAccount)
                parts.Add("unread count not known yet");
            if (NotSynced)
                parts.Add("not synced on this PC");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>Per-folder counts of the messages kept on this PC.</summary>
public readonly record struct FolderCount(string AccountId, string FolderId, int Total, int Unread);

/// <summary>Builds the Mail sidebar: All accounts (unified standard folders), then every account with its folders.</summary>
public static class MailNavigation
{
    public static readonly IReadOnlyList<FolderRole> StandardRoles =
        [FolderRole.Inbox, FolderRole.Sent, FolderRole.Drafts, FolderRole.Archive, FolderRole.Trash, FolderRole.Junk];

    /// <summary>
    /// Unread basis (spec §3.1): the unread messages kept on this PC in that folder. A unified folder adds up the accounts
    /// whose folder is known; it is null when no account's count is known. Accounts are always listed, with or without mail.
    /// </summary>
    public static IReadOnlyList<MailNavNode> Build(IReadOnlyList<AccountConfig> accounts, IReadOnlyCollection<FolderCount> counts)
    {
        var lookup = counts.GroupBy(c => (c.AccountId, c.FolderId)).ToDictionary(g => g.Key, g => g.First());
        int? UnreadOf(AccountConfig account, string folderId) =>
            lookup.TryGetValue((account.Id, folderId), out var c) ? c.Unread : SyncedHere(account, folderId) ? 0 : null;

        var nodes = new List<MailNavNode>();
        var unified = new MailNavNode { Scope = null, Label = "All accounts", IsHeading = true };
        foreach (var role in StandardRoles)
        {
            var present = accounts.Where(a => SpecialFolders.TryResolve(a, role) is not null).ToList();
            if (role == FolderRole.Junk && present.Count == 0)
                continue; // shown only where some account has one
            int? unread = null;
            foreach (var account in present)
            {
                if (UnreadOf(account, SpecialFolders.TryResolve(account, role)!) is { } n)
                    unread = (unread ?? 0) + n;
            }

            unified.Children.Add(new MailNavNode
            {
                Scope = new MailScope(null, role),
                Label = MailScope.RoleLabel(role),
                Depth = 1,
                Role = role,
                Unread = accounts.Count == 0 ? 0 : unread,
                Detail = present.Count == accounts.Count ? "" : $"{present.Count} of {accounts.Count} accounts have this folder",
            });
        }

        nodes.Add(unified);
        var duplicates = accounts.GroupBy(a => a.DisplayName.Trim(), StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var account in accounts)
        {
            var name = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Address : account.DisplayName.Trim();
            var node = new MailNavNode
            {
                Scope = new MailScope(account.Id, FolderRole.Inbox),
                Label = name,
                Detail = account.Address,
                IsAccount = true,
                AccountId = account.Id,
                Unread = SpecialFolders.TryResolve(account, FolderRole.Inbox) is { } inbox ? UnreadOf(account, inbox) : null,
            };
            if (duplicates.Contains(name))
                node = Rename(node, $"{name} ({account.Address})");
            var special = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in StandardRoles)
            {
                if (SpecialFolders.TryResolve(account, role) is not { } id)
                    continue;
                special.Add(id);
                node.Children.Add(new MailNavNode
                {
                    Scope = new MailScope(account.Id, role),
                    Label = MailScope.RoleLabel(role),
                    Detail = id,
                    Depth = 1,
                    AccountId = account.Id,
                    Role = role,
                    Unread = UnreadOf(account, id),
                    NotSynced = !SyncedHere(account, id),
                });
            }

            AddCustomFolders(node, account, special, UnreadOf);
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>All rows in display order (for a flat, virtualized list with indentation).</summary>
    public static IEnumerable<MailNavNode> Flatten(IEnumerable<MailNavNode> nodes, Func<MailNavNode, bool>? expanded = null)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children.Count > 0 && (expanded?.Invoke(node) ?? true))
            {
                foreach (var child in Flatten(node.Children, expanded))
                    yield return child;
            }
        }
    }

    /// <summary>Whether the account keeps this folder on this PC (its synced folders; an empty list means all).</summary>
    public static bool SyncedHere(AccountConfig account, string folderId)
    {
        var selected = account.Sync.SelectedFolders;
        if (selected.Count == 0)
            return true;
        if (selected.Contains(folderId, StringComparer.OrdinalIgnoreCase))
            return true;
        var known = account.Folders.Known.FirstOrDefault(f => f.Id == folderId);
        if (known is not null && selected.Contains(known.Name, StringComparer.OrdinalIgnoreCase))
            return true;
        // Selected folders are usually stored by display role ("Inbox", "Sent").
        foreach (var role in StandardRoles)
        {
            if (SpecialFolders.TryResolve(account, role) == folderId && selected.Contains(MailScope.RoleLabel(role), StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AddCustomFolders(MailNavNode accountNode, AccountConfig account, HashSet<string> special, Func<AccountConfig, string, int?> unreadOf)
    {
        var custom = account.Folders.Known.Where(f => !special.Contains(f.Id) && f.SpecialUse is null or "").OrderBy(f => f.Id, StringComparer.CurrentCultureIgnoreCase).ToList();
        var byPath = new Dictionary<string, MailNavNode>(StringComparer.Ordinal);
        foreach (var folder in custom)
        {
            var separator = string.IsNullOrEmpty(folder.Delimiter) ? null : folder.Delimiter;
            var segments = separator is null ? [folder.Id] : folder.Id.Split(separator);
            // Folders under the inbox (INBOX.Projects) belong under the account, not under a hidden INBOX row.
            var inbox = SpecialFolders.TryResolve(account, FolderRole.Inbox);
            var start = inbox is not null && separator is not null && folder.Id.StartsWith(inbox + separator, StringComparison.Ordinal) ? inbox.Split(separator).Length : 0;
            var parent = accountNode;
            for (var depth = start; depth < segments.Length; depth++)
            {
                var path = string.Join(separator ?? "", segments.Take(depth + 1));
                if (!byPath.TryGetValue(path, out var node))
                {
                    var real = depth == segments.Length - 1 ? folder : custom.FirstOrDefault(f => f.Id == path);
                    node = new MailNavNode
                    {
                        // A parent that is only a path segment (no selectable folder on the server) has no scope.
                        Scope = real is null ? null : new MailScope(account.Id, FolderRole.Other, real.Id),
                        Label = segments[depth],
                        Detail = path,
                        Depth = depth - start + 1,
                        AccountId = account.Id,
                        Unread = real is null ? null : unreadOf(account, real.Id),
                        NotSynced = real is not null && !SyncedHere(account, real.Id),
                    };
                    byPath[path] = node;
                    parent.Children.Add(node);
                }

                parent = node;
            }
        }
    }

    private static MailNavNode Rename(MailNavNode node, string label) => new()
    {
        Scope = node.Scope,
        Label = label,
        Detail = node.Detail,
        Depth = node.Depth,
        IsAccount = node.IsAccount,
        AccountId = node.AccountId,
        Unread = node.Unread,
    };
}
