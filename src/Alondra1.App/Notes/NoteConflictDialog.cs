using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Alondra1.Core.Cloud;
using Alondra1.Core.Notes;

namespace Alondra1.App.Notes;

/// <summary>
/// Compares the two versions of a note that conflicted during sync (Notes spec §10.2): the other device's version
/// (the note) and this PC's version (its conflict copy), side by side with the differing lines marked. The user keeps
/// one, combines both into the note, or keeps both as separate notes.
/// </summary>
public static class NoteConflictDialog
{
    public static async Task ShowAsync(Window owner, NotesViewModel model, SyncIssue issue)
    {
        var (other, mine) = await model.ConflictVersionsAsync(issue);
        if (mine is null)
        {
            // The conflict copy was deleted or already resolved elsewhere: nothing left to compare.
            await model.DismissConflictAsync(issue);
            Dialogs.Alert(owner, "This PC's version is no longer there (it was deleted or already resolved).", "Compare versions");
            return;
        }

        var window = Dialogs.Create("Compare versions", owner, 960, 680);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title($"Two versions of “{NoteMerge.OriginalTitle(mine.Title)}”"));
        panel.Children.Add(Dialogs.Note(other is null
            ? "The note was deleted on another device while you edited it on this PC. Your version was kept."
            : "The note was changed on another device and on this PC before they synced. Nothing was lost. Lines that are only in one version are marked."));

        var lines = NoteMerge.DiffLines(other?.Body ?? "", mine.Body);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.Children.Add(Column("Other device", other, lines, DiffSide.OnlyOther));
        var right = Column("This PC", mine, lines, DiffSide.OnlyMine);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);
        panel.Children.Add(columns);

        async Task Choose(NoteConflictChoice choice)
        {
            await model.ResolveConflictAsync(issue, choice);
            window.Close();
        }

        var footer = Dialogs.Footer(window);
        footer.Children.Add(Dialogs.Cancel(window));
        footer.Children.Add(Tip(Dialogs.Button("Keep both", () => Choose(NoteConflictChoice.KeepBoth)), "Keep two separate notes."));
        if (other is not null)
        {
            footer.Children.Add(Tip(Dialogs.Button("Keep other device's", () => Choose(NoteConflictChoice.KeepOther)), "This PC's version goes to Trash."));
            footer.Children.Add(Tip(Dialogs.Button("Keep this PC's", () => Choose(NoteConflictChoice.KeepMine)), "It replaces the other device's version; the copy goes to Trash."));
            footer.Children.Add(Tip(Dialogs.Primary("Combine both", () => Choose(NoteConflictChoice.Combine)),
                "Put everything from both versions into the note, shared parts once, then edit it."));
        }
        else
        {
            footer.Children.Add(Tip(Dialogs.Button("Delete my version", () => Choose(NoteConflictChoice.KeepOther)), "Your version goes to Trash, like on the other device."));
            footer.Children.Add(Tip(Dialogs.Primary("Keep my version", () => Choose(NoteConflictChoice.KeepMine)), "Keep your version as the note."));
        }

        window.ShowDialog();
    }

    private static Button Tip(Button button, string tip)
    {
        button.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tip);
        return button;
    }

    private static UIElement Column(string heading, Note? note, IReadOnlyList<DiffLine> lines, DiffSide own)
    {
        var column = new StackPanel();
        System.Windows.Automation.AutomationProperties.SetName(column, heading);
        column.Children.Add(Dialogs.Subtitle(heading, first: true));
        if (note is null)
        {
            column.Children.Add(Dialogs.Note("Deleted on that device."));
            return column;
        }

        column.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(note.Title) ? "Untitled" : NoteMerge.OriginalTitle(note.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var details = $"Edited {note.ModifiedUtc.ToLocalTime():g}";
        if (note.Assets.Count > 0)
            details += $" · {note.Assets.Count} picture{(note.Assets.Count == 1 ? "" : "s")}";
        if (note.Links.Count > 0)
            details += $" · {note.Links.Count} link{(note.Links.Count == 1 ? "" : "s")}";
        column.Children.Add(Dialogs.Note(details));
        var marked = (Brush)Application.Current.FindResource("SelectedBrush");
        var edge = (Brush)Application.Current.FindResource("AccentWarmBrush");
        var body = new StackPanel();
        foreach (var line in lines.Where(l => l.Side == DiffSide.Both || l.Side == own).Take(2000))
        {
            var text = new TextBlock { Text = line.Text.Length == 0 ? " " : line.Text, TextWrapping = TextWrapping.Wrap };
            body.Children.Add(line.Side == own
                ? new Border { Child = text, Background = marked, BorderBrush = edge, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(6, 1, 4, 1), ToolTip = "Only in this version" }
                : new Border { Child = text, Padding = new Thickness(9, 1, 4, 1) });
        }

        column.Children.Add(new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Padding = new Thickness(4, 8, 4, 8),
            Child = body,
        });
        return column;
    }
}
