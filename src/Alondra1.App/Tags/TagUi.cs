using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Alondra1.Core.Tags;

namespace Alondra1.App.Tags;

/// <summary>A tag as shown on an item: its name and a color dot (supplemental; the name is always there).</summary>
public sealed record TagChip(string Id, string Name, Brush Dot)
{
    public string AccessName => "Tag " + Name;
}

/// <summary>One tagged item in the cross-module Tags view, with how to open it.</summary>
public sealed record TaggedItem(string Module, string Title, string Detail, Action? Open);

/// <summary>
/// The shared tag UI on Windows (FEATURES_EXTENSION Part B §2.2): the tag picker for one or many items (mail, notes,
/// tasks, events), chips, the Tags settings page and the tagged-items view. Tags are never required; nothing here
/// changes the tagged items themselves.
/// </summary>
public static class TagUi
{
    private static readonly Dictionary<string, Color> Palette = new()
    {
        ["gray"] = Color.FromRgb(0x8A, 0x8F, 0x98), ["red"] = Color.FromRgb(0xD9, 0x48, 0x4C), ["orange"] = Color.FromRgb(0xE0, 0x7B, 0x2E),
        ["yellow"] = Color.FromRgb(0xD6, 0xA9, 0x1C), ["green"] = Color.FromRgb(0x3C, 0xA5, 0x5C), ["teal"] = Color.FromRgb(0x23, 0x9C, 0x9C),
        ["blue"] = Color.FromRgb(0x3D, 0x7B, 0xE0), ["purple"] = Color.FromRgb(0x8A, 0x5C, 0xD6), ["pink"] = Color.FromRgb(0xD6, 0x5C, 0xA4),
    };

    public static Brush BrushFor(string? color)
    {
        var brush = new SolidColorBrush(color is not null && Palette.TryGetValue(color, out var c) ? c : Palette["gray"]);
        brush.Freeze();
        return brush;
    }

    public static IReadOnlyList<TagChip> Chips(IEnumerable<Tag> tags) => tags.Select(t => new TagChip(t.Id, t.Name, BrushFor(t.Color))).ToList();

    /// <summary>"Travel", "Travel +2" (compact text for list rows; the full list is in the item's details).</summary>
    public static string Summary(IReadOnlyList<Tag> tags) => tags.Count switch
    {
        0 => "",
        1 => tags[0].Name,
        _ => $"{tags[0].Name} +{tags.Count - 1}",
    };

    // ------------------------------------------------------------------ picker

    /// <summary>
    /// The tag picker for one or several items: search, tick tags, create a tag from the typed text. Ticks show whether
    /// all, some (indeterminate) or none of the items have a tag; only the tags the user changes are applied (add to
    /// all, or remove from all), so other tags on the items stay as they are. Returns false when cancelled.
    /// </summary>
    public static async Task<bool> PickAsync(Window owner, TagBoard board, string objectType, IReadOnlyList<string> refs, string what,
        IEnumerable<MailTagTarget>? mailTargets = null)
    {
        if (refs.Count == 0)
            return false;
        var window = Dialogs.Create("Tags", owner, 400, sizeToContent: true);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Note(what));
        var search = new TextBox();
        panel.Children.Add(Dialogs.Labeled("Find or create a tag", search));
        var list = new StackPanel();
        var scroller = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        AutomationProperties.SetName(list, "Tags");
        panel.Children.Add(scroller);
        var create = new Button { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        panel.Children.Add(create);
        var empty = Dialogs.Note("No tags yet. Type a name to create one.");
        panel.Children.Add(empty);

        var initial = new Dictionary<string, bool?>();
        var boxes = new Dictionary<string, CheckBox>();
        bool? StateOf(string tagId)
        {
            var have = refs.Count(r => board.TagsOf(objectType, r).Any(t => t.Id == tagId));
            return have == 0 ? false : have == refs.Count ? true : null;
        }

        void AddBox(Tag tag, bool? state)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = BrushFor(tag.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = tag.Name, VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox { Content = row, IsThreeState = state is null, IsChecked = state, Margin = new Thickness(0, 3, 0, 3), Tag = tag };
            AutomationProperties.SetName(box, tag.Name);
            // A mixed tag cycles all → none → back to mixed; a click never leaves it in the third state by accident.
            box.Click += (_, _) => box.IsThreeState = false;
            boxes[tag.Id] = box;
            list.Children.Add(box);
        }

        foreach (var tag in board.Tags)
        {
            var state = StateOf(tag.Id);
            initial[tag.Id] = state;
            AddBox(tag, state);
        }

        void Filter()
        {
            var text = search.Text.Trim();
            foreach (var box in boxes.Values)
                box.Visibility = text.Length == 0 || ((Tag)box.Tag).Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            var exact = text.Length > 0 && board.FindByName(text) is not null;
            create.Visibility = text.Length > 0 && !exact ? Visibility.Visible : Visibility.Collapsed;
            create.Content = $"Create tag “{text}”";
            empty.Visibility = boxes.Count == 0 && text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task CreateFromText()
        {
            try
            {
                var tag = await board.CreateAsync(search.Text);
                initial[tag.Id] = false;
                AddBox(tag, true);
                search.Text = "";
                Filter();
                boxes[tag.Id].Focus();
            }
            catch (TagException ex)
            {
                Dialogs.Alert(window, ex.Message, "Tags");
            }
        }

        search.TextChanged += (_, _) => Filter();
        search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if (board.FindByName(search.Text.Trim()) is { } match && boxes.TryGetValue(match.Id, out var box))
                box.IsChecked = box.IsChecked != true;
            else if (search.Text.Trim().Length > 0)
                await CreateFromText();
        };
        create.Click += async (_, _) => await CreateFromText();
        Filter();

        var applied = false;
        var actions = Dialogs.Footer(window);
        actions.Children.Add(Dialogs.Cancel(window));
        actions.Children.Add(Dialogs.Primary("Apply", async () =>
        {
            try
            {
                foreach (var (tagId, box) in boxes)
                {
                    var before = initial.GetValueOrDefault(tagId, false);
                    if (box.IsChecked == before || box.IsChecked is null)
                        continue;
                    if (board.Find(tagId) is null)
                        continue; // deleted meanwhile (another window or a sync): nothing to apply
                    await board.SetTagAsync(objectType, refs, tagId, box.IsChecked == true, mailTargets);
                }

                applied = true;
                window.Close();
            }
            catch (TagException ex)
            {
                Dialogs.Alert(window, ex.Message, "Tags");
            }
        }));
        window.Loaded += (_, _) => search.Focus();
        window.ShowDialog();
        return applied;
    }

    // ------------------------------------------------------------------ settings → tags

    /// <summary>
    /// Settings → Tags: every tag with its usage count; rename, color, delete (with the number of items affected) and
    /// show the tagged items. <paramref name="showItems"/> opens the cross-module view for a tag.
    /// </summary>
    public static StackPanel ManagePage(TagBoard? board, Func<Window?> owner, Action<string> showItems, Func<string, int> rulesUsing)
    {
        var page = new StackPanel();
        page.Children.Add(Dialogs.Title("Tags"));
        if (board is null)
        {
            page.Children.Add(Dialogs.Note("Tags aren't available because the task database couldn't be opened."));
            return page;
        }

        page.Children.Add(Dialogs.Note("Tags are your own labels for mail, notes, tasks and calendar events. Renaming a tag updates it everywhere; deleting one removes it from items but never deletes the items."));
        var add = new TextBox { Width = 240 };
        AutomationProperties.SetName(add, "New tag name");
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        async Task AddTag()
        {
            try
            {
                await board.CreateAsync(add.Text);
                add.Text = "";
            }
            catch (TagException ex)
            {
                Dialogs.Alert(owner(), ex.Message, "Tags");
            }
        }

        add.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await AddTag();
            }
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(add);
        addRow.Children.Add(Dialogs.Button("Add tag", AddTag));
        page.Children.Add(Dialogs.Labeled("New tag", new ContentControl { Content = addRow, Focusable = false }));
        page.Children.Add(list);

        void Fill()
        {
            list.Children.Clear();
            if (board.Tags.Count == 0)
                list.Children.Add(Dialogs.Note("No tags yet."));
            foreach (var tag in board.Tags)
            {
                var count = board.UsageCount(tag.Id);
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                DockPanel.SetDock(buttons, Dock.Right);
                var colors = new ComboBox { ItemsSource = new[] { "Default" }.Concat(TagRules.Colors.Skip(1).Select(c => char.ToUpperInvariant(c[0]) + c[1..])).ToList(), Width = 110, Margin = new Thickness(0, 0, 8, 0) };
                colors.SelectedIndex = tag.Color is null || tag.Color == "gray" ? 0 : TagRules.Colors.ToList().IndexOf(tag.Color);
                AutomationProperties.SetName(colors, "Color of " + tag.Name);
                colors.SelectionChanged += async (_, _) =>
                {
                    var chosen = colors.SelectedIndex <= 0 ? null : TagRules.Colors[colors.SelectedIndex];
                    if (chosen != tag.Color)
                        await board.SetColorAsync(tag.Id, chosen);
                };
                buttons.Children.Add(colors);
                buttons.Children.Add(Dialogs.Button("Show items", () => showItems(tag.Id)));
                buttons.Children.Add(Dialogs.Button("Rename", async () =>
                {
                    if (Prompt(owner(), "Rename tag", "Name", tag.Name) is { } name && name != tag.Name)
                    {
                        try
                        {
                            await board.RenameAsync(tag.Id, name);
                        }
                        catch (TagException ex)
                        {
                            Dialogs.Alert(owner(), ex.Message, "Tags");
                        }
                    }
                }));
                buttons.Children.Add(Dialogs.Danger("Delete", async () =>
                {
                    var rules = rulesUsing(tag.Id);
                    var message = $"Delete the tag “{tag.Name}”? It is removed from {Plural(count, "item")}" + (rules > 0 ? $", and {Plural(rules, "mail rule")} that use it will be turned off until you fix them" : "") + ". The items themselves are not deleted.";
                    if (Dialogs.Alert(owner(), message, "Delete tag", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                        await board.DeleteAsync(tag.Id);
                }));
                row.Children.Add(buttons);
                var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = BrushFor(tag.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = tag.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock { Text = "  " + Plural(count, "item"), Foreground = (Brush)Application.Current.FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(label);
                list.Children.Add(row);
            }
        }

        // Follow changes (renames from another window, a sync) only while the page is shown.
        void OnChanged() => page.Dispatcher.Invoke(Fill);
        page.Loaded += (_, _) => board.Changed += OnChanged;
        page.Unloaded += (_, _) => board.Changed -= OnChanged;
        Fill();
        return page;
    }

    // ------------------------------------------------------------------ tagged items

    /// <summary>
    /// The cross-module view of a tag: mail, notes, tasks and calendar events carrying it, grouped, with counts and the
    /// normal open action. Items not on this PC (for example mail kept on another computer) are listed as such.
    /// </summary>
    public static void ShowItems(Window owner, TagBoard board, string tagId, Func<TagLink, TaggedItem> resolve)
    {
        if (board.Find(tagId) is not { } tag)
            return;
        var window = Dialogs.Create($"Tagged “{tag.Name}”", owner, 560, 560);
        var panel = Dialogs.Panel(window);
        var items = board.LinksOf(tagId).Select(resolve).ToList();
        if (items.Count == 0)
            panel.Children.Add(Dialogs.Note("Nothing has this tag yet."));
        foreach (var group in items.GroupBy(i => i.Module).OrderBy(g => Array.IndexOf(["Mail", "Notes", "To Do", "Calendar"], g.Key)))
        {
            panel.Children.Add(Dialogs.Subtitle($"{group.Key} · {group.Count()}", first: panel.Children.Count == 0));
            foreach (var item in group.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                if (item.Detail.Length > 0)
                    text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                if (item.Open is { } open)
                {
                    var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4), Style = (Style)Application.Current.FindResource("QuietButton") };
                    AutomationProperties.SetName(button, $"Open {item.Title}");
                    button.Click += (_, _) =>
                    {
                        window.Close();
                        open();
                    };
                    panel.Children.Add(button);
                }
                else
                {
                    text.Margin = new Thickness(12, 0, 0, 6);
                    panel.Children.Add(text);
                }
            }
        }

        var actions = Dialogs.Footer(window);
        actions.Children.Add(Dialogs.Cancel(window));
        ((Button)actions.Children[^1]).Content = "Close";
        window.ShowDialog();
    }

    private static string? Prompt(Window? owner, string title, string label, string value)
    {
        var window = Dialogs.Create(title, owner ?? Application.Current.MainWindow, 380, sizeToContent: true);
        var panel = Dialogs.Panel(window);
        var box = new TextBox { Text = value };
        panel.Children.Add(Dialogs.Labeled(label, box));
        string? result = null;
        var actions = Dialogs.Footer(window);
        actions.Children.Add(Dialogs.Cancel(window));
        actions.Children.Add(Dialogs.Primary("Save", () =>
        {
            result = box.Text;
            window.Close();
        }));
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                window.Close();
            }
        };
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.ShowDialog();
        return result;
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
