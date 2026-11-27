using Alondra1.Core.Config;

namespace Alondra1.Core.Mail;

/// <summary>
/// Maps the folder names the app shows (Inbox, Sent, Drafts, Archive, Trash) to each account's real folder ids. IMAP
/// servers name and nest these differently ("INBOX", "INBOX.INBOX.Sent", "[Gmail]/Sent Mail"), so the ids come from
/// the server's special-use flags seen during sync and are kept in <see cref="AccountConfig.Folders"/>.
/// </summary>
public static class SpecialFolders
{
    private static string Describe(KnownFolder f) => $"{f.Id}{f.Name}{f.SpecialUse}{f.Delimiter}";

    public static readonly IReadOnlyList<string> Displayed = ["Inbox", "Sent", "Drafts", "Archive", "Trash"];

    /// <summary>The folder id to read for a displayed folder of this account.</summary>
    public static string Resolve(AccountConfig account, string display)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
            return display; // POP3 downloads into "Inbox"; sent copies and other folders exist only locally, under their display names
        var folders = account.Folders;
        return display switch
        {
            "Inbox" => folders.Inbox ?? "INBOX", // RFC 3501: the inbox is always reachable as INBOX
            "Sent" => folders.Sent ?? "Sent",
            "Drafts" => folders.Drafts ?? "Drafts",
            "Archive" => folders.Archive ?? "Archive",
            "Trash" => folders.Trash ?? "Trash",
            "Junk" => folders.Junk ?? "Junk",
            _ => display,
        };
    }

    /// <summary>
    /// The folder id of a standard role, or null when this account has no such folder. Before the first sync (no folder
    /// list yet) the usual names are assumed, as <see cref="Resolve"/> does; afterwards only folders the server listed
    /// count, so a missing Archive is never taken for another folder (Mail UI spec §3.1).
    /// </summary>
    public static string? TryResolve(AccountConfig account, FolderRole role)
    {
        if (role == FolderRole.Other)
            return null;
        if (account.Incoming.Protocol == MailProtocol.Pop3)
            return role is FolderRole.Junk ? null : role.ToString();
        var map = account.Folders;
        var mapped = role switch
        {
            FolderRole.Inbox => map.Inbox,
            FolderRole.Sent => map.Sent,
            FolderRole.Drafts => map.Drafts,
            FolderRole.Archive => map.Archive,
            FolderRole.Trash => map.Trash,
            _ => map.Junk,
        };
        if (map.Known.Count == 0)
            return mapped ?? (role == FolderRole.Inbox ? "INBOX" : role == FolderRole.Junk ? null : role.ToString());
        if (mapped is not null && map.Known.Any(f => f.Id == mapped))
            return mapped;
        return role == FolderRole.Inbox ? map.Known.FirstOrDefault(f => f.SpecialUse == "inbox")?.Id ?? "INBOX" : null;
    }

    /// <summary>The standard role of a folder id of this account, or <see cref="FolderRole.Other"/>.</summary>
    public static FolderRole RoleOf(AccountConfig account, string folderId)
    {
        foreach (var role in new[] { FolderRole.Inbox, FolderRole.Sent, FolderRole.Drafts, FolderRole.Archive, FolderRole.Trash, FolderRole.Junk })
        {
            if (string.Equals(TryResolve(account, role), folderId, StringComparison.Ordinal))
                return role;
        }

        return FolderRole.Other;
    }

    /// <summary>A folder's name for display: the role name of a standard folder, else the server's leaf name.</summary>
    public static string LabelOf(AccountConfig account, string folderId)
    {
        var role = RoleOf(account, folderId);
        if (role != FolderRole.Other)
            return MailScope.RoleLabel(role);
        if (account.Folders.Known.FirstOrDefault(f => f.Id == folderId) is { } known && !string.IsNullOrWhiteSpace(known.Name))
            return known.Name;
        return folderId;
    }

    /// <summary>
    /// Records the real ids from a sync's folder list: a special-use flag wins, then a folder whose display name matches.
    /// Returns true when the account changed (the caller saves the configuration).
    /// </summary>
    public static bool Learn(AccountConfig account, IReadOnlyList<RemoteFolder> folders)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3 || folders.Count == 0)
            return false;
        var map = account.Folders;
        var changed = false;
        void Set(string? found, Func<string?> get, Action<string> set)
        {
            if (found is not null && !string.Equals(get(), found, StringComparison.Ordinal))
            {
                set(found);
                changed = true;
            }
        }

        string? Find(string special, string display)
        {
            return folders.FirstOrDefault(folder => string.Equals(folder.SpecialUse, special, StringComparison.OrdinalIgnoreCase))?.Id
                ?? folders.FirstOrDefault(folder => string.Equals(folder.Name, display, StringComparison.OrdinalIgnoreCase))?.Id
                ?? folders.FirstOrDefault(folder => string.Equals(folder.Id, display, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        Set(Find("inbox", "Inbox"), () => map.Inbox, value => map.Inbox = value);
        Set(Find("sent", "Sent"), () => map.Sent, value => map.Sent = value);
        Set(Find("drafts", "Drafts"), () => map.Drafts, value => map.Drafts = value);
        Set(Find("archive", "Archive"), () => map.Archive, value => map.Archive = value);
        Set(Find("trash", "Trash"), () => map.Trash, value => map.Trash = value);
        Set(Find("junk", "Junk") ?? Find("junk", "Spam"), () => map.Junk, value => map.Junk = value);
        var known = folders.Select(f => new KnownFolder { Id = f.Id, Name = f.Name, SpecialUse = f.SpecialUse, Delimiter = f.Delimiter }).ToList();
        if (!known.Select(Describe).SequenceEqual(map.Known.Select(Describe)))
        {
            map.Known = known;
            changed = true;
        }

        return changed;
    }
}
