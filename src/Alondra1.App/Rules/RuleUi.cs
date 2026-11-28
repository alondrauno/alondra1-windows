using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Alondra1.App.ViewModels;
using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Rules;
using Alondra1.Core.Tags;

namespace Alondra1.App.Rules;

/// <summary>
/// Settings → Mail rules and Settings → Blocked senders &amp; words, the rule builder, rule activity, Block sender /
/// Block domain and Create rule from a message, and the check before sending (FEATURES_EXTENSION Part B §3.1, §5.2).
/// </summary>
public static class RuleUi
{
    private static Brush Muted => (Brush)Application.Current.FindResource("MutedBrush");

    private static string AccountLabel(MainViewModel model, string? id) =>
        id is null ? "All accounts" : model.Config.Accounts.FirstOrDefault(a => a.Id == id) is { } a ? ComposeIdentity.Label(a) : "A removed account";

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var c in children)
            row.Children.Add(c);
        return row;
    }

    /// <summary>Re-fills a page while it is shown, whenever rules change (and stops listening when it closes).</summary>
    private static void Live(FrameworkElement page, MailRuleService rules, Action fill)
    {
        void OnChanged() => page.Dispatcher.BeginInvoke(fill);
        page.Loaded += (_, _) =>
        {
            rules.Changed += OnChanged;
            fill();
        };
        page.Unloaded += (_, _) => rules.Changed -= OnChanged;
    }

    // ------------------------------------------------------------------ rules page

    public static StackPanel RulesPage(MainViewModel model, Func<Window?> owner)
    {
        var page = new StackPanel();
        page.Children.Add(Dialogs.Title("Mail rules"));
        if (model.Rules is not { } rules)
        {
            page.Children.Add(Dialogs.Note("Mail rules aren't available because the mail database couldn't be opened."));
            return page;
        }

        page.Children.Add(Dialogs.Note("Rules run on this PC, in order, on mail that arrives after an account's first download (accounts that keep mail on this PC). Outgoing rules check every message before it is sent. Rules never forward, reply, or delete for good."));
        var switches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var incoming = new CheckBox { Content = "Run incoming rules", IsChecked = rules.IncomingEnabled, Margin = new Thickness(0, 0, 16, 0) };
        incoming.Click += (_, _) =>
        {
            rules.IncomingEnabled = incoming.IsChecked == true;
            model.Config.Reading.IncomingRulesOff = !rules.IncomingEnabled;
            model.ConfigStoreSave();
        };
        switches.Children.Add(incoming);
        page.Children.Add(switches);
        page.Children.Add(Dialogs.ButtonRow(
            Dialogs.Primary("New rule", () => Edit(owner(), model, null)),
            Dialogs.Button("Rule activity", () => ShowActivity(owner(), model))));
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        page.Children.Add(list);

        void Fill()
        {
            list.Children.Clear();
            foreach (var direction in new[] { RuleDirection.Incoming, RuleDirection.Outgoing })
            {
                list.Children.Add(Dialogs.Subtitle(direction == RuleDirection.Incoming ? "Incoming" : "Outgoing", first: direction == RuleDirection.Incoming));
                var ofDirection = rules.Rules.Where(r => r.Direction == direction).ToList();
                if (ofDirection.Count == 0)
                    list.Children.Add(Dialogs.Note(direction == RuleDirection.Incoming ? "No incoming rules." : "No outgoing rules."));
                foreach (var rule in ofDirection)
                    list.Children.Add(RuleRow(model, rules, rule, owner));
            }
        }

        Live(page, rules, Fill);
        return page;
    }

    private static UIElement RuleRow(MainViewModel model, MailRuleService rules, MailRule rule, Func<Window?> owner)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
        AutomationProperties.SetName(enabled, "Turn on " + rule.Name);
        enabled.Click += async (_, _) =>
        {
            try
            {
                await rules.SetEnabledAsync(rule.Id, enabled.IsChecked == true);
            }
            catch (RuleException ex)
            {
                enabled.IsChecked = rule.Enabled;
                Dialogs.Alert(owner(), ex.Message, "Mail rules");
            }
        };
        DockPanel.SetDock(enabled, Dock.Left);
        row.Children.Add(enabled);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(buttons, Dock.Right);
        var up = Dialogs.Button("↑", () => rules.MoveAsync(rule.Id, -1));
        AutomationProperties.SetName(up, "Move " + rule.Name + " up");
        var down = Dialogs.Button("↓", () => rules.MoveAsync(rule.Id, 1));
        AutomationProperties.SetName(down, "Move " + rule.Name + " down");
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(Dialogs.Button("Edit", () => Edit(owner(), model, rule)));
        buttons.Children.Add(Dialogs.Button("Duplicate", () => rules.DuplicateAsync(rule.Id)));
        buttons.Children.Add(Dialogs.Danger("Delete", async () =>
        {
            if (Dialogs.Alert(owner(), $"Delete the rule “{rule.Name}”? Messages it already changed stay as they are.", "Delete rule", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                await rules.DeleteRuleAsync(rule.Id);
        }));
        row.Children.Add(buttons);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = rule.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = AccountLabel(model, rule.AccountId) + " · " + Summary(model, rule)
            + (rule.LastRunUtc is { } last ? $" · last run {last.ToLocalTime():g}" + (rule.LastResult is { } r ? $" ({r})" : "") : " · not run yet");
        text.Children.Add(new TextBlock { Text = detail, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        if (rule.SuspendedReason is { } why)
            text.Children.Add(new TextBlock { Text = "Paused: " + why, Foreground = (Brush)Application.Current.FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap });
        else if (!rule.Enabled)
            text.Children.Add(new TextBlock { Text = "Off", Foreground = Muted });
        row.Children.Add(text);
        return row;
    }

    /// <summary>A rule in words: "If From ends with @lists.example (and 1 more) → Add tag News, Archive".</summary>
    public static string Summary(MainViewModel model, MailRule rule)
    {
        string Operand(RuleField f, string v) => f == RuleField.Tag ? TagName(model, v) : f == RuleField.Account ? AccountLabel(model, v) : v;
        var conditions = rule.Conditions.Count == 0
            ? "every message"
            : string.Join(rule.Match == RuleMatch.All ? " and " : " or ", rule.Conditions.Take(2).Select(c =>
                $"{RuleValidator.Describe(c.Field)} {RuleValidator.Describe(c.Operator)} {Operand(c.Field, c.Value)}".Trim()))
              + (rule.Conditions.Count > 2 ? $" (and {rule.Conditions.Count - 2} more)" : "");
        var actions = string.Join(", ", rule.Actions.Select(a => RuleValidator.Describe(a.Kind) + (a.Kind is RuleActionKind.AddTag or RuleActionKind.RemoveTag ? " " + TagName(model, a.Value)
            : a.Kind == RuleActionKind.MoveTo ? " " + a.Value : "")));
        return $"If {conditions} → {actions}" + (rule.StopProcessing ? ", then stop" : "");
    }

    private static string TagName(MainViewModel model, string id) => model.TagBoard?.Find(id)?.Name ?? "(deleted tag)";

    // ------------------------------------------------------------------ rule editor

    /// <summary>Creates (null) or edits a rule. A new rule, or one made from a message, is off until the user turns it on.</summary>
    public static void Edit(Window? owner, MainViewModel model, MailRule? existing, MailRule? prefill = null)
    {
        if (model.Rules is not { } rules)
            return;
        var rule = existing?.Clone() ?? prefill ?? new MailRule { Direction = RuleDirection.Incoming, Enabled = false };
        var window = Dialogs.Create(existing is null ? "New rule" : "Edit rule", owner ?? Application.Current.MainWindow, 760, 680);
        var panel = Dialogs.Panel(window);
        var tags = model.TagBoard?.Tags ?? [];

        var name = new TextBox { Text = rule.Name, MaxLength = RuleLimits.MaxName };
        panel.Children.Add(Dialogs.Labeled("Name", name));
        var accounts = new List<(string? Id, string Label)> { (null, "All accounts") };
        accounts.AddRange(model.Config.Accounts.Select(a => ((string?)a.Id, ComposeIdentity.Label(a))));
        var account = new ComboBox { ItemsSource = accounts.Select(a => a.Label).ToList(), SelectedIndex = Math.Max(0, accounts.FindIndex(a => a.Id == rule.AccountId)), Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Dialogs.Labeled("Account", account));
        var direction = new ComboBox { ItemsSource = new[] { "Incoming: mail I receive", "Outgoing: mail I send" }, SelectedIndex = (int)rule.Direction, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Dialogs.Labeled("Direction", direction));
        var match = new ComboBox { ItemsSource = new[] { "All of these conditions", "Any of these conditions" }, SelectedIndex = (int)rule.Match, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Dialogs.Labeled("Applies when", match));

        var conditionRows = new StackPanel();
        var actionRows = new StackPanel();
        var conditionEditors = new List<Func<RuleCondition>>();
        var actionEditors = new List<Func<RuleAction>>();
        RuleDirection Direction() => (RuleDirection)Math.Max(0, direction.SelectedIndex);
        AccountConfig? Account() => accounts.ElementAtOrDefault(account.SelectedIndex).Id is { } id ? model.Config.Accounts.FirstOrDefault(a => a.Id == id) : null;

        ComboBox TagBox(string value)
        {
            var box = new ComboBox { ItemsSource = tags.Select(t => t.Name).ToList(), Width = 200, SelectedIndex = tags.ToList().FindIndex(t => t.Id == value) };
            AutomationProperties.SetName(box, "Tag");
            return box;
        }

        void AddCondition(RuleCondition c)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var fields = RuleValidator.FieldsFor(Direction()).ToList();
            var field = new ComboBox { ItemsSource = fields.Select(RuleValidator.Describe).ToList(), Width = 150, SelectedIndex = Math.Max(0, fields.IndexOf(c.Field)), Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetName(field, "Field");
            var op = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetName(op, "Operator");
            var host = new ContentControl { Focusable = false };
            var text = new TextBox { Text = c.Value, Width = 220, MaxLength = RuleLimits.MaxOperand };
            AutomationProperties.SetName(text, "Value");
            ComboBox? tagBox = null;
            ComboBox? accountBox = null;
            RuleField Field() => fields[Math.Max(0, field.SelectedIndex)];
            void Refresh(bool keepOperator)
            {
                var ops = RuleValidator.OperatorsFor(Field()).ToList();
                var previous = keepOperator ? c.Operator : ops[0];
                op.ItemsSource = ops.Select(RuleValidator.Describe).ToList();
                op.SelectedIndex = Math.Max(0, ops.IndexOf(previous));
                host.Content = Field() switch
                {
                    RuleField.Tag => tagBox = TagBox(c.Value),
                    RuleField.Account => accountBox = new ComboBox { ItemsSource = model.Config.Accounts.Select(ComposeIdentity.Label).ToList(), Width = 220,
                        SelectedIndex = model.Config.Accounts.FindIndex(a => a.Id == c.Value) },
                    RuleField.HasAttachments or RuleField.Unread or RuleField.Flagged => null,
                    _ => text,
                };
            }

            field.SelectionChanged += (_, _) => Refresh(keepOperator: false);
            op.SelectionChanged += (_, _) => host.Visibility = RuleValidator.OperatorsFor(Field()).ElementAtOrDefault(op.SelectedIndex) is RuleOperator.Exists or RuleOperator.DoesNotExist
                && Field() is not RuleField.Tag ? Visibility.Collapsed : Visibility.Visible;
            Refresh(keepOperator: true);
            row.Children.Add(field);
            row.Children.Add(op);
            row.Children.Add(host);
            Func<RuleCondition> read = () => new RuleCondition
            {
                Field = Field(),
                Operator = RuleValidator.OperatorsFor(Field()).ElementAtOrDefault(op.SelectedIndex),
                Value = Field() switch
                {
                    RuleField.Tag => tags.ElementAtOrDefault(tagBox?.SelectedIndex ?? -1)?.Id ?? "",
                    RuleField.Account => model.Config.Accounts.ElementAtOrDefault(accountBox?.SelectedIndex ?? -1)?.Id ?? "",
                    _ => text.Text,
                },
            };
            var remove = Dialogs.Button("Remove", () =>
            {
                conditionRows.Children.Remove(row);
                conditionEditors.Remove(read);
            });
            AutomationProperties.SetName(remove, "Remove condition");
            row.Children.Add(remove);
            conditionEditors.Add(read);
            conditionRows.Children.Add(row);
        }

        void AddAction(RuleAction a)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var kinds = RuleValidator.ActionsFor(Direction()).ToList();
            var kind = new ComboBox { ItemsSource = kinds.Select(RuleValidator.Describe).ToList(), Width = 170, SelectedIndex = Math.Max(0, kinds.IndexOf(a.Kind)), Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetName(kind, "Action");
            var host = new ContentControl { Focusable = false };
            var text = new TextBox { Text = a.Value, Width = 300, MaxLength = RuleLimits.MaxOperand };
            AutomationProperties.SetName(text, "Message shown");
            ComboBox? tagBox = null;
            ComboBox? folderBox = null;
            List<KnownFolder> folders = [];
            RuleActionKind Kind() => kinds[Math.Max(0, kind.SelectedIndex)];
            void Refresh()
            {
                host.Content = Kind() switch
                {
                    RuleActionKind.AddTag or RuleActionKind.RemoveTag => tagBox = TagBox(a.Value),
                    RuleActionKind.MoveTo => folderBox = FolderBox(),
                    RuleActionKind.Warn or RuleActionKind.Block => text,
                    _ => null,
                };
            }

            ComboBox FolderBox()
            {
                folders = Account()?.Folders.Known ?? [];
                var box = new ComboBox { ItemsSource = folders.Select(f => f.Name).ToList(), Width = 220, SelectedIndex = folders.FindIndex(f => f.Id == a.Value) };
                AutomationProperties.SetName(box, "Folder");
                if (folders.Count == 0)
                    box.ToolTip = "Choose one account first; its folders appear after it has synced.";
                return box;
            }

            kind.SelectionChanged += (_, _) => Refresh();
            account.SelectionChanged += (_, _) => { if (Kind() == RuleActionKind.MoveTo) Refresh(); };
            Refresh();
            row.Children.Add(kind);
            row.Children.Add(host);
            Func<RuleAction> read = () => new RuleAction
            {
                Kind = Kind(),
                Value = Kind() switch
                {
                    RuleActionKind.AddTag or RuleActionKind.RemoveTag => tags.ElementAtOrDefault(tagBox?.SelectedIndex ?? -1)?.Id ?? "",
                    RuleActionKind.MoveTo => folders.ElementAtOrDefault(folderBox?.SelectedIndex ?? -1)?.Id ?? "",
                    RuleActionKind.Warn or RuleActionKind.Block => text.Text,
                    _ => "",
                },
            };
            var remove = Dialogs.Button("Remove", () =>
            {
                actionRows.Children.Remove(row);
                actionEditors.Remove(read);
            });
            AutomationProperties.SetName(remove, "Remove action");
            row.Children.Add(remove);
            actionEditors.Add(read);
            actionRows.Children.Add(row);
        }

        void Rebuild(bool keep)
        {
            var conditions = keep ? conditionEditors.Select(f => f()).ToList() : rule.Conditions;
            var actions = keep ? actionEditors.Select(f => f()).ToList() : rule.Actions;
            conditionRows.Children.Clear();
            conditionEditors.Clear();
            actionRows.Children.Clear();
            actionEditors.Clear();
            var fields = RuleValidator.FieldsFor(Direction());
            var kinds = RuleValidator.ActionsFor(Direction());
            foreach (var c in conditions.Where(c => fields.Contains(c.Field)))
                AddCondition(c);
            foreach (var a in actions.Where(a => kinds.Contains(a.Kind)))
                AddAction(a);
        }

        panel.Children.Add(Dialogs.Subtitle("Conditions"));
        panel.Children.Add(conditionRows);
        var all = new CheckBox { Content = "No conditions: apply to every message", IsChecked = rule.AllMessages, Margin = new Thickness(0, 4, 0, 6) };
        panel.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Add condition", () =>
        {
            AddCondition(new RuleCondition { Field = RuleField.From, Operator = RuleOperator.Contains });
        })));
        panel.Children.Add(all);
        panel.Children.Add(Dialogs.Note("Text is compared ignoring case and accents in composed form; addresses use the email address only, never the display name. A condition on the message text can't be checked until the text is downloaded: such a rule then does nothing."));
        panel.Children.Add(Dialogs.Subtitle("Actions"));
        panel.Children.Add(actionRows);
        panel.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Add action", () =>
            AddAction(new RuleAction { Kind = Direction() == RuleDirection.Incoming ? RuleActionKind.AddTag : RuleActionKind.Warn }))));
        var stop = new CheckBox { Content = "Stop processing more rules after this one", IsChecked = rule.StopProcessing, Margin = new Thickness(0, 8, 0, 2) };
        panel.Children.Add(stop);
        panel.Children.Add(Dialogs.Note("Stop also skips later outgoing rules, including ones that would warn or stop a message."));
        var enabled = new CheckBox { Content = "Turn the rule on", IsChecked = rule.Enabled, Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(enabled);
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        direction.SelectionChanged += (_, _) => Rebuild(keep: true);
        Rebuild(keep: false);

        MailRule Read()
        {
            var r = rule.Clone();
            r.Name = name.Text;
            r.AccountId = accounts.ElementAtOrDefault(account.SelectedIndex).Id;
            r.Direction = Direction();
            r.Match = (RuleMatch)Math.Max(0, match.SelectedIndex);
            r.Conditions = conditionEditors.Select(f => f()).ToList();
            r.Actions = actionEditors.Select(f => f()).ToList();
            r.AllMessages = all.IsChecked == true && r.Conditions.Count == 0;
            r.StopProcessing = stop.IsChecked == true;
            r.Enabled = enabled.IsChecked == true;
            return r;
        }

        var footer = Dialogs.Footer(window);
        footer.Children.Add(Dialogs.Button("Test on selected message", async () =>
        {
            var candidate = Read();
            RuleValidator.Validate(candidate, id => model.TagBoard?.Find(id) is not null);
            if (model.SelectedMessage is not { } message)
            {
                result.Text = "Select a message in the list first.";
                return;
            }

            var facts = await model.FactsAsync(message);
            var truth = RuleEngine.Evaluate(candidate, facts);
            result.Text = truth switch
            {
                Truth.True => $"The rule matches “{message.Subject}”. Nothing was changed.",
                Truth.False => $"The rule doesn't match “{message.Subject}”.",
                _ => $"Can't tell for “{message.Subject}”: a condition needs the message text, which isn't downloaded.",
            } + string.Concat(candidate.Conditions.Select(c => $"\n• {RuleValidator.Describe(c.Field)} {RuleValidator.Describe(c.Operator)}: {Word(RuleEngine.Evaluate(c, facts))}"));
        }));
        footer.Children.Add(Dialogs.Cancel(window));
        footer.Children.Add(Dialogs.Primary("Save rule", async () =>
        {
            var edited = Read();
            if (edited.AllMessages && Dialogs.Alert(window, "This rule has no conditions and will act on every message. Continue?", "Every message", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            if (edited.Direction == RuleDirection.Incoming && edited.Actions.Any(a => a.Kind is RuleActionKind.MoveToJunk or RuleActionKind.MoveToTrash)
                && !rules.Rules.Any(r => r.Id == edited.Id && r.Actions.Any(a => a.Kind is RuleActionKind.MoveToJunk or RuleActionKind.MoveToTrash))
                && Dialogs.Alert(window, "Matching messages will be moved to Junk or Trash as they arrive. They can be found and restored there. Continue?", "Move messages away", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            try
            {
                await rules.SaveRuleAsync(edited);
                window.Close();
            }
            catch (RuleException ex)
            {
                result.Text = ex.Message;
            }
        }));
        panel.Children.Add(result);
        window.Loaded += (_, _) => name.Focus();
        window.ShowDialog();
    }

    private static string Word(Truth t) => t switch { Truth.True => "yes", Truth.False => "no", _ => "unknown" };

    /// <summary>A new, switched-off incoming rule prefilled from a message (sender and subject), for the user to edit.</summary>
    public static void RuleFromMessage(Window owner, MainViewModel model, StoredMessage message)
    {
        var from = MailMatch.Address(message.FromAddress) ?? message.FromAddress;
        var prefill = new MailRule
        {
            Name = "From " + from,
            AccountId = message.AccountId,
            Direction = RuleDirection.Incoming,
            Enabled = false,
            Conditions = [new RuleCondition { Field = RuleField.From, Operator = RuleOperator.Is, Value = from }],
            Actions = [new RuleAction { Kind = RuleActionKind.AddTag }],
        };
        Edit(owner, model, null, prefill);
    }

    // ------------------------------------------------------------------ activity

    public static void ShowActivity(Window? owner, MainViewModel model)
    {
        if (model.Rules is not { } rules)
            return;
        var window = Dialogs.Create("Rule activity", owner ?? Application.Current.MainWindow, 720, 560);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Note("What the rules did on this PC in the last 30 days. Message subjects aren't kept here; find the messages in Mail."));
        var list = new StackPanel();
        panel.Children.Add(list);
        window.Loaded += async (_, _) =>
        {
            var items = await rules.ActivityAsync();
            if (items.Count == 0)
                list.Children.Add(Dialogs.Note("Nothing yet."));
            foreach (var e in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock { Text = $"{e.CreatedUtc.ToLocalTime():g} · {AccountLabel(model, e.AccountId)} · {(e.Direction == RuleDirection.Incoming ? "received" : "sent")} · {Status(e.Status)}", Foreground = Muted });
                row.Children.Add(new TextBlock { Text = e.Summary, TextWrapping = TextWrapping.Wrap });
                list.Children.Add(row);
            }
        };
        Dialogs.Footer(window).Children.Add(Dialogs.Primary("Close", window.Close));
        window.ShowDialog();
    }

    private static string Status(string s) => s switch
    {
        "completed" => "done",
        "partial" => "partly done",
        "failed" => "failed",
        "unknown" => "not fully checked",
        _ => s,
    };

    // ------------------------------------------------------------------ blocked senders & words

    public static StackPanel BlockedPage(MainViewModel model, Func<Window?> owner)
    {
        var page = new StackPanel();
        page.Children.Add(Dialogs.Title("Blocked senders & words"));
        if (model.Rules is not { } rules)
        {
            page.Children.Add(Dialogs.Note("Blocking isn't available because the mail database couldn't be opened."));
            return page;
        }

        page.Children.Add(Dialogs.Note("New mail that matches is moved to the account's Junk folder, with the reason shown here, and ordinary rules skip it. Nothing is deleted or bounced, and the mail server's own spam filter keeps working. A sender address can be faked: this is personal filtering, not proof of who sent a message."));
        var on = new CheckBox { Content = "Use the blocklist", IsChecked = rules.BlocklistEnabled, Margin = new Thickness(0, 0, 0, 8) };
        on.Click += (_, _) =>
        {
            rules.BlocklistEnabled = on.IsChecked == true;
            model.Config.Reading.BlocklistOff = !rules.BlocklistEnabled;
            model.ConfigStoreSave();
        };
        page.Children.Add(on);

        var kinds = new[] { (BlockKind.Address, "Sender address"), (BlockKind.Domain, "Domain"), (BlockKind.SubjectText, "Words in the subject"), (BlockKind.BodyText, "Words in the message text") };
        var kind = new ComboBox { ItemsSource = kinds.Select(k => k.Item2).ToList(), SelectedIndex = 0, Width = 200, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(kind, "What to block");
        var value = new TextBox { Width = 240, MaxLength = RuleLimits.MaxOperand, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(value, "Address, domain, word or phrase");
        var broad = new CheckBox { Content = "Include subdomains", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        void UpdateBroad()
        {
            var k = kinds[Math.Max(0, kind.SelectedIndex)].Item1;
            broad.Visibility = k == BlockKind.Address ? Visibility.Collapsed : Visibility.Visible;
            broad.Content = k == BlockKind.Domain ? "Include subdomains" : "Also inside longer words";
        }

        kind.SelectionChanged += (_, _) => UpdateBroad();
        UpdateBroad();
        var scopes = new List<(string? Id, string Label)> { (null, "All accounts") };
        scopes.AddRange(model.Config.Accounts.Select(a => ((string?)a.Id, ComposeIdentity.Label(a))));
        var scope = new ComboBox { ItemsSource = scopes.Select(s => s.Label).ToList(), SelectedIndex = 0, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(scope, "Account");
        page.Children.Add(Row(kind, value, broad));
        page.Children.Add(Row(scope, Dialogs.Primary("Add", async () =>
        {
            var entry = new BlockEntry
            {
                Kind = kinds[Math.Max(0, kind.SelectedIndex)].Item1, Value = value.Text, AccountId = scopes.ElementAtOrDefault(scope.SelectedIndex).Id,
                Mode = broad.IsChecked == true ? BlockMode.Broad : BlockMode.Exact,
            };
            try
            {
                RuleValidator.Validate(entry);
                if (entry.Kind is BlockKind.SubjectText or BlockKind.BodyText && !await ConfirmPhraseAsync(owner(), model, entry))
                    return;
                await rules.SaveBlockAsync(entry);
                value.Text = "";
            }
            catch (RuleException ex)
            {
                Dialogs.Alert(owner(), ex.Message, "Blocked senders & words");
            }
        })));
        page.Children.Add(Dialogs.Note("Words match whole words and phrases, ignoring case. The message text includes quoted replies and signatures, so a common phrase can catch more than you expect."));

        var entries = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var quarantine = new StackPanel();
        page.Children.Add(entries);
        page.Children.Add(Dialogs.Subtitle("Blocked messages"));
        page.Children.Add(quarantine);

        async void Fill()
        {
            entries.Children.Clear();
            if (rules.Blocks.Count == 0)
                entries.Children.Add(Dialogs.Note("Nothing is blocked."));
            foreach (var entry in rules.Blocks)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var enabled = new CheckBox { IsChecked = entry.Enabled, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetName(enabled, "Use " + entry.Value);
                enabled.Click += async (_, _) =>
                {
                    entry.Enabled = enabled.IsChecked == true;
                    await rules.SaveBlockAsync(entry);
                };
                DockPanel.SetDock(enabled, Dock.Left);
                row.Children.Add(enabled);
                var delete = Dialogs.Danger("Delete", async () => await rules.DeleteBlockAsync(entry.Id));
                AutomationProperties.SetName(delete, "Delete " + entry.Value);
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);
                var text = new StackPanel();
                var label = MailRuleService.Reason(entry).Replace("Blocked ", "", StringComparison.Ordinal);
                text.Children.Add(new TextBlock { Text = char.ToUpperInvariant(label[0]) + label[1..], TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock
                {
                    Text = $"{AccountLabel(model, entry.AccountId)} · added {entry.CreatedUtc.ToLocalTime():d} · {(entry.HitCount == 1 ? "1 message" : $"{entry.HitCount} messages")} blocked"
                        + (entry.LastHitUtc is { } hit ? $", last {hit.ToLocalTime():g}" : ""),
                    Foreground = Muted,
                });
                row.Children.Add(text);
                entries.Children.Add(row);
            }

            quarantine.Children.Clear();
            var items = await rules.QuarantineAsync();
            if (items.Count == 0)
                quarantine.Children.Add(Dialogs.Note("No blocked messages."));
            foreach (var item in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock { Text = item.Reason, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = $"{AccountLabel(model, item.AccountId)} · {item.BlockedUtc.ToLocalTime():g} · in {(item.FolderId == item.OriginalFolderId ? "its folder (no Junk folder)" : "Junk")}", Foreground = Muted });
                Func<Core.Mail.RemoteMailCommand, Task> apply = command => model.ApplyRuleCommandAsync(item.AccountId, command);
                row.Children.Add(Dialogs.ButtonRow(
                    Dialogs.Button("Restore", () => rules.RestoreAsync(item, unblock: false, apply)),
                    Dialogs.Button("Unblock", () => rules.UnblockAsync(item)),
                    Dialogs.Button("Restore & unblock", () => rules.RestoreAsync(item, unblock: true, apply))));
                quarantine.Children.Add(row);
            }
        }

        Live(page, rules, Fill);
        return page;
    }

    /// <summary>Before a word or phrase is added: how many saved messages in the open folder it would have matched (nothing is changed).</summary>
    private static async Task<bool> ConfirmPhraseAsync(Window? owner, MainViewModel model, BlockEntry entry)
    {
        var hits = 0;
        var checkedCount = 0;
        foreach (var message in model.Messages.Take(500))
        {
            var facts = await model.FactsAsync(message);
            checkedCount++;
            if (RuleEngine.MatchBlock([entry], facts).Entry is not null)
                hits++;
        }

        if (hits == 0)
            return true;
        return Dialogs.Alert(owner, $"“{entry.Value}” appears in {hits} of the {checkedCount} messages shown now. Those messages stay where they are; only new mail is blocked. Add it?",
            "Blocked words", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    /// <summary>Block sender or Block domain from the message list (the domain asks first: it is broad).</summary>
    public static async Task BlockFromMessageAsync(Window owner, MainViewModel model, StoredMessage message, bool domain)
    {
        if (model.Rules is not { } rules)
            return;
        var address = MailMatch.Address(message.FromAddress);
        if (address is null)
        {
            Dialogs.Alert(owner, "This message has no sender address to block.", "Block");
            return;
        }

        var entry = new BlockEntry { Kind = domain ? BlockKind.Domain : BlockKind.Address, Value = domain ? MailMatch.DomainOf(address)! : address, AccountId = null };
        if (domain && Dialogs.Alert(owner, $"Block everything from {entry.Value}? All future mail from any address at this domain will go to Junk, in every account. Subdomains aren't included.",
                "Block domain", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            await rules.SaveBlockAsync(entry);
            Dialogs.Alert(owner, $"{(domain ? "Domain" : "Sender")} blocked: {entry.Value}. New mail from it goes to Junk. This message stays where it is. Undo in Settings → Blocked senders & words.", "Block");
        }
        catch (RuleException ex)
        {
            Dialogs.Alert(owner, ex.Message, "Block");
        }
    }

    // ------------------------------------------------------------------ before sending

    /// <summary>
    /// The outgoing rules, asked in the compose window: false keeps the draft open (blocked, or the user chose not to
    /// send after a warning). A confirmation is recorded on the mail and covers exactly this draft.
    /// </summary>
    public static bool ConfirmSend(Window owner, MailRuleService? rules, AccountConfig account, OutgoingMail mail)
    {
        if (rules is null)
            return true;
        OutgoingVerdict verdict;
        try
        {
            verdict = rules.CheckOutgoing(account, mail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Dialogs.Alert(owner, "Outgoing rules could not check this message, so it was not sent. Try again.", "Not sent");
            return false;
        }

        if (verdict.Blockers.Count > 0)
        {
            Dialogs.Alert(owner, string.Join("\n", verdict.Blockers.Select(b => $"The rule “{b.RuleName}” stops this message: {b.Text}")) + "\n\nThe draft is kept. Change the message, or change the rule in Settings → Mail rules.",
                "Not sent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (verdict.Warnings.Count == 0)
            return true;
        var ok = Dialogs.Alert(owner, string.Join("\n", verdict.Warnings.Select(w => $"“{w.RuleName}”: {w.Text}")) + "\n\nSend it anyway?", "Check before sending",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        if (ok)
            mail.RuleApproval = verdict.ApprovalKey;
        return ok;
    }
}
