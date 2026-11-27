using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Alondra1.Core.Mail;

namespace Alondra1.App.ViewModels;

/// <summary>
/// One visible row of the Mail sidebar (a flattened <see cref="MailNavNode"/> tree): All accounts and its unified
/// folders, then each account with its folders. Rows with children expand and collapse; collapsed rows are remembered.
/// </summary>
public sealed partial class MailNavRow : ObservableObject
{
    public MailNavRow(MailNavNode node, bool expanded)
    {
        Node = node;
        _isExpanded = expanded;
    }

    public MailNavNode Node { get; }
    public MailScope? Scope => Node.Scope;
    public bool CanSelect => Node.Scope is not null && !Node.IsHeading;
    public bool HasChildren => Node.Children.Count > 0;
    public string Label => Node.Label;
    public string Detail => Node.Detail;
    public bool IsAccount => Node.IsAccount;
    public bool IsHeading => Node.IsHeading;
    public bool IsFolder => !Node.IsAccount && !Node.IsHeading;
    public bool HasUnread => Node.Unread is > 0;
    public string UnreadText => Node.UnreadText;
    public bool NotSynced => Node.NotSynced;
    public Thickness Indent => new(Node.Depth * 16, 0, 0, 0);

    /// <summary>Remembered expand state key: the scope, or the folder path for a path-only parent.</summary>
    public string Key => Node.IsHeading ? "unified" : Node.Scope?.Key ?? $"account/{Node.AccountId}/path/{Node.Detail}";

    /// <summary>Two letters for the account marker (supplemental: the name and address are always shown too).</summary>
    public string Initials
    {
        get
        {
            var words = Node.Label.Split([' ', '.', '@', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            var letters = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : words.Length == 1 ? words[0][..Math.Min(2, words[0].Length)] : "?";
            return letters.ToUpperInvariant();
        }
    }

    public string Icon => Node.IsHeading ? "Inbox" : Node.Role switch
    {
        FolderRole.Inbox => "Inbox",
        FolderRole.Sent => "Sent",
        FolderRole.Drafts => "Drafts",
        FolderRole.Archive => "Archive",
        FolderRole.Trash => "Delete",
        FolderRole.Junk => "Alert",
        _ => "Folder",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessName), nameof(ChevronGlyph))]
    private bool _isExpanded;

    /// <summary>The row of the scope the list shows.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string AccessName => HasChildren ? $"{Node.AccessName}, {(IsExpanded ? "expanded" : "collapsed")}" : Node.AccessName;

    public string ToolTip => Node.IsAccount ? $"{Node.Label}\n{Node.Detail}"
        : Node.NotSynced ? $"{Node.Detail}\nNot kept on this PC. Choose the folders to keep in the account's settings."
        : string.IsNullOrEmpty(Node.Detail) ? Node.Label : $"{Node.Label}\n{Node.Detail}";
}
