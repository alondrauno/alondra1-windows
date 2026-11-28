using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Alondra1.App.Tasks;
using Alondra1.Core.Notes;
using Microsoft.Win32;

namespace Alondra1.App.Notes;

/// <summary>
/// The Notes workspace: views and folders on the left, the note list in the middle, the editor on the right. Below
/// 900 px it shows one pane at a time (list, then the note full width with a Back button).
/// </summary>
public partial class NotesView : UserControl
{
    private const double CompactWidth = 900;
    private bool _compactEditing;
    private bool _filling;
    private Point _dragStart;

    public NotesView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Model is not { } model || model.Board is null)
                return;
            model.AttachEditor(Editor);
            model.PickImageRequested += PickPictures;
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(NotesViewModel.SelectedNavKey))
                    SyncNavSelection();
                if (e.PropertyName is nameof(NotesViewModel.ShowFolders) or nameof(NotesViewModel.ShowList) or nameof(NotesViewModel.HasNote))
                    ApplyLayout();
            };
            model.Items.CollectionChanged += (_, _) =>
            {
                EmptyText.Visibility = model.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SyncNoteSelection();
                OpenFirstOnce(model);
            };
            model.NoteCreated += _ => { SyncNoteSelection(); if (ActualWidth < CompactWidth) { _compactEditing = true; ApplyLayout(); } };
            EmptyText.Visibility = model.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncNavSelection();
            ApplyLayout();
            OpenFirstOnce(model);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && Model is { } model)
                _ = model.FlushAsync(); // leaving the workspace saves the open note
            else if (IsVisible)
                _ = Editor.ApplyThemeAsync();
        };
    }

    private NotesViewModel? Model => DataContext as NotesViewModel;
    private bool _openedFirst;

    /// <summary>On a wide window, the most recent note opens once, so the editor is not an empty page at first.</summary>
    private void OpenFirstOnce(NotesViewModel model)
    {
        if (_openedFirst || model.SelectedId is not null || model.Items.FirstOrDefault() is not { } first || ActualWidth < CompactWidth)
            return;
        _openedFirst = true;
        _ = Dispatcher.InvokeAsync(() => model.OpenAsync(first.Id));
    }

    public FrameworkElement? EditorSurface => Editor.Surface;

    public Task<System.Windows.Media.Imaging.BitmapSource?> CaptureEditorAsync() => Editor.CaptureAsync();
    private Window Owner => Window.GetWindow(this);

    private void NoteTags_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { SelectedId: { } id, Tags: { } tags })
            _ = Alondra1.App.Tags.TagUi.PickAsync(Window.GetWindow(this), tags, Alondra1.Core.Tags.TagRules.Note, [id], "Tags for this note");
    }

    private List<string> SelectedIds() => NoteList.SelectedItems.OfType<NoteItem>().Select(i => i.Id).ToList();

    // ---------------------------------------------------------------- layout

    private void View_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        if (Model is not { } model)
            return;
        var compact = ActualWidth > 0 && ActualWidth < CompactWidth;
        if (!compact)
            _compactEditing = false;
        var showFolders = compact ? !_compactEditing && model.ShowFolders && ActualWidth >= 640 : model.ShowFolders;
        var showList = compact ? !_compactEditing : model.ShowList;
        FoldersColumn.Width = showFolders ? new GridLength(210) : new GridLength(0);
        FoldersPane.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
        ListColumn.Width = showList ? new GridLength(compact && !showFolders ? 1 : 300, compact ? GridUnitType.Star : GridUnitType.Pixel) : new GridLength(0);
        ListPane.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        EditorPane.Visibility = compact && !_compactEditing ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = compact && _compactEditing ? Visibility.Visible : Visibility.Collapsed;
        ListToggle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FoldersToggle_Click(object sender, RoutedEventArgs e) => Model?.TogglePanes(folders: true);

    private void ListToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        // Focus mode: hide both lists; pressing again brings them back.
        var hide = model.ShowList || model.ShowFolders;
        if (model.ShowList == hide)
            model.TogglePanes(folders: false);
        if (model.ShowFolders == hide)
            model.TogglePanes(folders: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _compactEditing = false;
        _ = Model?.FlushAsync();
        ApplyLayout();
    }

    // ---------------------------------------------------------------- selection

    private void SyncNavSelection()
    {
        if (Model is not { } model)
            return;
        _filling = true;
        NavList.SelectedItem = model.Sidebar.FirstOrDefault(i => i.Key == model.SelectedNavKey);
        _filling = false;
    }

    private void SyncNoteSelection()
    {
        if (Model is not { } model || model.SelectedId is null)
            return;
        _filling = true;
        var item = model.Items.FirstOrDefault(i => i.Id == model.SelectedId);
        if (item is not null && !NoteList.SelectedItems.Contains(item))
        {
            NoteList.SelectedItem = item;
            NoteList.ScrollIntoView(item);
        }

        _filling = false;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && NavList.SelectedItem is NoteNavItem item)
            Model?.SelectNav(item);
    }

    private async void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Model is not { } model || NoteList.SelectedItems.Count != 1 || NoteList.SelectedItem is not NoteItem item)
            return;
        await model.OpenAsync(item.Id);
        if (ActualWidth < CompactWidth)
        {
            _compactEditing = true;
            ApplyLayout();
        }
    }

    /// <summary>Keys while the lists have focus (the editor handles its own keys).</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (Model is not { } model)
            return false;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            _ = model.NewNoteAsync();
            return true;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            _ = model.FlushAsync();
            return true;
        }

        return false;
    }

    private void NoteList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Model is { } model && SelectedIds() is { Count: > 0 } ids)
        {
            _ = DeleteAsync(ids, forever: model.IsTrashView || Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && NoteList.SelectedItem is NoteItem)
        {
            Editor.FocusEditor();
            e.Handled = true;
        }
    }

    private async Task DeleteAsync(IReadOnlyCollection<string> ids, bool forever)
    {
        if (forever && Dialogs.Alert(Owner, ids.Count == 1 ? "Delete this note permanently? This cannot be undone." : $"Delete {ids.Count} notes permanently? This cannot be undone.",
                "Delete permanently", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await Run(() => Model!.DeleteAsync(ids, forever));
    }

    private async Task Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is NoteStorageException or NoteAssetException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Alert(Owner, ex.Message, "Notes");
        }
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => _ = Model?.NewNoteAsync();

    private void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && Dialogs.Alert(Owner, "Delete every note in Trash permanently? This cannot be undone.", "Empty trash", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _ = Run(model.EmptyTrashAsync);
    }

    // ---------------------------------------------------------------- context menus

    private static MenuItem Item(string header, string icon, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Icon = new Controls.Alondra1Icon { Kind = icon, IconSize = 16 } };
        item.Click += (_, _) => action();
        return item;
    }

    private void NoteList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model || model.Board is not { } board)
            return;
        if ((e.OriginalSource as DependencyObject)?.FindParent<ListBoxItem>() is { DataContext: NoteItem clicked } && !NoteList.SelectedItems.Contains(clicked))
            NoteList.SelectedItem = clicked;
        var ids = SelectedIds();
        if (ids.Count == 0)
            return;
        var first = board.Find(ids[0]);
        var menu = NoteList.ContextMenu!;
        menu.Items.Clear();
        if (model.IsTrashView)
        {
            menu.Items.Add(Item("Restore", "Undo", () => _ = Run(() => model.RestoreAsync(ids))));
            menu.Items.Add(Item("Delete permanently", "Delete", () => _ = DeleteAsync(ids, forever: true), "Del"));
        }
        else
        {
            menu.Items.Add(first?.Favorite == true ? Item("Remove from favorites", "Priority", () => _ = Run(() => model.FavoriteAsync(ids, false)))
                : Item("Add to favorites", "PriorityFilled", () => _ = Run(() => model.FavoriteAsync(ids, true))));
            menu.Items.Add(first?.Pinned == true ? Item("Unpin", "Promote", () => _ = Run(() => model.PinAsync(ids, false)))
                : Item("Pin to top", "Promote", () => _ = Run(() => model.PinAsync(ids, true))));
            if (ids.Count == 1)
                menu.Items.Add(Item("Duplicate", "Copy", () => _ = Run(() => model.DuplicateAsync(ids[0]))));
            if (model.Tags is { } tags)
                menu.Items.Add(Item("Tags…", "Tag", () => _ = Alondra1.App.Tags.TagUi.PickAsync(Window.GetWindow(this), tags, Alondra1.Core.Tags.TagRules.Note, ids, ids.Count == 1 ? "Tags for this note" : $"Tags for {ids.Count} notes")));
            var move = new MenuItem { Header = "Move to", Icon = new Controls.Alondra1Icon { Kind = "Move", IconSize = 16 } };
            move.Items.Add(Item("No folder", "Notes", () => _ = Run(() => model.MoveAsync(ids, null))));
            foreach (var f in board.Folders)
                move.Items.Add(Item(f.Name, "Folder", () => _ = Run(() => model.MoveAsync(ids, f.Id))));
            menu.Items.Add(move);
            menu.Items.Add(first?.ArchivedUtc is not null ? Item("Unarchive", "Archive", () => _ = Run(() => model.ArchiveAsync(ids, false)))
                : Item("Archive", "Archive", () => _ = Run(() => model.ArchiveAsync(ids, true))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move to Trash", "Delete", () => _ = DeleteAsync(ids, forever: false), "Del"));
        }

        menu.PlacementTarget = NoteList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void NavList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model || (e.OriginalSource as DependencyObject)?.FindParent<ListBoxItem>() is not { DataContext: NoteNavItem nav })
            return;
        var menu = NavList.ContextMenu!;
        menu.Items.Clear();
        if (nav.FolderId is { } id)
        {
            menu.Items.Add(Item("Rename folder", "Edit", () =>
            {
                if (TodoDialogs.Prompt(Owner, "Rename folder", "Name", nav.Label) is { } name)
                    _ = Run(() => model.RenameFolderAsync(id, name));
            }));
            menu.Items.Add(Item("New folder inside", "Add", () => NewFolder(id)));
            if (model.Board!.FindFolder(id)?.ParentId is not null)
                menu.Items.Add(Item("Move to top level", "Move", () => _ = Run(() => model.MoveFolderAsync(id, null))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete folder", "Delete", () =>
            {
                if (Dialogs.Alert(Owner, $"Delete the folder \"{nav.Label}\"? Its notes are kept and move to All notes.", "Delete folder", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                    _ = Run(() => model.DeleteFolderAsync(id));
            }));
        }
        else if (nav.View == NoteView.Trash)
        {
            menu.Items.Add(Item("Empty trash", "Delete", () => EmptyTrash_Click(this, new RoutedEventArgs())));
        }
        else
        {
            return;
        }

        menu.PlacementTarget = NavList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => NewFolder(null);

    private void NewFolder(string? parentId)
    {
        if (Model is { } model && TodoDialogs.Prompt(Owner, "New folder", "Name", "") is { } name)
            _ = Run(() => model.AddFolderAsync(name, parentId));
    }

    // ---------------------------------------------------------------- drag notes onto folders and views

    private void NoteList_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void NoteList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedIds() is not { Count: > 0 } ids)
            return;
        var delta = _dragStart - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        DragDrop.DoDragDrop(NoteList, new DataObject("alondra1/notes", ids), DragDropEffects.Move);
    }

    private static NoteNavItem? DropTarget(DragEventArgs e) => (e.OriginalSource as DependencyObject)?.FindParent<ListBoxItem>()?.DataContext as NoteNavItem;

    private void NavList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("alondra1/notes") && DropTarget(e) is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void NavList_Drop(object sender, DragEventArgs e)
    {
        if (Model is not { } model || e.Data.GetData("alondra1/notes") is not List<string> ids || DropTarget(e) is not { } target)
            return;
        _ = Run(() => target.View switch
        {
            NoteView.Folder => model.MoveAsync(ids, target.FolderId),
            NoteView.Favorites => model.FavoriteAsync(ids, true),
            NoteView.Archive => model.ArchiveAsync(ids, true),
            NoteView.Trash => model.DeleteAsync(ids, forever: false),
            _ => model.MoveAsync(ids, null),
        });
    }

    // ---------------------------------------------------------------- note menu, links, pictures

    private void NoteMore_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.SelectedId is not { } id)
            return;
        var menu = NoteMoreButton.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Item("Export as HTML…", "Download", () => Export("html")));
        menu.Items.Add(Item("Export as Markdown…", "Download", () => Export("markdown")));
        menu.Items.Add(Item("Import an HTML file as a note…", "Upload", ImportHtml));
        menu.Items.Add(Item("Import notes from mail accounts", "Upload", () => _ = ImportAccountNotesAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Duplicate", "Copy", () => _ = Run(() => model.DuplicateAsync(id))));
        menu.Items.Add(Item("Move to Trash", "Delete", () => _ = DeleteAsync([id], forever: false)));
        menu.PlacementTarget = NoteMoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Export(string format)
    {
        if (Model is not { } model)
            return;
        var title = model.Board?.Find(model.SelectedId!)?.DisplayTitle ?? "note";
        var dialog = new SaveFileDialog
        {
            FileName = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } safe ? safe : "note",
            Filter = format == "html" ? "Web page (*.html)|*.html" : "Markdown (*.md)|*.md",
            DefaultExt = format == "html" ? ".html" : ".md",
        };
        if (dialog.ShowDialog(Owner) == true)
            _ = Run(() => model.ExportAsync(dialog.FileName, format));
    }

    private void ImportHtml()
    {
        var dialog = new OpenFileDialog { Filter = "Web page (*.html;*.htm)|*.html;*.htm" };
        if (dialog.ShowDialog(Owner) == true && Model is { } model)
            _ = Run(() => model.ImportHtmlAsync(dialog.FileName));
    }

    private async Task ImportAccountNotesAsync()
    {
        try
        {
            var count = await Model!.ImportAccountNotesAsync();
            Dialogs.Alert(Owner, count == 0 ? "No new notes were found in your mail accounts." : $"{count} notes were copied into Alondra1 Notes. The originals were not changed.", "Notes");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Dialogs.Alert(Owner, "The notes could not be read from the mail accounts: " + ex.Message, "Notes");
        }
    }

    private void PickPictures()
    {
        var dialog = new OpenFileDialog { Filter = "Pictures (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp", Multiselect = true };
        if (dialog.ShowDialog(Owner) == true)
            _ = Model?.InsertPicturesAsync(dialog.FileNames);
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || sender is not Button button)
            return;
        var menu = button.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Item("Link a task…", "Todo", () =>
        {
            var tasks = model.Host.Todo.AllTasksForLinking();
            if (LinkPicker.Pick(Owner, "Link a task", tasks.Select(t => (t.Id, t.Title, t.Completed ? "Completed" : "")).ToList()) is { } picked)
                _ = Run(() => model.AddLinkAsync(new NoteLink(NoteLink.Task, picked.Id, picked.Label)));
        }));
        var loading = Item("Link an event or a contact…", "Calendar", () => { });
        loading.Click += async (_, _) =>
        {
            var (events, contacts) = await model.Host.LinkTargetsAsync();
            var choices = events.Select(ev => ("event:" + ev.Id, ev.Subject, $"{ev.Start.ToLocalTime():g} · {ev.AccountName}"))
                .Concat(contacts.Select(c => ("contact:" + c.Id, c.Name, c.Email))).ToList();
            if (choices.Count == 0)
            {
                Dialogs.Alert(Owner, "No events or contacts were found. Connect a Microsoft 365 or Google account with calendar and contacts access.", "Link");
                return;
            }

            if (LinkPicker.Pick(Owner, "Link an event or a contact", choices) is { } picked)
            {
                var (type, id) = picked.Id.Split(':', 2) is [var t, var i] ? (t, i) : ("event", picked.Id);
                await Run(() => model.AddLinkAsync(new NoteLink(type, id, picked.Label)));
            }
        };
        menu.Items.Add(loading);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        await Task.CompletedTask;
    }

    private void CompareVersions_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { Conflict: { } issue } model)
            _ = NoteConflictDialog.ShowAsync(Owner, model, issue);
    }

    private void LinkChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NoteLinkChip chip)
            _ = Model?.OpenLinkedAsync(chip.Link);
    }

    private void LinkRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NoteLinkChip chip)
            _ = Run(() => Model!.RemoveLinkAsync(chip.Link));
    }
}

/// <summary>A searchable list to choose one item (tasks, events, contacts) for a link.</summary>
public static class LinkPicker
{
    public static (string Id, string Label)? Pick(Window owner, string title, IReadOnlyList<(string Id, string Label, string Detail)> items)
    {
        var window = Dialogs.Create(title, owner, 520, 560);
        var panel = Dialogs.Panel(window);
        var filter = new TextBox();
        panel.Children.Add(Dialogs.Labeled("Search", filter));
        var list = new ListBox { Height = 340, DisplayMemberPath = "Text" };
        System.Windows.Automation.AutomationProperties.SetName(list, title);
        panel.Children.Add(list);
        void Fill()
        {
            list.Items.Clear();
            foreach (var item in items.Where(i => filter.Text.Length == 0 || (i.Label + " " + i.Detail).Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase)).Take(500))
                list.Items.Add(new { item.Id, item.Label, Text = string.IsNullOrEmpty(item.Detail) ? item.Label : $"{item.Label}   ·   {item.Detail}" });
        }

        filter.TextChanged += (_, _) => Fill();
        Fill();
        (string, string)? chosen = null;
        void Accept()
        {
            if (list.SelectedItem is { } selected)
            {
                dynamic d = selected;
                chosen = ((string)d.Id, (string)d.Label);
                window.Close();
            }
        }

        list.MouseDoubleClick += (_, _) => Accept();
        var footer = Dialogs.Footer(window);
        footer.Children.Add(Dialogs.Cancel(window));
        footer.Children.Add(Dialogs.Primary("Link", Accept));
        filter.Focus();
        window.ShowDialog();
        return chosen;
    }
}

internal static class VisualTreeExtensions
{
    public static T? FindParent<T>(this DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}
