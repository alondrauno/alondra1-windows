using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Shape = System.Windows.Shapes.Shape;
using Path = System.IO.Path;
using Alondra1.App.Shell;
using Alondra1.App.ViewModels;
using Alondra1.Core.Config;
using Alondra1.Core.Layout;
using Alondra1.Core.Mail;
using Alondra1.Core.Search;

namespace Alondra1.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _filling;
    private bool _syncSelection;
    private bool _syncingSearch;
    private Alondra1.App.Tasks.ReminderNotifier? _reminders;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        Title = Alondra1.Core.BuildEnvironment.DisplayName;
        if (Alondra1.Core.BuildEnvironment.IsDev)
            DevBadge.Visibility = Visibility.Visible;
        _model = model;
        DataContext = model;
        ShellWindow.Attach(this);
        // Accounts and folders are a sidebar (left, right) or hidden behind the folder button; no band (Mail UI spec §2.2).
        FolderDockBox.ItemsSource = new[] { nameof(DockEdge.Left), nameof(DockEdge.Right), nameof(DockEdge.Hidden) };
        model.NavRowsChanged += SelectNavRow;
        ReaderDockBox.ItemsSource = Enum.GetNames<DockEdge>();
        DensityBox.ItemsSource = Enum.GetNames<Density>();
        MailGrouping.SelectedIndex = model.Config.Appearance.GroupMailByAccount ? 1 : 0;
        SetGrouping(model.Messages, nameof(StoredMessage.AccountId), model.Config.Appearance.GroupMailByAccount);
        model.ComposeRequested += ShowCompose;
        model.SettingsRequested += () => Dialogs.ShowSettings(this, model);
        Loaded += async (_, _) => await model.Cloud.StartAsync();
        model.AccountRequested += account => Dialogs.ShowAccount(this, model, account);
        model.ConnectionChanged += UpdateStatusDot;
        model.Todo.SearchFocusRequested += FocusSearch;
        model.Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlobalSearchViewModel.IsPanelOpen) && model.Search.IsPanelOpen)
                SearchResultsPopup.PlacementTarget = model.CompactSearch ? SearchToggle : SearchArea;
        };
        Deactivated += (_, _) => model.Search.Close();
        LocationChanged += (_, _) => model.Search.Close();
        model.Todo.PropertyChanged += Todo_PropertyChanged;
        ApplyBackground();
        ReaderBody.HtmlFailed += reason => _model.ReportHtmlFailure(reason);
        model.MessageStateChanged += RedrawRow;
        Closing += SaveOpenNoteBeforeClosing;
        Loaded += async (_, _) =>
        {
            Trace("await model.InitializeAsync();");
            await model.InitializeAsync();
            Trace("await StartTasksAsync();");
            await StartTasksAsync();
            FillSelectors();
            UpdateStatusDot();
            Trace("ApplyReaderColors();");
            ApplyReaderColors();
            WriteLaunchLog();
            if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--open-eml=", StringComparison.Ordinal)) is { } eml)
                await OpenMessageFileAsync(eml["--open-eml=".Length..].Trim('"'));
            // Diagnostics: --account=<name> picks the Mail account; --sync syncs every account once at start (before a
            // --snapshot), as F5 would; --send-check sends between the first two manual accounts. Results go to logs\check.log.
            if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--account=", StringComparison.Ordinal)) is { } chosen)
            {
                _model.SetMailAccountByName(chosen["--account=".Length..]);
                _model.SetAccount(chosen["--account=".Length..]);
                FillSelectors();
            }

            if (App.Arguments.Contains("--sync") && !model.IsDemo)
            {
                await model.SyncAsync();
                UpdateStatusDot();
                var ok = model.Connection == ConnectionState.Ready;
                WriteCheck($"sync: {(ok ? "ok" : "failed")} · {model.ConnectionLabel} · {model.StatusText.Replace('\n', ';')} · {model.CountText}");
                if (!ok)
                    _exitCode = 4;
            }

            if (App.Arguments.Contains("--send-check") && !model.IsDemo)
            {
                var (ok, detail) = await model.RunSendCheckAsync();
                WriteCheck((ok ? "ok " : "failed ") + detail);
                if (!ok)
                    _exitCode = 5;
            }

            if (App.Arguments.Contains("--perf"))
                await RunPerfAsync();
            else if (App.Arguments.Contains("--exit") && !App.Arguments.Any(argument => argument.StartsWith("--snapshot=", StringComparison.Ordinal)))
                ExitAfterDiagnostics();
            else if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--snapshot=", StringComparison.Ordinal)) is { } snapshot)
                await SnapshotAsync(snapshot["--snapshot=".Length..]);
            else if (model.Config.Accounts.Count == 0 && !model.IsDemo && !App.Arguments.Contains("--demo"))
                Dialogs.ShowWelcome(this, model);
        };
    }

    /// <summary>Applies Settings → Appearance → Background to the window. Also used for live preview.</summary>
    public void ApplyBackground() => BackgroundService.Apply(_model.Config.Appearance.Background, BackgroundImage, BackgroundOverlay);

    private static void SetGrouping(System.Collections.IEnumerable items, string property, bool enabled)
    {
        var view = CollectionViewSource.GetDefaultView(items);
        using var refresh = view.DeferRefresh();
        view.GroupDescriptions.Clear();
        if (enabled)
            view.GroupDescriptions.Add(new PropertyGroupDescription(property));
    }

    private void MailGrouping_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || MailGrouping.SelectedIndex < 0)
            return;
        var enabled = MailGrouping.SelectedIndex == 1;
        _model.Config.Appearance.GroupMailByAccount = enabled;
        SetGrouping(_model.Messages, nameof(StoredMessage.AccountId), enabled);
        _model.ConfigStoreSave();
    }

    private bool _noteSavedForClose;

    /// <summary>
    /// Notes spec §8.2: closing the app saves the open note's latest edits first. The close is postponed until the
    /// editor's document is stored, then repeated (Close cannot be called from inside Closing).
    /// </summary>
    private async void SaveOpenNoteBeforeClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_noteSavedForClose || !_model.Notes.HasNote)
            return;
        e.Cancel = true;
        _noteSavedForClose = true;
        try
        {
            await _model.Notes.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Saving the open note on close failed: " + ex.Message);
        }

        _ = Dispatcher.BeginInvoke(Close);
    }

    private async Task StartTasksAsync()
    {
        var todo = _model.Todo;
        if (App.Services.Tasks is { } board && _model.IsDemo && board.Tasks.Count == 0)
            await Alondra1.App.Tasks.TodoSamples.SeedAsync(board);
        await todo.InitializeAsync();
        _reminders = new Alondra1.App.Tasks.ReminderNotifier(todo);
        _reminders.Start();
        Closed += (_, _) => _reminders?.Dispose();
        if (App.Services.TaskStore is { } store && !_model.IsDemo)
            _ = Alondra1.App.Tasks.TodoDataActions.AutomaticBackupAsync(store);
    }

    private void FillSelectors()
    {
        _filling = true;
        AccountBox.ItemsSource = _model.AccountChoices;
        AccountBox.SelectedItem = _model.SelectedAccount;
        SelectNavRow();
        NoteSortBox.SelectedIndex = (int)_model.Notes.Sort;
        FolderDockBox.SelectedItem = _model.FoldersDock.ToString();
        ReaderDockBox.SelectedItem = _model.ReadingDock.ToString();
        DensityBox.SelectedItem = _model.Config.Appearance.Density.ToString();
        SyncSearchBox();
        _filling = false;
        MessageList.ItemsSource = _model.Messages;
        MessageList.SelectedItem = _model.SelectedMessage;
        MessageList_SizeChanged(MessageList, null!);
    }

    private void UpdateStatusDot() => StatusDot.SetResourceReference(Shape.FillProperty, _model.ConnectionBrushKey);

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => _model.RememberShellWidth(e.NewSize.Width);

    private int _exitCode;

    /// <summary>Closes the app for --exit runs, with a non-zero exit code when a diagnostic check failed.</summary>
    private void ExitAfterDiagnostics()
    {
        if (_exitCode == 0)
            Close();
        else
            Application.Current.Shutdown(_exitCode);
    }

    /// <summary>Diagnostics (--trace): startup and check steps with timestamps in logs\trace.log, to find where a run stops.</summary>
    private static void Trace(string step)
    {
        if (!App.Arguments.Contains("--trace"))
            return;
        try
        {
            var directory = Path.Combine(App.Services.Home, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "trace.log"), $"{DateTimeOffset.Now:HH:mm:ss.fff} {step}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static void WriteCheck(string line)
    {
        try
        {
            var directory = Path.Combine(App.Services.Home, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "check.log"), $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void WriteLaunchLog()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Alondra1.Core.BuildEnvironment.DataFolder, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "launch.log"), $"{DateTimeOffset.Now:O} launch {App.Services.Launch.ElapsedMilliseconds} ms{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private async Task RunPerfAsync()
    {
        var batch = new PersonalBatch();
        for (var i = 0; i < 10000; i++)
        {
            batch.Upserts.Add(new StoredMessage
            {
                AccountId = "perf",
                AccountName = "Perf",
                FolderId = "Inbox",
                Uid = i + 1,
                Subject = "Indexed message " + i,
                FromName = "Sender " + (i % 50),
                FromAddress = "sender@example.com",
                ReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                TimeLabel = "09:00",
                Preview = "Body token " + (i % 100),
                BodyDownloaded = true,
                Availability = ItemAvailability.Downloaded,
            });
        }

        var started = Stopwatch.StartNew();
        await App.Services.Store.CommitAsync(batch, CancellationToken.None);
        var commitMs = started.ElapsedMilliseconds;
        started.Restart();
        var found = await App.Services.Store.SearchAsync("perf", "Inbox", "token 7", CancellationToken.None);
        var searchMs = started.ElapsedMilliseconds;
        _model.Messages.Clear();
        foreach (var message in await App.Services.Store.ListAsync("perf", "Inbox", CancellationToken.None))
            _model.Messages.Add(message);
        var scroll = FindScrollViewer(MessageList);
        var frames = 0;
        var watch = Stopwatch.StartNew();
        EventHandler handler = (_, _) => frames++;
        System.Windows.Media.CompositionTarget.Rendering += handler;
        if (scroll is not null)
        {
            while (watch.ElapsedMilliseconds < 800)
            {
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 240);
                await Task.Delay(16);
            }
        }

        System.Windows.Media.CompositionTarget.Rendering -= handler;
        var working = Process.GetCurrentProcess().WorkingSet64;
        var report = $"commit {commitMs} ms; search {found.Count} hits in {searchMs} ms; scroll frames {frames}; working set {working / (1024 * 1024)} MB";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Alondra1.Core.BuildEnvironment.DataFolder, "perf.txt");
        File.WriteAllText(path, report);
        if (App.Arguments.Contains("--exit"))
            Close();
    }

    /// <summary>
    /// Diagnostics: renders the window to a PNG (for UI review and visual checks), then closes when --exit is given.
    /// --open=&lt;module&gt; and --width=&lt;pixels&gt; pick what to capture. Popups are not part of the image.
    /// </summary>
    private async Task SnapshotAsync(string path)
    {
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--width=", StringComparison.Ordinal)) is { } width
            && double.TryParse(width["--width=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels))
            Width = pixels;
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--height=", StringComparison.Ordinal)) is { } height
            && double.TryParse(height["--height=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tall))
            Height = tall;
        // --scale=1.25|1.5|2: lay out and render at that display scale, as Windows would at 125/150/200 %.
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--scale=", StringComparison.Ordinal)) is { } scaleArgument
            && double.TryParse(scaleArgument["--scale=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale)
            && scale is >= 1 and <= 3)
        {
            System.Windows.Media.VisualTreeHelper.SetRootDpi(this, new DpiScale(scale, scale));
            UpdateLayout();
        }

        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--open=", StringComparison.Ordinal)) is { } open)
            OpenWorkspace(open["--open=".Length..]);
        // --day=<n>: the Calendar with that day of the shown month selected (its events and Meeting notes).
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--day=", StringComparison.Ordinal)) is { } day && int.TryParse(day["--day=".Length..], out var dayNumber))
            _model.SelectDay(_model.Days.FirstOrDefault(d => d.IsDay && d.Day == dayNumber));
        // --search-scope=Module|AllMail|All: the reach of the header search; --search=<text>: the search state.
        // --mail-filter=unread,flagged,attachments: list filters (for snapshots of the filtered and empty-filtered states).
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--mail-filter=", StringComparison.Ordinal)) is { } filterArg)
        {
            var parts = filterArg["--mail-filter=".Length..].Split(',');
            _model.SetFilter(new MailListFilter(parts.Contains("unread"), parts.Contains("flagged"), parts.Contains("attachments")));
            await Task.Delay(300);
        }

        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--search-scope=", StringComparison.Ordinal)) is { } reach)
            _model.Search.Choose(SearchScopes.ParseReach(reach["--search-scope=".Length..]));
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--search=", StringComparison.Ordinal)) is { } search)
            SearchBox.Text = search["--search=".Length..];
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--select=", StringComparison.Ordinal)) is { } select)
            _model.Todo.Select(select["--select=".Length..]);
            Trace("await Task.Delay(1500);");
        await Task.Delay(1500);
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--detail", StringComparison.Ordinal)) is not null && _model.Todo.Items.FirstOrDefault() is { } first)
        {
            _model.Todo.ShowDetail(first.Id);
            await Task.Delay(600);
        }

        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--dialog=", StringComparison.Ordinal)) is { } dialog)
        {
            Action show = dialog["--dialog=".Length..] switch
            {
                "compose" => () => Dialogs.ShowCompose(this, _model, null),
                "settings" => () => Dialogs.ShowSettings(this, _model),
                "background" => () => Dialogs.ShowSettings(this, _model, 1),
                "mail" => () => Dialogs.ShowSettings(this, _model, 2),
                "account" => () => Dialogs.ShowAccount(this, _model, null),
                "welcome" => () => Dialogs.ShowWelcome(this, _model),
                "conflict" => () => _ = Alondra1.App.Notes.NotesSamples.ShowConflictAsync(this, _model.Notes),
                "list" => () => Alondra1.App.Tasks.TodoDialogs.Prompt(this, "New list", "Name (emoji welcome)", "Untitled list"),
                "alert" => () => Dialogs.Alert(this, "Replace the account list with this file? Existing account modes stay as they are, and saved mail is not deleted.", "Alondra1", MessageBoxButton.OKCancel),
                _ => () => { },
            };
            Shell.Snapshot.CaptureDialog(this, show, path);
        }
        else
        {
            Trace("var overlays = new List<(Frame");
            var overlays = new List<(FrameworkElement, System.Windows.Media.Imaging.BitmapSource)>();
            // The message body is drawn by WebView2 outside WPF's own rendering; take it from the screen instead.
            if (_model.IsMail && ReaderBody.Surface is FrameworkElement surface && surface.IsVisible
                && (await ReaderBody.CaptureAsync() ?? Shell.Snapshot.CaptureScreen(surface)) is { } picture)
                overlays.Add((surface, picture));
            if (_model.IsNotes && NotesPanel.EditorSurface is { IsVisible: true } noteSurface
                && (await NotesPanel.CaptureEditorAsync() ?? Shell.Snapshot.CaptureScreen(noteSurface)) is { } notePicture)
                overlays.Add((noteSurface, notePicture));
            Trace("Shell.Snapshot.RenderElement((");
            Shell.Snapshot.RenderElement((FrameworkElement)Content, path, overlays);
            // The search results are a popup (its own window): written next to the snapshot as <name>.search.png.
            if (SearchResultsPopup.IsOpen && SearchResultsPopup.Child is FrameworkElement results)
                Shell.Snapshot.RenderElement(results, Path.ChangeExtension(path, ".search.png"));
            var geometry = Shell.GeometryCheck.Run(new Shell.GeometryCheck.Parts(this, Header, Toolbar, ContentArea,
                _model.IsMail && Dock.Single == Alondra1.Core.Layout.SinglePane.None ? ListPane : null, _model.IsMail && Dock.Single == Alondra1.Core.Layout.SinglePane.None ? ReaderPane : null,
                _model.IsMail && Dock.Single != Alondra1.Core.Layout.SinglePane.Reader ? ListColumnHeader : null, _model.IsMail ? MessageList : null));
            var displayScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            File.WriteAllText(path + ".txt", $"header {Header.ActualHeight:0} toolbar {Toolbar.ActualHeight:0} window {ActualWidth:0}x{ActualHeight:0} scale {displayScale:0.##}; {Shell.Snapshot.Measure(this)}"
                + $"{Environment.NewLine}account={_model.SelectedAccount}{Environment.NewLine}count={_model.CountText}{Environment.NewLine}connection={_model.ConnectionLabel}"
                + $"{Environment.NewLine}single={Dock.Single}{Environment.NewLine}" + string.Join(Environment.NewLine, geometry));
        }

        Trace("snapshot written");
        if (App.Arguments.Contains("--exit"))
            ExitAfterDiagnostics();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            var nested = FindScrollViewer(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    // ---------- Window chrome ----------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInteractive(source))
            return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private static bool IsInteractive(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or TextBox or ComboBox)
                return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        ShellWindow.ApplyCorners(this);
    }

    // ---------- Module navigation ----------

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string name })
            OpenWorkspace(name);
    }

    private void OpenWorkspace(string name)
    {
        _model.SelectWorkspace(name);
        LayoutButton.IsChecked = false;
        SearchToggle.IsChecked = false;
        FillSelectors();
    }

    private void NavMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = NavMoreButton.ContextMenu!;
        menu.Items.Clear();
        foreach (var workspace in _model.Workspaces.Where(item => item.InOverflow))
        {
            var item = new MenuItem { Header = workspace.Soon ? workspace.Name + " (soon)" : workspace.Name, Icon = new Alondra1.App.Controls.Alondra1Icon { Kind = workspace.Icon, IconSize = 16 } };
            var name = workspace.Name;
            item.Click += (_, _) => OpenWorkspace(name);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = NavMoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------- Contextual search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RouteSearch(SearchBox.Text, from: SearchBox);

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Coming back to the box shows the last results again (after opening one, or after Esc).
        if (_model.Search.UsesPanel && _model.Search.Rows.Count > 0 && _model.Search.Query == ((TextBox)sender).Text)
            _model.Search.IsPanelOpen = true;
    }

    private void CompactSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RouteSearch(CompactSearchBox.Text, from: CompactSearchBox);

    /// <summary>Sends the query to the module that owns it: mail search, task search, or contact filtering.</summary>
    private void RouteSearch(string text, TextBox from)
    {
        if (_filling || _syncingSearch)
            return;
        _syncingSearch = true;
        var other = from == SearchBox ? CompactSearchBox : SearchBox;
        if (other.Text != text)
            other.Text = text;
        _syncingSearch = false;
        if (_model.Search.UsesPanel)
        {
            // Results panel (All, All mail, Calendar): the module itself is never filtered behind the user's back.
            ClearModuleFilter();
            _model.Search.SetQuery(text);
        }
        else
        {
            _model.Search.SetQuery(text);
            FilterModule(text);
        }
    }

    private void FilterModule(string text)
    {
        if (_model.IsMail)
        {
            _model.ApplySearch(text);
            MessageList.SelectedItem = _model.SelectedMessage;
        }
        else if (_model.IsTasks)
            _model.Todo.ApplySearch(text);
        else if (_model.IsNotes)
            _model.Notes.ApplySearch(text);
        else if (_model.IsContacts)
            _model.ApplyContactQuery(text);
    }

    private void ClearModuleFilter()
    {
        if (!string.IsNullOrEmpty(_model.ModuleSearchText))
            FilterModule("");
    }

    private void SyncSearchBox()
    {
        _syncingSearch = true;
        var text = _model.Search.UsesPanel ? _model.Search.Query : _model.ModuleSearchText;
        SearchBox.Text = text;
        CompactSearchBox.Text = text;
        _syncingSearch = false;
    }

    /// <summary>The scope menu: this folder or module, all mail (in Mail), or all of Alondra1. The choice is remembered.</summary>
    private void SearchScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.Items.Clear();
        var search = _model.Search;
        void Add(string header, SearchReach reach)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = search.Reach == reach };
            item.Click += (_, _) => ChangeSearchReach(reach);
            menu.Items.Add(item);
        }

        Add(_model.IsMail ? $"This folder: {_model.ScopeTitle}" : $"Only {SearchScopes.ModuleName(search.Module)}", SearchReach.Module);
        if (_model.IsMail)
            Add("All mail (every account and folder on this PC)", SearchReach.AllMail);
        Add("All of Alondra1 (Mail, Calendar, To-do, Notes, Contacts)", SearchReach.All);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ChangeSearchReach(SearchReach reach)
    {
        var text = SearchBox.Text;
        if (reach != SearchReach.Module)
            ClearModuleFilter();
        _model.Search.Choose(reach);
        if (_model.Search.UsesPanel)
            _model.Search.SetQuery(text, now: true);
        else
            FilterModule(text);
        FocusSearch();
    }

    private void SearchResultsPopup_Closed(object? sender, EventArgs e) => _model.Search.Close();

    private void CloseSearchResults_Click(object sender, RoutedEventArgs e)
    {
        _model.Search.Close();
        SearchBox.Focus();
    }

    private void SearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResultsList.SelectedItem is SearchRow { Hit: not null } row)
        {
            _ = OpenSearchResultAsync(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _model.Search.Close();
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SearchResultsList.Items.Cast<SearchRow>().FirstOrDefault(r => !r.IsHeading) == SearchResultsList.SelectedItem)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SearchResults_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && SearchResultsList.SelectedItem is SearchRow { Hit: not null } row)
            _ = OpenSearchResultAsync(row);
    }

    /// <summary>Opens a result in its module. The query stays in the box, so the results come back on focus.</summary>
    private async Task OpenSearchResultAsync(SearchRow row)
    {
        var hit = row.Hit!;
        if (!await _model.OpenSearchHitAsync(hit))
        {
            _model.Search.Status = $"“{hit.Title}” is no longer available on this PC (deleted, moved, or its account was removed).";
            return;
        }

        _model.Search.Close();
        FillSelectors();
        if (hit.Module == SearchModule.Mail && _model.SelectedMessage is { } message)
        {
            MessageList.SelectedItem = message;
            MessageList.ScrollIntoView(message);
            MessageList.Focus();
        }
    }

    private void FocusFirstSearchResult()
    {
        if (!_model.Search.IsPanelOpen || SearchResultsList.Items.Cast<SearchRow>().FirstOrDefault(r => !r.IsHeading) is not { } first)
            return;
        SearchResultsList.SelectedItem = first;
        SearchResultsList.UpdateLayout();
        (SearchResultsList.ItemContainerGenerator.ContainerFromItem(first) as ListBoxItem)?.Focus();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A click outside the search area closes the results (the popup's own clicks never reach the window).
        if (_model.Search.IsPanelOpen && e.OriginalSource is DependencyObject source && !SearchArea.IsAncestorOf(source) && !SearchToggle.IsAncestorOf(source))
            _model.Search.Close();
    }

    private void Todo_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Alondra1.App.Tasks.TodoViewModel.SearchText) && _model.IsTasks && SearchBox.Text != _model.Todo.SearchText)
            SyncSearchBox();
    }

    private void FocusSearch()
    {
        if (!_model.HasSearch)
            return;
        if (_model.CompactSearch)
            SearchToggle.IsChecked = true;
        else
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter && _model.Search.UsesPanel)
        {
            _model.Search.SetQuery(box.Text, now: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && _model.Search.IsPanelOpen)
        {
            FocusFirstSearchResult();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        if (_model.Search.IsPanelOpen)
            _model.Search.Close();
        else if (box.Text.Length > 0)
            box.Clear();
        else
        {
            SearchToggle.IsChecked = false;
            Keyboard.ClearFocus();
        }
    }

    private void SearchToggle_Checked(object sender, RoutedEventArgs e) => SyncSearchBox();

    private void SearchPopup_Opened(object sender, EventArgs e) => Dispatcher.InvokeAsync(() => { CompactSearchBox.Focus(); CompactSearchBox.SelectAll(); });

    // ---------- Accounts and folders ----------

    /// <summary>The account of the Calendar and Contacts views (their toolbar selector). Mail uses the sidebar.</summary>
    private void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || AccountBox.SelectedItem is not string account)
            return;
        _model.SetAccount(account);
        UpdateStatusDot();
    }

    // ---------- Accounts and folders sidebar (Mail UI spec §3.1) ----------

    /// <summary>Selects the row of the current scope (after the rows were rebuilt), without opening anything.</summary>
    private void SelectNavRow()
    {
        _syncSelection = true;
        NavList.SelectedItem = _model.NavRows.FirstOrDefault(row => row.Scope == _model.Scope);
        _syncSelection = false;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncSelection || NavList.SelectedItem is not MailNavRow row)
            return;
        if (!row.CanSelect)
        {
            // Headings and path-only parents only expand and collapse; the selection stays on the current scope.
            SelectNavRow();
            return;
        }

        if (row.Scope == _model.Scope)
            return;
        _model.SetScope(row.Scope);
        MessageList.SelectedItem = _model.SelectedMessage;
    }

    private void NavExpander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MailNavRow row)
            _model.ToggleNav(row);
        e.Handled = true;
    }

    /// <summary>Tree keys on the flat list: Left collapses (or goes to the parent), Right expands, Enter moves to the messages.</summary>
    private void NavList_KeyDown(object sender, KeyEventArgs e)
    {
        if (NavList.SelectedItem is not MailNavRow row)
            return;
        var index = _model.NavRows.IndexOf(row);
        if (e.Key == Key.Left)
        {
            if (row.HasChildren && row.IsExpanded)
                _model.ToggleNav(row);
            else
            {
                var parent = _model.NavRows.Take(index).LastOrDefault(r => r.Node.Depth < row.Node.Depth);
                if (parent is not null)
                    FocusNavRow(parent);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Right && row.HasChildren)
        {
            if (!row.IsExpanded)
                _model.ToggleNav(row);
            else if (index + 1 < _model.NavRows.Count)
                FocusNavRow(_model.NavRows[index + 1]);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            MessageList.Focus();
            e.Handled = true;
        }
    }

    private void FocusNavRow(MailNavRow row)
    {
        NavList.SelectedItem = row;
        NavList.ScrollIntoView(row);
        (NavList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    /// <summary>Account rows: refresh, settings (reconnect, rename, folders kept here, remove), collapse.</summary>
    private void NavList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as MailNavRow ?? NavList.SelectedItem as MailNavRow;
        var menu = NavList.ContextMenu!;
        menu.Items.Clear();
        if (row is null)
        {
            e.Handled = true;
            return;
        }

        if (row.CanSelect)
            menu.Items.Add(MenuItem("Open", () => _model.SetScope(row.Scope)));
        if (row.IsAccount && !_model.IsDemo && _model.Config.Accounts.FirstOrDefault(a => a.Id == row.Node.AccountId) is { } account)
        {
            menu.Items.Add(MenuItem("Sync this account now", () => _ = _model.SyncAccountAsync(account.Id)));
            menu.Items.Add(MenuItem("Sync status…", () => Dialogs.Alert(this, $"{account.DisplayName} <{account.Address}>" + System.Environment.NewLine + System.Environment.NewLine
                + _model.SyncStatusOf(account).Describe(System.DateTimeOffset.UtcNow), "Sync status", MessageBoxButton.OK)));
            menu.Items.Add(MenuItem("Account settings…", () => Dialogs.ShowAccount(this, _model, account)));
        }

        if (row.HasChildren)
            menu.Items.Add(MenuItem(row.IsExpanded ? "Collapse" : "Expand", () => _model.ToggleNav(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Add or manage accounts…", () => _model.AccountsCommand.Execute(null)));
    }

    // ---------- Message list filters and sort ----------

    private void ListFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.Items.Clear();
        var f = _model.Filter;
        void Toggle(string header, bool on, MailListFilter next)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
            item.Click += (_, _) => _model.SetFilter(next);
            menu.Items.Add(item);
        }

        Toggle("Unread", f.Unread, f with { Unread = !f.Unread });
        Toggle("Flagged", f.Flagged, f with { Flagged = !f.Flagged });
        Toggle("Has attachments", f.Attachments, f with { Attachments = !f.Attachments });
        if (_model.Scope.IsUnified && _model.MailAccounts.Count > 1)
        {
            menu.Items.Add(new Separator());
            Toggle("All accounts", f.AccountId is null, f with { AccountId = null });
            foreach (var account in _model.MailAccounts)
                Toggle(ComposeIdentity.Label(account), f.AccountId == account.Id, f with { AccountId = account.Id });
        }

        menu.Items.Add(new Separator());
        var clear = MenuItem("Clear filters", () => _model.SetFilter(MailListFilter.None));
        clear.IsEnabled = f.IsActive;
        menu.Items.Add(clear);
        menu.Items.Add(new MenuItem { Header = "Filters apply to messages kept on this PC.", IsEnabled = false });
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ListSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.Items.Clear();
        foreach (var (field, desc) in new[] { (MailSortField.Date, true), (MailSortField.Date, false), (MailSortField.From, false), (MailSortField.From, true), (MailSortField.Subject, false), (MailSortField.Subject, true) })
        {
            var item = new MenuItem { Header = MailListView.SortLabel(field, desc), IsCheckable = true, IsChecked = _model.Sort == field && _model.SortDescending == desc };
            item.Click += (_, _) => _model.SetSort(field, desc);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ColumnSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MailSortField>(tag, out var field))
            _model.SetSort(field);
    }

    private void RemoveFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MailFilterChip chip })
            _model.SetFilter(chip.Without);
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e) => _model.SetFilter(MailListFilter.None);

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Shows or hides the accounts and folders sidebar (Hidden is remembered like the other layout choices).</summary>
    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _model.SetDock(folders: true, _model.FoldersHidden ? DockEdge.Left : DockEdge.Hidden);
        _filling = true;
        FolderDockBox.SelectedItem = _model.FoldersDock.ToString();
        _filling = false;
        if (!_model.FoldersHidden)
            Dispatcher.InvokeAsync(() => (NavList.ItemContainerGenerator.ContainerFromItem(NavList.SelectedItem) as ListBoxItem)?.Focus());
    }

    private void FolderDockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || FolderDockBox.SelectedItem is not string value || !Enum.TryParse<DockEdge>(value, out var edge))
            return;
        _model.SetDock(folders: true, edge);
    }

    private void ReaderDockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || ReaderDockBox.SelectedItem is not string value || !Enum.TryParse<DockEdge>(value, out var edge))
            return;
        _model.SetDock(folders: false, edge);
    }

    private void DensityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || DensityBox.SelectedItem is not string value || !Enum.TryParse<Density>(value, out var density))
            return;
        _model.SetDensity(density);
        MessageList.Items.Refresh();
        MessageList_SizeChanged(MessageList, null!);
    }

    private void Dock_BreadthCommitted(object sender, BreadthChangedEventArgs e) => _model.CommitBreadth(e.PaneId, e.Breadth);

    // ---------- Mail ----------

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
            return;
        _model.SetSelection(MessageList.SelectedItems.OfType<StoredMessage>().ToList());
        _model.SelectMessage(MessageList.SelectedItem as StoredMessage);
        if (MessageList.SelectedItem is null && _readerOpen)
            ShowList();
    }

    // ---------- One pane at a time ----------

    private bool _readerOpen;
    private double _listOffset;

    private void Dock_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSinglePane();

    /// <summary>Shows list and reader side by side when both fit (420 / 440 px), otherwise one at a time.</summary>
    private void UpdateSinglePane()
    {
        var fits = Alondra1.Core.Layout.LayoutArranger.FitsSplit(_model.PaneFoldersDock, _model.ReadingDock, Dock.ActualWidth, Dock.ActualHeight, _model.FolderBreadth);
        var single = fits ? Alondra1.Core.Layout.SinglePane.None
            : _readerOpen && _model.SelectedMessage is not null ? Alondra1.Core.Layout.SinglePane.Reader : Alondra1.Core.Layout.SinglePane.List;
        if (Dock.Single != single)
        {
            if (single == Alondra1.Core.Layout.SinglePane.Reader)
                _listOffset = FindScrollViewer(MessageList)?.VerticalOffset ?? 0;
            Dock.Single = single;
            if (single == Alondra1.Core.Layout.SinglePane.List && _listOffset > 0)
                Dispatcher.InvokeAsync(() => FindScrollViewer(MessageList)?.ScrollToVerticalOffset(_listOffset), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        _model.SetSinglePane(!fits, single == Alondra1.Core.Layout.SinglePane.Reader);
    }

    private void ShowReader()
    {
        if (_model.SelectedMessage is null)
            return;
        _readerOpen = true;
        UpdateSinglePane();
        if (_model.IsSinglePane)
            Dispatcher.InvokeAsync(() => ReaderBackButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowList()
    {
        _readerOpen = false;
        UpdateSinglePane();
        if (MessageList.ItemContainerGenerator.ContainerFromItem(MessageList.SelectedItem) is ListBoxItem item)
            Dispatcher.InvokeAsync(() => item.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ReaderBack_Click(object sender, RoutedEventArgs e) => ShowList();

    /// <summary>Clicking a row opens it; in one-pane mode that switches to the message.</summary>
    private void MessageList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source)
            return;
        for (var node = source; node is not null && node != MessageList; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem)
            {
                Dispatcher.InvokeAsync(ShowReader, System.Windows.Threading.DispatcherPriority.Input);
                return;
            }
        }
    }

    private void MessageList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            ShowReader();
            e.Handled = true;
        }
    }

    private void MessageList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewer = FindScrollViewer(MessageList);
        var height = viewer?.ViewportHeight ?? MessageList.ActualHeight;
        _model.UpdateViewport(MessageList.ActualWidth, height > 1 ? height : MessageList.ActualHeight);
        AlignColumnHeader();
    }

    private void MessageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            AlignColumnHeader();
    }

    /// <summary>
    /// Rows lose the width of the vertical scroll bar when it shows; give the column header the same right inset so
    /// From / Subject / Received stay over their columns.
    /// </summary>
    private void AlignColumnHeader()
    {
        var bar = FindScrollViewer(MessageList)?.ComputedVerticalScrollBarVisibility == Visibility.Visible
            ? FindVerticalScrollBar(MessageList)?.ActualWidth ?? 0
            : 0;
        // With a scroll bar, rows keep 6 px of breathing room before it so it never touches the dates.
        var breathing = bar > 0 ? ScrollBarBreathing : 0;
        if (Math.Abs(MessageList.Padding.Right - breathing) > 0.1)
            MessageList.Padding = new Thickness(0, 0, breathing, 0);
        if (ListColumnHeader.Child is FrameworkElement columns && Math.Abs(columns.Margin.Right - (bar + breathing)) > 0.1)
            columns.Margin = new Thickness(0, 0, bar + breathing, 0);
    }

    private const double ScrollBarBreathing = 6;

    private static System.Windows.Controls.Primitives.ScrollBar? FindVerticalScrollBar(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Primitives.ScrollBar { Orientation: Orientation.Vertical } bar)
                return bar;
            if (FindVerticalScrollBar(child) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Less common mail actions, plus the actions hidden from the toolbar when the window is narrow.</summary>
    private void MailMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = MailMoreButton.ContextMenu!;
        menu.Items.Clear();
        if (!_model.ShowSecondaryMailActions)
        {
            menu.Items.Add(Command("Reply all", "ReplyAll", _model.ReplyAllCommand));
            menu.Items.Add(Command("Forward", "Forward", _model.ForwardCommand, "Ctrl+Shift+F"));
            menu.Items.Add(Command("Archive", "Archive", _model.ArchiveCommand));
            menu.Items.Add(Command(_model.ReadToggleLabel, _model.ReadToggleIcon, _model.ToggleReadCommand));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Command("Sync now", "Refresh", _model.SyncCommand, "F5"));
        if (_model.SelectedMessage is not null && _model.Notes.IsAvailable)
        {
            menu.Items.Add(Command("Create note from this email", "Notes", null, action: () => _ = _model.CreateNoteFromMailAsync(withPictures: false)));
            if (_model.Attachments.Any(a => a.IsPicture))
                menu.Items.Add(Command("Create note with its pictures", "Image", null, action: () => _ = _model.CreateNoteFromMailAsync(withPictures: true)));
        }

        if (_model.Cloud.Available && _model.SelectedMessage is not null && !_model.IsDemo)
            menu.Items.Add(Command("Archive to Alondra1 Cloud", "Upload", null, action: () => _ = _model.ArchiveToCloudAsync()));
        menu.Items.Add(_model.ShowingHtml
            ? Command("Show plain text", "Notes", _model.ShowPlainCommand)
            : Command("Show HTML", "Mail", _model.ShowHtmlCommand));
        menu.PlacementTarget = MailMoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem Command(string header, string icon, ICommand? command, string? gesture = null, Action? action = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            InputGestureText = gesture ?? "",
            Icon = new Alondra1.App.Controls.Alondra1Icon { Kind = icon, IconSize = 16 },
        };
        if (action is not null)
            item.Click += (_, _) => action();
        return item;
    }

    private void NoteSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && NoteSortBox.SelectedIndex >= 0 && NoteSortBox.SelectedIndex != (int)_model.Notes.Sort)
            _model.Notes.SetSort(NoteSortBox.SelectedIndex);
    }

    /// <summary>After Settings → Mail changed density or pane positions: sync View settings and redraw the list.</summary>
    public void RefreshLayoutSelectors()
    {
        FillSelectors();
        MessageList.Items.Refresh();
        MessageList_SizeChanged(MessageList, null!);
    }

    /// <summary>Re-applies a row's template so a changed read state shows (messages are plain data objects).</summary>
    private void RedrawRow(StoredMessage message)
    {
        if (MessageList.ItemContainerGenerator.ContainerFromItem(message) is ListBoxItem item)
        {
            var content = item.Content;
            item.Content = null;
            item.Content = content;
        }
    }

    // ---------- Reader ----------

    /// <summary>Plain-text pages follow the theme: transparent over the pane, theme text and link colors.</summary>
    private void ApplyReaderColors()
    {
        static string Hex(object? brush) => brush is System.Windows.Media.SolidColorBrush solid
            ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : "#808080";
        _model.PlainColors = new Alondra1.Core.Html.MailHtml.PlainColors("transparent", Hex(TryFindResource("TextBrush")), Hex(TryFindResource("BlueBrush")));
        _model.RenderReader();
    }

    private async void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AttachmentRow row })
            return;
        if (Alondra1.Core.Mail.ReaderPolicy.IsRisky(row.FileName)
            && Dialogs.Alert(this, $"“{row.FileName}” can run programs on this PC. Open it only if you trust the sender.\n\nOpen it anyway?", "Alondra1", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            // Opened from a private temporary folder; the file keeps its own name so Windows picks the right app.
            var folder = Path.Combine(Path.GetTempPath(), "alondra1-open", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeFileName(row.FileName));
            await using (var file = File.Create(path))
                await _model.SaveAttachmentAsync(row, file);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or MailKit.ProtocolException or MailKit.Security.AuthenticationException)
        {
            Dialogs.Alert(this, $"Could not open “{row.FileName}”. {ex.Message}", "Alondra1");
        }
    }

    private async void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AttachmentRow row })
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = SafeFileName(row.FileName), Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            await using var file = File.Create(dialog.FileName);
            await _model.SaveAttachmentAsync(row, file);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Net.Sockets.SocketException or MailKit.ProtocolException or MailKit.Security.AuthenticationException)
        {
            Dialogs.Alert(this, $"Could not save “{row.FileName}”. {ex.Message}", "Alondra1");
        }
    }

    private static string SafeFileName(string name)
    {
        var cleaned = new string(Path.GetFileName(name).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "attachment" : cleaned;
    }

    /// <summary>
    /// Diagnostics (--open-eml=&lt;file&gt;): shows a message file through the normal reader pipeline and logs what the page
    /// attempted (title changes from scripts, blocked requests and navigations) to logs\check.log. Exit code 6 when
    /// active content ran or a request escaped the image policy.
    /// </summary>
    private async Task OpenMessageFileAsync(string path)
    {
        OpenWorkspace("Mail");
        try
        {
            Trace("_model.ShowMessageFile(path);");
            _model.ShowMessageFile(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            WriteCheck($"reader: {Path.GetFileName(path)} could not be read ({ex.GetType().Name})");
            _exitCode = 6;
            return;
        }

        if (App.Arguments.Contains("--load-images"))
            _model.LoadImagesCommand.Execute(null);
        MessageList.SelectedItem = _model.SelectedMessage;
        ShowReader();
            Trace("var loaded = await ReaderBody.");
        var loaded = await ReaderBody.WaitForDocumentAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(1500); // let image requests and any (blocked) script effects happen
            Trace("var title = ReaderBody.Documen");
        var title = ReaderBody.DocumentTitle;
        var ranScript = title.Contains("pwned", StringComparison.OrdinalIgnoreCase);
        var escaped = !_model.ReaderAllowRemote && ReaderBody.RemoteRequests > 0;
        WriteCheck($"reader: {Path.GetFileName(path)} loaded={loaded} html={_model.ShowingHtml} blockedImages={_model.BlockedImages} "
            + $"remoteRequests={ReaderBody.RemoteRequests} blockedRequests={ReaderBody.BlockedRequests} blockedNavigations={ReaderBody.BlockedNavigations} "
            + $"attachments={_model.Attachments.Count} scriptRan={ranScript}");
        if (ranScript || escaped || !loaded)
            _exitCode = 6;
    }

    // ---------- Keyboard ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+K: the shared search, from any module (Ctrl+E / Ctrl+F keep searching the module too).
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            FocusSearch();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key < Key.D1 + _model.Workspaces.Count)
        {
            OpenWorkspace(_model.Workspaces[e.Key - Key.D1].Name);
            e.Handled = true;
            return;
        }

        // In To-do, task shortcuts apply and mail shortcuts (reply, forward, delete message) do not.
        if (_model.IsTasks)
        {
            if (TodoPanel.HandleKey(e))
                e.Handled = true;
            return;
        }

        if (_model.IsNotes)
        {
            if (NotesPanel.HandleKey(e))
                e.Handled = true;
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.E or Key.F)
            {
                FocusSearch();
                e.Handled = true;
            }

            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.E or Key.F && _model.HasSearch)
        {
            FocusSearch();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is >= Key.D1 and <= Key.D6)
        {
            // Inbox, Sent, Drafts, Archive, Trash, Junk of the account in view (or of All accounts).
            _model.SetFolder(Alondra1.Core.Mail.MailNavigation.StandardRoles[e.Key - Key.D1]);
            FillSelectors();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            _model.NewMailCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && !IsTyping())
        {
            _model.ReplyCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F && !IsTyping())
        {
            _model.ForwardCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _model.SyncCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _model.ShowBackButton && !IsTyping())
        {
            ShowList();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && MessageList.IsKeyboardFocusWithin)
        {
            _model.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ShowCompose(OutgoingMail? mail) => Dialogs.ShowCompose(this, _model, mail);

    /// <summary>Mail shortcuts never act while the user is typing in a text box (search, note, task title).</summary>
    private static bool IsTyping() => Keyboard.FocusedElement is TextBox or PasswordBox or System.Windows.Controls.Primitives.TextBoxBase;
}
