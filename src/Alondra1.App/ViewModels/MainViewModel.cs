using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alondra1.Core.Config;
using Alondra1.Core.Demo;
using Alondra1.Core.Layout;
using Alondra1.Core.Mail;
using Alondra1.Core.Organizer;
using Alondra1.Core.Ports;
using Alondra1.Core.Search;
using Alondra1.Core.Sync;
using Alondra1.Persistence;

namespace Alondra1.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private List<CalendarEntry> _events = [];
    private List<NoteEntry> _notes = [];
    private List<ContactEntry> _contacts = [];
    private string _eventsCoverage = "Calendar events aren't loaded on this PC yet. Open Calendar to load them, then search again.";
    private string _contactsCoverage = "Contacts aren't loaded on this PC yet. Open Contacts to load them, then search again.";
    private DateTime _month = new(2026, 11, 1);
    private int _chosenDay = 15;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Workspaces =
        [
            new WorkspaceChoice("Mail", "\uE715", false, "Mail", "Search mail"),
            new WorkspaceChoice("Calendar", "\uE787", false, "Calendar", "Search calendar"),
            new WorkspaceChoice("To-do", "\uE9D5", false, "Todo", "Search tasks"),
            new WorkspaceChoice("Notes", "\uE70B", false, "Notes", "Search notes"),
            new WorkspaceChoice("Contacts", "\uE77B", false, "Contacts", "Search contacts"),
        ];
        // Chat is not offered (Mail UI spec \u00A72.1): no tab, shortcut or search entry. Its code and data are kept.
        SelectWorkspaceCommand = new RelayCommand<string>(SelectWorkspace);
        NewMailCommand = new RelayCommand(() => ComposeRequested?.Invoke(new OutgoingMail
        {
            AccountId = ComposeIdentity.DefaultAccount(MailAccounts, Scope, null, _services.Config.Reading.DefaultSendAccountId),
        }));
        ReplyCommand = new RelayCommand(() => ComposeRequested?.Invoke(BuildReply(all: false)), () => SelectedMessage is not null);
        ReplyAllCommand = new RelayCommand(() => ComposeRequested?.Invoke(BuildReply(all: true)), () => SelectedMessage is not null);
        ForwardCommand = new RelayCommand(() => ComposeRequested?.Invoke(BuildForward()), () => SelectedMessage is not null);
        DeleteCommand = new RelayCommand(async () => await DeleteAsync(), () => SelectedMessage is not null);
        ArchiveCommand = new RelayCommand(async () => await MoveSpecialAsync("Archive"), () => SelectedMessage is not null);
        ToggleReadCommand = new RelayCommand(async () => await ToggleReadAsync(), () => SelectedMessage is not null);
        // Always enabled so the button keeps showing its status while a sync runs; SyncAsync ignores a second press.
        SyncCommand = new RelayCommand(async () => await SyncAsync());
        PreviousMonthCommand = new RelayCommand(() => ShiftMonth(-1));
        NextMonthCommand = new RelayCommand(() => ShiftMonth(1));
        SelectDayCommand = new RelayCommand<CalendarDay>(SelectDay);
        MeetingNotesCommand = new AsyncRelayCommand<AgendaEvent>(OpenMeetingNotesAsync);
        ToggleTaskCommand = new RelayCommand<TaskRow>(async row => await ToggleTaskAsync(row));
        AddTaskCommand = new RelayCommand(async () => await AddTaskAsync());
        SaveNoteCommand = new RelayCommand(async () => await SaveNoteAsync());
        NewNoteCommand = new RelayCommand(async () => await NewNoteAsync());
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        AccountsCommand = new RelayCommand(() => AccountRequested?.Invoke(null));
        Cloud = new Alondra1.App.Cloud.CloudController(services);
        Todo = new Alondra1.App.Tasks.TodoViewModel(services, this);
        Notes = new Alondra1.App.Notes.NotesViewModel(services, this);
        Search = new GlobalSearchViewModel(CreateSearchSource, () => ScopeTitle,
            SearchScopes.ParseReach(services.Config.Layout.SearchReach), reach =>
            {
                if (IsDemo)
                    return;
                _services.Config.Layout.SearchReach = reach.ToString();
                SaveConfig();
            });
        Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlobalSearchViewModel.Placeholder))
                OnPropertyChanged(nameof(SearchPlaceholder));
        };
    }

    /// <summary>The shared header search (Mail UI spec §8).</summary>
    public GlobalSearchViewModel Search { get; }

    public ObservableCollection<WorkspaceChoice> Workspaces { get; }
    public ObservableCollection<string> AccountChoices { get; } = [];
    public ObservableCollection<string> Folders { get; } = [];
    public BulkObservableCollection<StoredMessage> Messages { get; } = [];
    public ObservableCollection<CalendarDay> Days { get; } = [];
    public ObservableCollection<TaskRow> Tasks { get; } = [];
    public ObservableCollection<string> NoteTitles { get; } = [];
    public ObservableCollection<ContactEntry> VisibleContacts { get; } = [];

    public IRelayCommand<string> SelectWorkspaceCommand { get; }
    public IRelayCommand NewMailCommand { get; }
    public IRelayCommand ReplyCommand { get; }
    public IRelayCommand ReplyAllCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand ArchiveCommand { get; }
    public IRelayCommand ToggleReadCommand { get; }
    public IRelayCommand SyncCommand { get; }
    public IRelayCommand PreviousMonthCommand { get; }
    public IRelayCommand NextMonthCommand { get; }
    public IRelayCommand<CalendarDay> SelectDayCommand { get; }
    public IAsyncRelayCommand<AgendaEvent> MeetingNotesCommand { get; }
    /// <summary>The selected day's events (Calendar), each with its Meeting notes action.</summary>
    public ObservableCollection<AgendaEvent> AgendaEvents { get; } = [];
    public IRelayCommand<TaskRow> ToggleTaskCommand { get; }
    public IRelayCommand AddTaskCommand { get; }
    public IRelayCommand SaveNoteCommand { get; }
    public IRelayCommand NewNoteCommand { get; }
    public IRelayCommand SettingsCommand { get; }
    public IRelayCommand AccountsCommand { get; }

    public event Action<OutgoingMail?>? ComposeRequested;
    public event Action? SettingsRequested;

    /// <summary>The local rich-text Notes module.</summary>
    public Alondra1.App.Notes.NotesViewModel Notes { get; }

    /// <summary>Optional Alondra1 account, Cloud Sync and Mail Archive.</summary>
    public Alondra1.App.Cloud.CloudController Cloud { get; }

    /// <summary>Copies the selected message to the paid cloud archive; the result is shown in the reader status line.</summary>
    public async Task ArchiveToCloudAsync()
    {
        if (SelectedMessage is not { } message)
            return;
        ReaderStatus = "Copying to the cloud archive…";
        RaiseReader();
        try
        {
            ReaderStatus = await Cloud.ArchiveMessageAsync(message).ConfigureAwait(true);
        }
        catch (Alondra1.Core.Cloud.CloudApiException ex)
        {
            ReaderStatus = "Not archived: " + ex.Message;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException or MailKit.ServiceNotConnectedException or MailKit.Security.AuthenticationException or MailKit.ProtocolException)
        {
            ReaderStatus = "Not archived: the message could not be read from the mail server (" + ex.GetType().Name + ").";
        }

        RaiseReader();
    }
    public event Action<AccountConfig?>? AccountRequested;

    public string WorkspaceName { get; private set; } = "Mail";
    public string WorkspaceGlyph { get; private set; } = "\uE715";
    public bool IsMail { get; private set; } = true;
    public bool IsCalendar { get; private set; }
    public bool IsTasks { get; private set; }
    public bool IsNotes { get; private set; }
    public bool IsContacts { get; private set; }
    public bool IsDemo => _services.Config.DemoMode && _services.Config.Accounts.Count == 0;
    public string DemoBadge => IsDemo ? "Demo" : "";
    public string StatusText { get; private set; } = "";
    public string SearchText { get; set; } = "";
    /// <summary>The account of the Calendar and Contacts views (display name, or All accounts). Mail uses <see cref="Scope"/>.</summary>
    public string SelectedAccount { get; private set; } = "All accounts";

    // ---------- Mail scope and sidebar (Mail UI spec §3) ----------

    /// <summary>What Mail shows: a unified folder, a standard folder of one account, or one custom folder (stable ids).</summary>
    public MailScope Scope { get; private set; } = MailScope.UnifiedInbox;

    /// <summary>The visible sidebar rows: All accounts with its unified folders, then every account and its folders.</summary>
    public ObservableCollection<MailNavRow> NavRows { get; } = [];
    private IReadOnlyList<MailNavNode> _nav = [];
    private bool _scopeNotSynced;

    /// <summary>Grouping by account only applies to a unified view.</summary>
    public bool IsAllAccounts => Scope.IsUnified;

    /// <summary>The accounts Mail works with: the configured accounts, or the two sample accounts in demo mode.</summary>
    public IReadOnlyList<AccountConfig> MailAccounts => IsDemo ? SampleCatalog.Accounts() : _services.Config.Accounts;

    private AccountConfig? ScopeAccount => Scope.AccountId is null ? null : MailAccounts.FirstOrDefault(a => a.Id == Scope.AccountId);

    /// <summary>The folder part of the scope: "Inbox", or a custom folder's name.</summary>
    public string SelectedFolder => Scope.Role != FolderRole.Other ? MailScope.RoleLabel(Scope.Role)
        : ScopeAccount?.Folders.Known.FirstOrDefault(f => f.Id == Scope.FolderId)?.Name ?? Scope.FolderId ?? "";

    /// <summary>The account part of the scope: "All accounts" or the account's name.</summary>
    public string ScopeAccountLabel => Scope.IsUnified ? "All accounts" : ScopeAccount is { } a ? (string.IsNullOrWhiteSpace(a.DisplayName) ? a.Address : a.DisplayName) : "";

    /// <summary>List header: "Inbox · All accounts", "Sent · Work".</summary>
    public string ScopeTitle => $"{SelectedFolder} · {ScopeAccountLabel}";

    /// <summary>Selects what Mail shows; an account or folder that no longer exists falls back to the unified Inbox, visibly.</summary>
    public void SetScope(MailScope? scope)
    {
        ApplyScope(scope);
        _ = ReloadMessagesAsync();
    }

    private void ApplyScope(MailScope? scope)
    {
        scope ??= MailScope.UnifiedInbox;
        if (!scope.IsUnified && MailAccounts.All(a => a.Id != scope.AccountId))
            scope = MailScope.UnifiedInbox;
        Scope = scope;
        if (!scope.IsUnified && Filter.AccountId is not null)
        {
            Filter = Filter with { AccountId = null };
            FilterChips.Clear();
            foreach (var chip in MailListView.Chips(Filter, id => id))
                FilterChips.Add(chip);
            OnPropertyChanged(nameof(Filter));
            OnPropertyChanged(nameof(HasFilter));
        }

        if (!IsDemo && _services.Config.Layout.MailScope != scope.Key)
        {
            _services.Config.Layout.MailScope = scope.Key;
            SaveConfig();
        }

        foreach (var row in NavRows)
            row.IsSelected = row.Scope == Scope;
        RefreshColumns();
        RaiseScope();
    }

    /// <summary>A standard folder in the current account part of the scope (keyboard shortcuts Ctrl+Shift+1…6).</summary>
    public void SetFolder(FolderRole role) => SetScope(new MailScope(Scope.AccountId, role));

    public void ToggleNav(MailNavRow row)
    {
        if (!row.HasChildren)
            return;
        row.IsExpanded = !row.IsExpanded;
        var collapsed = _services.Config.Layout.CollapsedNav;
        collapsed.Remove(row.Key);
        if (!row.IsExpanded)
            collapsed.Add(row.Key);
        if (!IsDemo)
            SaveConfig();
        RebuildNavRows();
    }

    /// <summary>Recounts unread messages and rebuilds the sidebar (after a sync, a read change, a move).</summary>
    public async Task RefreshNavigationAsync()
    {
        IReadOnlyList<FolderCount> counts;
        if (IsDemo)
            counts = SampleCatalog.Counts();
        else
            counts = [.. await _services.Store.CountByFolderAsync(CancellationToken.None).ConfigureAwait(true), .. _services.Cache.Counts()];
        _nav = MailNavigation.Build(MailAccounts, counts);
        RebuildNavRows();
    }

    private void RebuildNavRows()
    {
        var collapsed = new HashSet<string>(_services.Config.Layout.CollapsedNav, StringComparer.Ordinal);
        var rows = new List<MailNavRow>();
        void Add(IEnumerable<MailNavNode> nodes)
        {
            foreach (var node in nodes)
            {
                var row = new MailNavRow(node, expanded: true);
                row.IsExpanded = !collapsed.Contains(row.Key);
                row.IsSelected = row.Scope == Scope;
                rows.Add(row);
                if (row.HasChildren && row.IsExpanded)
                    Add(node.Children);
            }
        }

        Add(_nav);
        NavRows.Clear();
        foreach (var row in rows)
            NavRows.Add(row);
        if (!IsDemo)
            ApplySyncBadges();
        NavRowsChanged?.Invoke();
    }

    /// <summary>Raised after the sidebar rows were rebuilt, so the view can restore the selected row.</summary>
    public event Action? NavRowsChanged;

    /// <summary>The (account, folder id) pairs the current scope reads, with each account's real folder id.</summary>
    private List<(AccountConfig Account, string FolderId)> FoldersInScope()
    {
        if (Scope.IsUnified)
        {
            return MailAccounts.Select(a => (a, SpecialFolders.TryResolve(a, Scope.Role)))
                .Where(pair => pair.Item2 is not null).Select(pair => (pair.a, pair.Item2!)).ToList();
        }

        if (ScopeAccount is not { } account)
            return [];
        var id = Scope.Role == FolderRole.Other ? Scope.FolderId : SpecialFolders.TryResolve(account, Scope.Role);
        return id is null ? [] : [(account, id)];
    }

    /// <summary>
    /// The list's group headings: by date (only while sorted by date, so headings never repeat), by account (only in a
    /// view of several accounts), or none. The window applies it to the list view.
    /// </summary>
    public string? GroupProperty =>
        _services.Config.Appearance.GroupMailByDate && Sort == MailSortField.Date ? nameof(StoredMessage.DateGroup)
        : _services.Config.Appearance.GroupMailByAccount && Scope.IsUnified ? nameof(StoredMessage.AccountName)
        : null;

    /// <summary>0 together, 1 by account, 2 by date (Settings in the layout menu).</summary>
    public int GroupingChoice => _services.Config.Appearance.GroupMailByDate ? 2 : _services.Config.Appearance.GroupMailByAccount ? 1 : 0;

    public void SetGrouping(int choice)
    {
        _services.Config.Appearance.GroupMailByDate = choice == 2;
        _services.Config.Appearance.GroupMailByAccount = choice == 1;
        if (!IsDemo)
            SaveConfig();
        OnPropertyChanged(nameof(GroupProperty));
        OnPropertyChanged(nameof(GroupingChoice));
        RefreshColumns();
    }

    /// <summary>Comfortable density shows the start of each message after its subject; compact keeps one plain line.</summary>
    public bool ShowSnippets => _services.Config.Appearance.Density == Density.Comfortable;

    private void RaiseScope()
    {
        OnPropertyChanged(nameof(GroupProperty));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(ScopeAccountLabel));
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(IsAllAccounts));
        NewMailCommand.NotifyCanExecuteChanged();
        Search?.MailScopeChanged();
    }
    public bool FoldersHidden => FoldersDock == DockEdge.Hidden;
    public string SidebarToggleLabel => FoldersHidden ? "Show accounts and folders" : "Hide accounts and folders";
    public bool FoldersAreBand => FoldersDock is DockEdge.Top or DockEdge.Bottom;
    public DockEdge FoldersDock => _services.Config.Layout.FoldersDock;
    public DockEdge ReadingDock => _services.Config.Layout.ReadingDock;
    public double FolderBreadth => LayoutArranger.BreadthFor(FoldersDock, _services.Config.Layout.FolderBreadthSide, _services.Config.Layout.FolderBreadthBand);
    public double ReadingBreadth => LayoutArranger.BreadthFor(ReadingDock, _services.Config.Layout.ReadingBreadthSide, _services.Config.Layout.ReadingBreadthBand);
    /// <summary>Message row height: 32 px compact (default), 40 px comfortable (client spec §6).</summary>
    public double RowHeight => _services.Config.Appearance.Density == Density.Compact ? 32 : 40;
    public string DensityLabel => _services.Config.Appearance.Density.ToString();
    public StoredMessage? SelectedMessage { get; private set; }
    public string ReaderStatus { get; private set; } = "Reading pane";
    public string ReaderSubject { get; private set; } = "No message selected";
    public string ReaderSender { get; private set; } = "";
    public string ReaderBody { get; private set; } = "";
    /// <summary>List header count: "24 messages", "5 of 24 messages", "Loading…", "No messages" (see <see cref="MessageCount"/>).</summary>
    public string CountText => MessageCount.Describe(Messages.Count, _unfilteredTotal, _loadingMessages, !string.IsNullOrWhiteSpace(SearchText) || Filter.IsActive, _selectionCount);

    // ---------- List filters and sort (Mail UI spec §5.1) ----------

    /// <summary>Unread, flagged, attachments, account: applied to the messages on this PC in the current scope.</summary>
    public MailListFilter Filter { get; private set; } = MailListFilter.None;
    public ObservableCollection<MailFilterChip> FilterChips { get; } = [];
    public bool HasFilter => Filter.IsActive;
    public MailSortField Sort => Enum.TryParse<MailSortField>(_services.Config.Layout.MailSort, out var sort) ? sort : MailSortField.Date;
    public bool SortDescending => _services.Config.Layout.MailSortDescending;
    public string SortLabel => MailListView.SortLabel(Sort, SortDescending);

    /// <summary>The arrow shown on the sorted column header ("" on the others).</summary>
    public string SortArrow(MailSortField field) => field == Sort ? (SortDescending ? "↓" : "↑") : "";
    public string FromSortArrow => SortArrow(MailSortField.From);
    public string SubjectSortArrow => SortArrow(MailSortField.Subject);
    public string DateSortArrow => SortArrow(MailSortField.Date);

    public void SetFilter(MailListFilter filter)
    {
        // An account filter only makes sense in a view of several accounts.
        filter = filter.Normalized();
        if (!Scope.IsUnified && filter.AccountId is not null)
            filter = filter with { AccountId = null };
        Filter = filter;
        FilterChips.Clear();
        foreach (var chip in MailListView.Chips(filter, AccountLabel))
            FilterChips.Add(chip);
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(HasFilter));
        _ = ReloadMessagesAsync();
        // All mail results use the same filters: run them again so the panel never shows results for the old ones.
        if (Search.IsPanelOpen && Search.Reach == SearchReach.AllMail)
            Search.SetQuery(Search.Query, now: true);
    }

    /// <summary>Sorts by a column; choosing the sorted column again reverses the direction. Remembered on this PC.</summary>
    public void SetSort(MailSortField field, bool? descending = null)
    {
        var layout = _services.Config.Layout;
        var desc = descending ?? (field == Sort ? !SortDescending : field == MailSortField.Date);
        layout.MailSort = field.ToString();
        layout.MailSortDescending = desc;
        if (!IsDemo)
            SaveConfig();
        foreach (var name in new[] { nameof(Sort), nameof(SortDescending), nameof(SortLabel), nameof(FromSortArrow), nameof(SubjectSortArrow), nameof(DateSortArrow), nameof(GroupProperty) })
            OnPropertyChanged(name);
        _ = ReloadMessagesAsync();
    }
    private int? _unfilteredTotal;
    private bool _loadingMessages;
    private int _selectionCount;
    public bool ShowTimeColumn { get; private set; } = true;
    public bool ShowAttachmentColumn { get; private set; } = true;
    public bool ShowAccountColumn { get; private set; }
    private double _listWidth;

    /// <summary>The message list table's column widths for the current list width and scope (header and rows share them).</summary>
    public MailColumnWidths Columns { get; private set; } = MailColumnLayout.Resolve(640, MailColumnPreferences.Default, false);

    private MailColumnPreferences ColumnPreferences
    {
        get
        {
            var l = _services.Config.Layout;
            return new MailColumnPreferences(l.MailColumnFrom, l.MailColumnAccount, l.MailColumnReceived);
        }
        set
        {
            var l = _services.Config.Layout;
            (l.MailColumnFrom, l.MailColumnAccount, l.MailColumnReceived) = (value.From, value.Account, value.Received);
        }
    }
    public string MonthLabel { get; private set; } = "";
    public string AgendaText { get; private set; } = "";
    public string TaskCountText { get; private set; } = "";
    public string SelectedNoteTitle { get; private set; } = "";
    public string NoteBody { get; set; } = "";
    public string ContactQuery { get; set; } = "";
    public bool NoContacts { get; private set; }
    public string OrganizerNotice { get; private set; } = "";
    public string NewTaskTitle { get; set; } = "";
    // ---------- Reader (client spec §4) ----------

    private StoredBody? _body;
    private bool? _htmlChoice;
    private bool _imagesLoadedForMessage;
    private string? _htmlFailure;
    private MimeKit.MimeMessage? _fixture;

    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasHtml => !string.IsNullOrWhiteSpace(_body?.Html) && _htmlFailure is null;
    public bool ShowingHtml => ReaderPolicy.OpenAsHtml(_services.Config.Reading, HasHtml, _htmlChoice);
    public bool ShowingPlain => !ShowingHtml;
    public string ReaderTo { get; private set; } = "";
    public bool HasReaderTo => !string.IsNullOrWhiteSpace(ReaderTo);
    public string ReaderMeta { get; private set; } = "";

    /// <summary>The document the reader shows: sanitized HTML, or the plain text as a linkified page.</summary>
    public string ReaderDocument { get; private set; } = "";
    public bool ReaderAllowRemote { get; private set; }
    public int BlockedImages { get; private set; }
    public bool ShowImageBar => ShowingHtml && BlockedImages > 0 && !ReaderAllowRemote;
    public string ImageBarText => BlockedImages == 1
        ? "1 remote image is blocked to protect your privacy."
        : $"{BlockedImages} remote images are blocked to protect your privacy.";
    public string TrustSenderLabel => SelectedMessage is { FromAddress.Length: > 0 } message ? $"Always load from {message.FromAddress}" : "Always load from this sender";
    // ---------- Reader header (Mail UI spec §6.1) ----------

    public bool HasMessage => SelectedMessage is not null;

    /// <summary>A new message to one address, sent from the account that received the open message.</summary>
    public void ComposeTo(string address, string? accountId) => ComposeRequested?.Invoke(new OutgoingMail
    {
        To = [address],
        AccountId = ComposeIdentity.DefaultAccount(MailAccounts, Scope, accountId, _services.Config.Reading.DefaultSendAccountId),
    });
    public string ReaderSenderName => SelectedMessage is { } m ? MailListView.SenderName(m) : "";
    public string ReaderSenderAddress => SelectedMessage is { FromAddress: { Length: > 0 } a } m && a != ReaderSenderName ? a : "";
    public bool HasReaderSenderAddress => ReaderSenderAddress.Length > 0;

    /// <summary>Local initials of the sender (no avatar service is ever contacted).</summary>
    public string ReaderInitials
    {
        get
        {
            var words = ReaderSenderName.Split([' ', '.', '@', '_', '-', '"', (char)39], StringSplitOptions.RemoveEmptyEntries);
            var letters = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : words.Length == 1 ? words[0][..1] : "?";
            return letters.ToUpperInvariant();
        }
    }

    /// <summary>The account that received the message: name and address, so two accounts never look alike.</summary>
    public string ReaderAccountLabel => SelectedMessage is { } m && MailAccounts.FirstOrDefault(a => a.Id == m.AccountId) is { } account
        ? ComposeIdentity.Label(account) : SelectedMessage?.AccountName ?? "";

    public string ReaderDateFull => SelectedMessage is { } m
        ? MailColumnLayout.FullDate(m.ReceivedUtc) : "";

    /// <summary>Every recipient on its own line (the header line shows them shortened).</summary>
    public string ReaderRecipientsFull => SelectedMessage is { Recipients: { Length: > 0 } r }
        ? string.Join(Environment.NewLine, Alondra1.Core.Mail.ComposeIdentity.SplitAddresses(r)) : "";
    public bool HasReaderRecipients => ReaderRecipientsFull.Length > 0;

    [ObservableProperty]
    private bool _readerDetailsOpen;

    public string ReaderDetailsLabel => ReaderDetailsOpen ? "Hide details" : "Details";

    partial void OnReaderDetailsOpenChanged(bool value) => OnPropertyChanged(nameof(ReaderDetailsLabel));

    /// <summary>No message open: a quiet centered state that matches the list (never "select" when there is nothing).</summary>
    public string ReaderEmptyTitle => Messages.Count == 0 ? "Nothing to read here" : "No message selected";
    public string ReaderEmptyDetail => Messages.Count == 0 ? "Messages you open from the list appear here." : "Choose a message in the list to read it.";

    /// <summary>Tries the HTML version again after a rendering failure (the plain text stays until it works).</summary>
    public IRelayCommand RetryHtmlCommand => _retryHtml ??= new RelayCommand(() =>
    {
        _htmlFailure = null;
        _htmlChoice = true;
        RenderReader();
    });
    private IRelayCommand? _retryHtml;

    public string? HtmlFailure => _htmlFailure;
    public bool ShowHtmlFailure => _htmlFailure is not null && !string.IsNullOrWhiteSpace(_body?.Html);

    public IRelayCommand ShowHtmlCommand => _showHtml ??= new RelayCommand(() => ChooseFormat(html: true), () => HasHtml);
    public IRelayCommand ShowPlainCommand => _showPlain ??= new RelayCommand(() => ChooseFormat(html: false));
    public IRelayCommand LoadImagesCommand => _loadImages ??= new RelayCommand(() => { _imagesLoadedForMessage = true; RenderReader(); });
    public IRelayCommand TrustSenderCommand => _trustSender ??= new RelayCommand(TrustSender);
    private IRelayCommand? _showHtml;
    private IRelayCommand? _showPlain;
    private IRelayCommand? _loadImages;
    private IRelayCommand? _trustSender;

    /// <summary>Theme colors for plain-text documents; set by the window from the current theme.</summary>
    public Alondra1.Core.Html.MailHtml.PlainColors PlainColors { get; set; } = new("transparent", "#2a2940", "#4b4fa8");

    /// <summary>Switches between HTML and plain text for this message only; no re-fetch.</summary>
    public void ChooseFormat(bool html)
    {
        _htmlChoice = html;
        RenderReader();
    }

    private void TrustSender()
    {
        if (SelectedMessage is null || IsDemo && _fixture is null)
        {
            _imagesLoadedForMessage = true;
            RenderReader();
            return;
        }

        if (ReaderPolicy.Trust(_services.Config.Reading, SelectedMessage.FromAddress))
            SaveConfig();
        RenderReader();
    }

    /// <summary>The view reports that HTML could not be shown; the reader switches to plain text and says so.</summary>
    public void ReportHtmlFailure(string reason)
    {
        if (_htmlFailure is not null)
            return;
        _htmlFailure = reason;
        RenderReader();
    }

    /// <summary>Builds the reader document for the current message, format, and image policy.</summary>
    public void RenderReader()
    {
        if (_body is null)
        {
            ReaderDocument = Alondra1.Core.Html.MailHtml.PlainDocument(ReaderBody, PlainColors);
            ReaderAllowRemote = false;
            BlockedImages = 0;
        }
        else if (ShowingHtml)
        {
            ReaderAllowRemote = ReaderPolicy.AllowRemoteImages(_services.Config.Reading, SelectedMessage?.FromAddress, _imagesLoadedForMessage);
            var result = Alondra1.Core.Html.MailHtml.Render(_body.Html, new Alondra1.Core.Html.MailRenderOptions { AllowRemoteImages = ReaderAllowRemote });
            if (result.Failed)
            {
                // Diagnostic without content: the reason names the problem, never the message text.
                Debug.WriteLine("Reader: HTML not shown: " + result.FailureReason);
                _htmlFailure = "Unable to display HTML.";
                RenderReader();
                return;
            }

            ReaderDocument = result.Document;
            BlockedImages = result.BlockedRemoteImages;
        }
        else
        {
            ReaderDocument = Alondra1.Core.Html.MailHtml.PlainDocument(_body.Text, PlainColors);
            ReaderAllowRemote = false;
            BlockedImages = 0;
        }

        foreach (var name in new[]
        {
            nameof(ReaderDocument), nameof(ReaderAllowRemote), nameof(BlockedImages), nameof(ShowImageBar), nameof(ImageBarText), nameof(TrustSenderLabel),
            nameof(HasHtml), nameof(ShowingHtml), nameof(ShowingPlain), nameof(HtmlFailure), nameof(ShowHtmlFailure), nameof(ReaderBody),
        })
            OnPropertyChanged(name);
        ShowHtmlCommand.NotifyCanExecuteChanged();
    }

    private void SetBody(StoredBody? body, string? recipients = null)
    {
        _body = body;
        if (body is not null)
            ReaderBody = body.Text;
        if (!string.IsNullOrWhiteSpace(recipients))
            ReaderTo = "To " + recipients;
        Attachments.Clear();
        foreach (var attachment in body?.Attachments ?? [])
            Attachments.Add(new AttachmentRow(attachment));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(ReaderTo));
        OnPropertyChanged(nameof(HasReaderTo));
        RenderReader();
    }

    /// <summary>Writes an attachment to <paramref name="destination"/>: from the saved copy when there is one, otherwise from the server.</summary>
    public async Task SaveAttachmentAsync(AttachmentRow row, Stream destination)
    {
        var source = row.Source;
        if (_fixture is not null)
        {
            var entity = Alondra1.Mail.MimeBody.FindAttachment(_fixture, source.FileName) ?? throw new InvalidOperationException("That attachment is not in the message.");
            await Alondra1.Mail.MimeBody.WriteAsync(entity, destination, CancellationToken.None).ConfigureAwait(true);
            return;
        }

        if (IsDemo)
            throw new InvalidOperationException("Sample messages have no files to open or save.");
        if (!string.IsNullOrEmpty(source.CacheRelativePath) && File.Exists(_services.Files.Resolve(source.CacheRelativePath)))
        {
            await using var cached = File.OpenRead(_services.Files.Resolve(source.CacheRelativePath));
            await cached.CopyToAsync(destination).ConfigureAwait(true);
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == source.AccountId)
            ?? throw new InvalidOperationException("The account of this message was removed.");
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        await _services.Mail.FetchAttachmentAsync(account, secrets, source.FolderId, source.Uid, source.FileName, destination, null, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Diagnostics (--open-eml): shows a message file in the reader through the normal pipeline.</summary>
    public void ShowMessageFile(string path)
    {
        var message = MimeKit.MimeMessage.Load(path);
        _fixture = message;
        var from = message.From.Mailboxes.FirstOrDefault();
        var stored = new StoredMessage
        {
            AccountId = "file",
            AccountName = "File",
            FolderId = "Inbox",
            Uid = 1,
            Subject = message.Subject ?? "",
            FromName = string.IsNullOrWhiteSpace(from?.Name) ? from?.Address ?? "" : from.Name,
            FromAddress = from?.Address ?? "",
            ReceivedUtc = message.Date,
            TimeLabel = MailText.FormatTime(message.Date, DateTimeOffset.Now),
            Availability = ItemAvailability.Downloaded,
            LocalOnly = true,
        };
        Messages.Clear();
        Messages.Add(stored);
        _unfilteredTotal = 1;
        SelectMessage(stored);
        SetBody(Alondra1.Mail.MimeBody.Extract(message, "file", "Inbox", 1), string.Join(", ", message.To.Mailboxes.Select(item => item.Address)));
        RaiseListState();
    }

    public AppConfig Config => _services.Config;

    /// <summary>The local To-do workspace (lists, smart views, calendar, reminders).</summary>
    public Alondra1.App.Tasks.TodoViewModel Todo { get; }

    /// <summary>The shared organizer pane is used by Calendar and Contacts. To-do and Notes have their own views.</summary>
    public bool ShowOrganizer => !IsMail && !IsTasks && !IsNotes;

    // ---------- Two-row shell: header search, module toolbar, connection status ----------

    public WorkspaceChoice CurrentWorkspace => Workspaces.First(item => item.IsSelected || item.Name == WorkspaceName);

    /// <summary>Every module has the header search (Calendar shows its results in the results panel).</summary>
    public bool HasSearch => true;
    public string SearchPlaceholder => Search.Placeholder;

    /// <summary>The query the header search box should show for the current module.</summary>
    public string ModuleSearchText => IsMail ? SearchText : IsTasks ? Todo.SearchText : IsNotes ? Notes.SearchText : IsContacts ? ContactQuery : "";

    /// <summary>Mail, Calendar, and Contacts are filtered by account; local To-do and Notes are not.</summary>
    public bool HasAccountSelector => IsMail || IsCalendar || IsContacts;
    /// <summary>Mail chooses its account in the view settings; the other workspaces keep the toolbar selector.</summary>
    public bool ShowToolbarAccountSelector => IsCalendar || IsContacts;
    public bool HasSyncButton => HasAccountSelector;
    public bool IsSyncing { get; private set; }
    public string SyncToolTip => (IsSyncing ? "Syncing…" : "Sync now (F5)") + (string.IsNullOrWhiteSpace(StatusText) ? "" : "\n" + StatusText)
        + (IsDemo || _services.Config.Accounts.Count < 1 ? "" : "\n\n" + SyncDetails);
    public string SyncAccessName => $"Sync now. Status: {ConnectionLabel}. {StatusText}";

    /// <summary>With folders docked on top, they appear as tabs in the module toolbar instead of a separate pane.</summary>
    public bool ShowToolbarFolders => FoldersDock == DockEdge.Top;
    public bool ShowFolderCombo => FoldersDock == DockEdge.Hidden;
    public DockEdge PaneFoldersDock => FoldersDock == DockEdge.Top ? DockEdge.Hidden : FoldersDock;

    public string ReadToggleLabel => SelectedMessage is null ? "Mark as read or unread — Select a message first"
        : SelectedMessage.IsUnread ? "Mark as read" : "Mark as unread";
    public string ReadToggleIcon => SelectedMessage?.IsUnread == true ? "MarkRead" : "Mail";

    public ConnectionState Connection { get; private set; } = ConnectionState.Ready;

    public string ConnectionLabel => Connection switch
    {
        ConnectionState.Sample => "Sample data",
        ConnectionState.NoAccounts => "No accounts",
        ConnectionState.Syncing => "Syncing…",
        ConnectionState.Offline => "Offline",
        ConnectionState.Error => "Sync failed",
        _ => "Up to date",
    };

    /// <summary>Resource key of the status dot color.</summary>
    public string ConnectionBrushKey => Connection switch
    {
        ConnectionState.Ready => "SuccessBrush",
        ConnectionState.Syncing => "BrandBrush",
        ConnectionState.Error => "DangerBrush",
        _ => "MutedBrush",
    };

    // ---------- Responsive header and toolbar (never wraps into another row) ----------

    public bool ShowWordmark { get; private set; } = true;
    public bool ShowNavLabels { get; private set; } = true;
    public bool CompactSearch { get; private set; }
    public bool ShowSearchBox => HasSearch && !CompactSearch;
    public bool ShowSearchButton => HasSearch && CompactSearch;
    public bool HasNavOverflow => Workspaces.Any(item => item.InOverflow);
    public bool ShowStatusLabel { get; private set; } = true;
    public bool ShowSecondaryMailActions { get; private set; } = true;
    public bool ShowFolderLabels { get; private set; } = true;
    public bool FolderTabsAsCombo { get; private set; }
    public bool ShowNewLabel { get; private set; } = true;

    /// <summary>
    /// Adapts the header and toolbar to the window width in steps: hide labels, shorten search, then move less-used
    /// modules into a More menu and collapse search to a button. Everything stays in its single row.
    /// </summary>
    public void SetShellWidth(double width)
    {
        // Breakpoints follow the width of the workspace tab strip (docs/UI-DESIGN.md → Responsive behavior).
        ShowNavLabels = width >= 1200;
        ShowWordmark = width >= 640;
        CompactSearch = width < 860;
        ShowStatusLabel = width >= 1240;
        ShowSecondaryMailActions = width >= 1040;
        ShowFolderLabels = width >= 960;
        FolderTabsAsCombo = width < 760;
        ShowNewLabel = width >= 560;
        string[] overflow = width < 560 ? ["Calendar", "To-do", "Notes", "Contacts"]
            : width < 720 ? ["Notes", "Contacts"]
            : [];
        foreach (var workspace in Workspaces)
            workspace.InOverflow = overflow.Contains(workspace.Name) && !workspace.IsSelected;
        foreach (var name in new[]
        {
            nameof(ShowNavLabels), nameof(ShowWordmark), nameof(CompactSearch), nameof(ShowSearchBox), nameof(ShowSearchButton), nameof(HasNavOverflow),
            nameof(ShowStatusLabel), nameof(ShowSecondaryMailActions), nameof(ShowFolderLabels), nameof(FolderTabsAsCombo), nameof(ShowNewLabel),
        })
            OnPropertyChanged(name);
    }

    private double _shellWidth = 1180;

    public void RememberShellWidth(double width)
    {
        _shellWidth = width;
        SetShellWidth(width);
    }

    public async Task InitializeAsync()
    {
        if (IsDemo)
            _month = new DateTime(2026, 11, 1);
        else
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _chosenDay = IsDemo ? 15 : DateTime.Today.Day;
        RefreshAccountChoices();
        Alondra1.Core.Config.ConfigWorkflow.NormalizeMailLayout(_services.Config);
        Scope = (IsDemo ? null : MailScope.Parse(_services.Config.Layout.MailScope)) ?? MailScope.UnifiedInbox;
        if (!Scope.IsUnified && MailAccounts.All(a => a.Id != Scope.AccountId))
            Scope = MailScope.UnifiedInbox;
        RaiseScope();
        await RefreshNavigationAsync().ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
        LoadOrganizerDemoOrEmpty();
        Todo.Refresh();
        UpdateStatus(IsDemo ? "Sample data" : "Ready");
        RaiseChrome();
    }

    public void SelectWorkspace(string? name)
    {
        // An unknown module (for example "Chat" from an earlier version) opens Mail.
        if (name is null || Workspaces.All(item => item.Name != name))
            name = "Mail";
        WorkspaceName = name;
        WorkspaceGlyph = Workspaces.First(item => item.Name == name).Glyph;
        IsMail = name == "Mail";
        IsCalendar = name == "Calendar";
        IsTasks = name == "To-do";
        IsNotes = name == "Notes";
        IsContacts = name == "Contacts";
        foreach (var workspace in Workspaces)
            workspace.IsSelected = workspace.Name == name;
        SetShellWidth(_shellWidth);
        Search?.SetModule(SearchScopes.FromWorkspace(name) ?? SearchModule.Mail);
        RaiseChrome();
        if (!IsDemo && !IsMail && !IsTasks && !IsNotes)
            _ = LoadOrganizerAsync();
    }

    /// <summary>The account of the Calendar and Contacts views (their toolbar selector). Mail has its own scope.</summary>
    public void SetAccount(string? account)
    {
        SelectedAccount = string.IsNullOrWhiteSpace(account) || !AccountChoices.Contains(account) ? "All accounts" : account;
        var remembered = SelectedAccount == "All accounts" ? null : SelectedAccount;
        if (!IsDemo && _services.Config.Layout.MailAccount != remembered)
        {
            _services.Config.Layout.MailAccount = remembered;
            SaveConfig();
        }

        OnPropertyChanged(nameof(SelectedAccount));
        if (!IsDemo && (IsCalendar || IsContacts))
            _ = LoadOrganizerAsync();
    }

    /// <summary>Diagnostics (--account=name): Mail shows that account's Inbox when exactly one account has the name.</summary>
    public void SetMailAccountByName(string name)
    {
        var named = MailAccounts.Where(a => a.DisplayName == name).ToList();
        SetScope(named.Count == 1 ? new MailScope(named[0].Id, FolderRole.Inbox) : MailScope.UnifiedInbox);
    }

    public void ApplySearch(string? text)
    {
        SearchText = text ?? "";
        _ = ReloadMessagesAsync();
    }

    public void SelectMessage(StoredMessage? message)
    {
        // Selecting the message that is already open (the list echoing a selection) keeps its body, format, and images.
        if (message is not null && ReferenceEquals(message, SelectedMessage) && _body is not null)
            return;
        // The same message re-read by a reload (sync, filter, first page then full list): keep its body, format and images.
        if (message is not null && SelectedMessage is { } open && _body is not null
            && open.AccountId == message.AccountId && open.FolderId == message.FolderId && open.Uid == message.Uid)
        {
            SelectedMessage = message;
            return;
        }
        SelectedMessage = message;
        // A new message starts in the default format with remote images blocked (unless allowed by settings).
        _htmlChoice = null;
        _imagesLoadedForMessage = false;
        _htmlFailure = null;
        _body = null;
        ReaderTo = "";
        Attachments.Clear();
        if (message is null)
        {
            ReaderStatus = "Reading pane";
            ReaderSubject = "No message selected";
            ReaderSender = "";
            ReaderMeta = "";
            ReaderBody = "";
        }
        else
        {
            ReaderSubject = message.Subject;
            ReaderSender = string.IsNullOrWhiteSpace(message.FromAddress) || message.FromName == message.FromAddress
                ? message.FromName
                : $"{message.FromName} <{message.FromAddress}>";
            ReaderMeta = $"{message.AccountName} · {message.ReceivedUtc.ToLocalTime():ddd d MMM yyyy, HH:mm}";
            ReaderTo = string.IsNullOrWhiteSpace(message.Recipients) ? "" : "To " + message.Recipients;
            ReaderStatus = message.SendState ?? AvailabilityLabel(message);
            ReaderBody = string.IsNullOrWhiteSpace(message.Preview) ? "" : message.Preview;
            if (_fixture is null || message.AccountId != "file")
                _ = LoadBodyAsync(message);
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(ReaderMeta));
        OnPropertyChanged(nameof(ReaderTo));
        OnPropertyChanged(nameof(HasReaderTo));
        foreach (var name in new[] { nameof(HasMessage), nameof(ReaderSenderName), nameof(ReaderSenderAddress), nameof(HasReaderSenderAddress), nameof(ReaderInitials),
            nameof(ReaderAccountLabel), nameof(ReaderDateFull), nameof(ReaderRecipientsFull), nameof(HasReaderRecipients), nameof(ReaderEmptyTitle), nameof(ReaderEmptyDetail) })
            OnPropertyChanged(name);
        RenderReader();

        ReplyCommand.NotifyCanExecuteChanged();
        ReplyAllCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        ToggleReadCommand.NotifyCanExecuteChanged();
        RaiseReader();
    }

    public void UpdateViewport(double width, double height)
    {
        _listWidth = width;
        RefreshColumns();
    }

    private void RefreshColumns()
    {
        if (_listWidth <= 0)
            return;
        // Account headings already name the account: no Account column under them.
        Columns = MailColumnLayout.Resolve(_listWidth, ColumnPreferences, Scope.IsUnified && GroupProperty != nameof(StoredMessage.AccountName));
        ShowTimeColumn = Columns.ShowReceived;
        ShowAttachmentColumn = Columns.ShowAttachment;
        ShowAccountColumn = Columns.ShowAccount;
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(ShowTimeColumn));
        OnPropertyChanged(nameof(ShowAttachmentColumn));
        OnPropertyChanged(nameof(ShowAccountColumn));
    }

    /// <summary>A column edge was dragged or moved with the arrow keys; <see cref="SaveColumns"/> keeps it.</summary>
    public void ResizeColumn(MailColumn column, double delta)
    {
        ColumnPreferences = MailColumnLayout.Resize(ColumnPreferences, column, delta, _listWidth, Scope.IsUnified);
        RefreshColumns();
    }

    public void SaveColumns()
    {
        if (!IsDemo)
            SaveConfig();
    }

    /// <summary>Back to the default proportions (header menu, or double-click on a column edge).</summary>
    public void ResetColumns(MailColumn? column = null)
    {
        var p = ColumnPreferences;
        ColumnPreferences = column switch
        {
            MailColumn.From => p with { From = null },
            MailColumn.Account => p with { Account = null },
            MailColumn.Received => p with { Received = null },
            _ => MailColumnPreferences.Default,
        };
        RefreshColumns();
        SaveColumns();
    }

    public void SetDock(bool folders, DockEdge edge)
    {
        if (folders)
            _services.Config.Layout.FoldersDock = edge;
        else
            _services.Config.Layout.ReadingDock = edge;
        SaveConfig();
        RaiseLayout();
    }

    public void SetDensity(Density density)
    {
        _services.Config.Appearance.Density = density;
        SaveConfig();
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(DensityLabel));
        OnPropertyChanged(nameof(ShowSnippets));
        _ = ReloadMessagesAsync();
    }

    public void CommitBreadth(string paneId, double breadth)
    {
        var layout = _services.Config.Layout;
        if (paneId == "folders")
        {
            if (layout.FoldersDock is DockEdge.Left or DockEdge.Right)
                layout.FolderBreadthSide = breadth;
            else
                layout.FolderBreadthBand = breadth;
        }
        else if (layout.ReadingDock is DockEdge.Left or DockEdge.Right)
            layout.ReadingBreadthSide = breadth;
        else
            layout.ReadingBreadthBand = breadth;
        SaveConfig();
        OnPropertyChanged(nameof(FolderBreadth));
        OnPropertyChanged(nameof(ReadingBreadth));
    }

    public async Task SyncAsync()
    {
        if (IsDemo)
        {
            UpdateStatus("Sample data");
            return;
        }

        if (IsSyncing)
            return;
        IsSyncing = true;
        OnPropertyChanged(nameof(IsSyncing));
        SyncCommand.NotifyCanExecuteChanged();
        try
        {
            await SyncAccountsAsync(_services.Config.Accounts.ToList()).ConfigureAwait(true);
        }
        finally
        {
            IsSyncing = false;
            OnPropertyChanged(nameof(IsSyncing));
            OnPropertyChanged(nameof(SyncToolTip));
            SyncCommand.NotifyCanExecuteChanged();
        }
    }

    // ---------- Per-account sync status (Mail UI spec §7) ----------

    private readonly AccountSyncBoard _syncBoard = new();

    /// <summary>At most this many accounts sync at once; a slow or failing provider never holds up the others.</summary>
    public const int SyncConcurrency = 3;

    public AccountSyncStatus SyncStatusOf(AccountConfig account) => _syncBoard.Get(account.Id, account.LastMailSyncUtc);

    /// <summary>
    /// Syncs the accounts side by side (bounded). Each account's state updates as soon as it finishes, the sidebar
    /// counts refresh without losing the selection, and the list reloads once at the end.
    /// </summary>
    /// <summary>A provider that has not finished one account's sync in this time is reported as not answering.</summary>
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);
    private Task? _navigationRefresh;

    private void RefreshNavigationSoon()
    {
        if (_navigationRefresh is { IsCompleted: false })
            return;
        _navigationRefresh = Coalesced();

        async Task Coalesced()
        {
            await Task.Delay(250).ConfigureAwait(true);
            await RefreshNavigationAsync().ConfigureAwait(true);
        }
    }

    private async Task SyncAccountsAsync(IReadOnlyList<AccountConfig> accounts)
    {
        foreach (var account in accounts)
            _syncBoard.Begin(account.Id, DateTimeOffset.UtcNow, account.LastMailSyncUtc);
        PublishSyncStatus();
        var degraded = new List<string>();
        // Independent per account: at most SyncConcurrency at once, each with its own time limit, so one slow or
        // failing provider never holds up the others or the window.
        await AccountSyncScheduler.RunAsync(accounts, SyncConcurrency, SyncTimeout,
            (account, ct) =>
            {
                // The account may have been deleted while others were syncing.
                if (!_services.Config.Accounts.Contains(account))
                    return Task.FromResult(SyncReport.Stopped("removed"));
                return Task.Run(() => _services.Sync.SyncAsync(account, ct), ct);
            },
            (account, report) =>
            {
                if (!_services.Config.Accounts.Contains(account))
                {
                    _syncBoard.Remove(account.Id);
                    PublishSyncStatus();
                    return Task.CompletedTask;
                }

                _syncBoard.Finish(account.Id, report, DateTimeOffset.UtcNow);
                if (report.Ok)
                {
                    account.LastMailSyncUtc = DateTimeOffset.UtcNow;
                    // Remember where this server keeps Inbox, Sent, Drafts, Archive, and Trash (for example "INBOX.INBOX.Sent").
                    SpecialFolders.Learn(account, report.Folders);
                }

                if (report.DegradedDedup)
                    degraded.Add(report.Detail);
                PublishSyncStatus();
                // Sidebar counts: one refresh for accounts finishing close together, not one redraw per account.
                RefreshNavigationSoon();
                return Task.CompletedTask;
            }).ConfigureAwait(true);
        await RefreshNavigationAsync().ConfigureAwait(true);
        SaveConfig();
        foreach (var detail in degraded.Distinct())
            Dialogs.Alert(detail, "Alondra1", MessageBoxButton.OK, MessageBoxImage.Information);
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    /// <summary>Syncs one account (its sidebar menu). Other accounts keep their own state.</summary>
    public async Task SyncAccountAsync(string accountId)
    {
        if (IsDemo || _services.Config.Accounts.FirstOrDefault(a => a.Id == accountId) is not { } account)
            return;
        if (_syncBoard.Get(accountId).State == AccountSyncState.Syncing)
            return;
        await SyncAccountsAsync([account]).ConfigureAwait(true);
    }

    /// <summary>One line per account for the status tooltip and details: name, state, last success, reason.</summary>
    public string SyncDetails
    {
        get
        {
            if (IsDemo || _services.Config.Accounts.Count == 0)
                return StatusText;
            var now = DateTimeOffset.UtcNow;
            var names = Alondra1.Core.Search.MailSearchSource.AccountNames(_services.Config.Accounts);
            return string.Join("\n", _services.Config.Accounts.Select(a => $"{names[a.Id]}: {SyncStatusOf(a).Describe(now)}"));
        }
    }

    /// <summary>The status area and the sidebar badges follow the per-account states.</summary>
    private void PublishSyncStatus()
    {
        var states = _services.Config.Accounts.Select(SyncStatusOf).ToList();
        var overall = AccountSyncBoard.Overall(states);
        UpdateStatus(AccountSyncBoard.Summary(states), overall switch
        {
            SyncOverall.Syncing => ConnectionState.Syncing,
            SyncOverall.Offline => ConnectionState.Offline,
            SyncOverall.Attention => ConnectionState.Error,
            _ => ConnectionState.Ready,
        }, summary: true);
        ApplySyncBadges();
        OnPropertyChanged(nameof(SyncDetails));
    }

    private void ApplySyncBadges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in NavRows.Where(r => r.IsAccount))
        {
            var account = _services.Config.Accounts.FirstOrDefault(a => a.Id == row.Node.AccountId);
            row.SetSync(account is null ? null : SyncStatusOf(account), now);
        }
    }

    public void UpsertAccount(AccountConfig account, string? password)
    {
        var existing = _services.Config.Accounts.FirstOrDefault(item => string.Equals(item.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            _services.Config.Accounts.Add(account);
        else
        {
            var index = _services.Config.Accounts.IndexOf(existing);
            account.Mode = existing.Mode;
            _services.Config.Accounts[index] = account;
        }

        _services.Config.DemoMode = false;
        SaveConfig();
        if (password is not null)
        {
            var bundle = _services.Secrets.LoadAsync(account.Id, CancellationToken.None).GetAwaiter().GetResult();
            bundle.Password = password;
            _services.Secrets.SaveAsync(account.Id, bundle, CancellationToken.None).GetAwaiter().GetResult();
        }

        RefreshAccountChoices();
        _ = InitializeAsync();
    }

    public async Task<string> TestAccountAsync(AccountConfig account, string? password)
    {
        var bundle = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(password))
            bundle.Password = password;
        var incoming = await _services.Mail.ProbeIncomingAsync(account, bundle, CancellationToken.None).ConfigureAwait(true);
        var outgoing = await _services.Mail.ProbeOutgoingAsync(account, bundle, CancellationToken.None).ConfigureAwait(true);
        return incoming.Detail + "\n" + outgoing.Detail;
    }

    public void ApplyImport(AppConfig replacement, bool replace)
    {
        var plan = ConfigWorkflow.Preview(_services.Config, replacement);
        if (!plan.Ok || plan.Merged is null || plan.Replaced is null)
            return;
        _services.ApplyImported(replace ? plan.Replaced : plan.Merged);
        _ = InitializeAsync();
    }

    public string ExportTo(string path)
    {
        var json = ConfigWorkflow.ExportJson(_services.Config);
        File.WriteAllText(path, json);
        return json;
    }

    public (long Database, long Files) DiskUsage() =>
        (_services.Store.DatabaseBytesAsync(CancellationToken.None).GetAwaiter().GetResult(), _services.Files.TotalBytes());

    public async Task CleanupAttachmentsAsync(string accountId)
    {
        var items = await _services.Store.ListUnpinnedAttachmentsAsync(accountId, CancellationToken.None).ConfigureAwait(true);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.CacheRelativePath))
                continue;
            var full = _services.Files.Resolve(item.CacheRelativePath);
            if (File.Exists(full))
                File.Delete(full);
            await _services.Store.ForgetAttachmentFileAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public Task<int> PendingSendCountAsync(AccountConfig account) =>
        _services.Sync.PendingCountAsync(account, CancellationToken.None);

    /// <summary>
    /// Deletes an account from Alondra1: its settings, saved password and sign-in, local mail copy, outbox, and
    /// attachment files. Mail and organizer data on the server are not touched.
    /// </summary>
    public async Task DeleteAccountAsync(AccountConfig account)
    {
        // Remove the account from the configuration first so no new sync starts for it.
        _services.Config.Accounts.RemoveAll(item => string.Equals(item.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        SaveConfig();
        await _services.Sync.RemoveAccountAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        _services.Files.DeleteAccount(account.Id);
        if (SelectedMessage?.AccountId == account.Id)
            SelectMessage(null);
        await InitializeAsync().ConfigureAwait(true);
    }

    public async Task ChangeModeAsync(AccountConfig account, AccountMode target, bool discardPending)
    {
        var pending = await _services.Sync.PendingCountAsync(account, CancellationToken.None).ConfigureAwait(true);
        var kind = ModeTransition.Evaluate(account.Mode, target, pending);
        if (kind == ModeTransitionKind.MustResolveOrDiscardPending && !discardPending)
            throw new InvalidOperationException("Resolve or discard the outbox before leaving Personal mode.");
        if (kind == ModeTransitionKind.ReviewLocalThenRemove && discardPending)
            await _services.Sync.DiscardPendingAsync(account, CancellationToken.None).ConfigureAwait(true);
        if (kind == ModeTransitionKind.MustResolveOrDiscardPending && discardPending)
            await _services.Sync.DiscardPendingAsync(account, CancellationToken.None).ConfigureAwait(true);
        account.Mode = target;
        SaveConfig();
        if (target == AccountMode.Personal)
            await _services.Sync.SyncAsync(account, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    public void SelectDay(CalendarDay? day)
    {
        if (day is not { IsDay: true })
            return;
        _chosenDay = day.Day;
        BuildCalendar();
    }

    public void ApplyContactQuery(string? query)
    {
        ContactQuery = query ?? "";
        RebuildContacts();
    }

    public void SelectNote(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;
        SelectedNoteTitle = title;
        NoteBody = _notes.FirstOrDefault(note => note.Title == title)?.Body ?? "";
        OnPropertyChanged(nameof(SelectedNoteTitle));
        OnPropertyChanged(nameof(NoteBody));
    }

    /// <summary>Incremented by every reload; a reload that finds a newer one started drops its results (no stale scope).</summary>
    private long _reloadVersion;

    /// <summary>A plain list (newest first, no search or filter) longer than this shows its newest rows while the rest loads.</summary>
    private const int FirstPageSize = 300;

    private async Task ReloadMessagesAsync()
    {
        var version = ++_reloadVersion;
        _loadingMessages = true;
        RaiseListState();
        var searching = !string.IsNullOrWhiteSpace(SearchText);
        var list = new List<StoredMessage>();
        var total = 0;
        IReadOnlySet<MessageKey>? attachments;
        try
        {
            var targets = FoldersInScope();
            // A Personal-mode folder that is not kept on this PC has no local copy: say so instead of "no messages".
            _scopeNotSynced = targets.Count > 0 && targets.All(t => !IsDemo && t.Account.Mode == AccountMode.Personal && !MailNavigation.SyncedHere(t.Account, t.FolderId));
            attachments = await AttachmentMatchesAsync(Filter, CancellationToken.None).ConfigureAwait(true);
            if (version != _reloadVersion)
                return;
            if (IsDemo)
            {
                foreach (var (account, folderId) in targets)
                {
                    var all = SampleCatalog.QueryScope(folderId, account.Id, "");
                    total += all.Count;
                    list.AddRange(searching ? SampleCatalog.QueryScope(folderId, account.Id, SearchText) : all);
                }
            }
            else
            {
                var personal = targets.Where(t => t.Account.Mode == AccountMode.Personal).Select(t => (t.Account.Id, t.FolderId)).ToList();
                if (!searching && !Filter.IsActive && Sort == MailSortField.Date && SortDescending && personal.Count > 0)
                {
                    // Large unified views: the newest rows first (spec §10, first usable rows), the full list follows.
                    var page = await _services.Store.ListPageAsync(personal, FirstPageSize, CancellationToken.None).ConfigureAwait(true);
                    if (version != _reloadVersion)
                        return;
                    if (page.Count >= FirstPageSize)
                    {
                        var held = targets.Where(t => t.Account.Mode != AccountMode.Personal).SelectMany(t => _services.Cache.List(t.Account.Id, t.FolderId));
                        _unfilteredTotal = null;
                        Publish(MailListView.Apply(page.Concat(held), MailListFilter.None, Sort, SortDescending));
                    }
                }

                foreach (var (account, folderId) in targets)
                {
                    if (account.Mode == AccountMode.Personal)
                    {
                        var all = await _services.Store.ListAsync(account.Id, folderId, CancellationToken.None).ConfigureAwait(true);
                        total += all.Count;
                        list.AddRange(searching
                            ? await _services.Store.SearchAsync(account.Id, folderId, SearchText, CancellationToken.None).ConfigureAwait(true)
                            : all);
                        if (version != _reloadVersion)
                            return;
                    }
                    else
                    {
                        var all = _services.Cache.List(account.Id, folderId);
                        total += all.Count;
                        list.AddRange(searching ? _services.Cache.Search(account.Id, folderId, SearchText) : all);
                    }
                }
            }
        }
        finally
        {
            if (version == _reloadVersion)
                _loadingMessages = false;
        }

        // One list across accounts, filtered and sorted; ties broken by account, folder and uid so the order is stable.
        var found = MailListView.Apply(list, Filter, Sort, SortDescending, attachments);
        _unfilteredTotal = total;
        Publish(found);
    }

    /// <summary>Shows rows in the list in one update, keeping the open message open when it is still listed.</summary>
    private void Publish(IReadOnlyList<StoredMessage> found)
    {
        _selectionCount = 0;
        _selection = [];
        var previous = SelectedMessage;
        // Dates relative to now (a message from yesterday no longer shows only its time), and the account each row
        // came from, by name, with the address when two accounts share a name.
        var names = MailSearchSource.AccountNames(MailAccounts);
        var now = DateTimeOffset.Now;
        foreach (var message in found)
        {
            message.TimeLabel = MailColumnLayout.TimeLabel(message.ReceivedUtc, now);
            message.DateGroup = MailListView.DateGroup(message.ReceivedUtc, now);
            message.SnippetSuffix = ShowSnippets ? MailListView.Snippet(message.Preview) : "";
            if (names.TryGetValue(message.AccountId, out var name))
                message.AccountName = name;
        }

        Messages.ReplaceAll(found);
        var keep = previous is null ? null
            : Messages.FirstOrDefault(m => m.AccountId == previous.AccountId && m.FolderId == previous.FolderId && m.Uid == previous.Uid);
        SelectMessage(keep ?? Messages.FirstOrDefault());
        OnPropertyChanged(nameof(Messages));
        RaiseListState();
    }

    /// <summary>Called by the list when its selection changes; two or more selected rows are counted in the header.</summary>
    public void SetSelectionCount(int count)
    {
        _selectionCount = count;
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>What the list shows when it has no rows: loading, an empty folder, no search results, or a sync problem.</summary>
    public string ListState => _loadingMessages && Messages.Count == 0 ? "loading"
        : Messages.Count > 0 ? "rows"
        : MailAccounts.Count == 0 ? "noaccount"
        : FoldersInScope().Count == 0 ? "nofolder"
        : _scopeNotSynced ? "notsynced"
        : !string.IsNullOrWhiteSpace(SearchText) ? "nomatch"
        : Filter.IsActive ? "filtered"
        : Connection is ConnectionState.Error ? "error"
        : Connection is ConnectionState.Offline ? "offline"
        : "empty";

    public bool ShowListPlaceholder => ListState != "rows";

    public string EmptyTitle => ListState switch
    {
        "loading" => "Loading messages…",
        "noaccount" => "No mail account yet",
        "nofolder" => $"No {SelectedFolder} folder",
        "notsynced" => "This folder is not kept on this PC",
        "nomatch" => "No messages match your search",
        "filtered" => "No messages match the filters",
        "error" => "Sync failed",
        "offline" => "You are offline",
        _ => "No messages here",
    };

    public string EmptyDetail => ListState switch
    {
        "loading" => "",
        "noaccount" => "Add an account in Settings → Accounts to read and send mail here.",
        "nofolder" => Scope.IsUnified ? $"None of your accounts has a {SelectedFolder} folder." : $"{ScopeAccountLabel} has no {SelectedFolder} folder on its server.",
        "notsynced" => "Choose the folders to keep in the account's settings, then sync (F5).",
        "nomatch" => $"Nothing in {SelectedFolder} matches “{SearchText.Trim()}”.",
        "filtered" => $"{SelectedFolder} in {ScopeAccountLabel} has no messages on this PC with: {string.Join(", ", FilterChips.Select(c => c.Label))}. Clear the filters to see all.",
        "error" => "Messages could not be loaded. Point at the Sync button for details, or press F5 to try again.",
        "offline" => "Messages already on this PC show here. Press F5 when you are back online.",
        _ => $"{SelectedFolder} in {ScopeAccountLabel} has no messages on this PC.",
    };

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ListState));
        OnPropertyChanged(nameof(ShowListPlaceholder));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
    }

    private async Task LoadBodyAsync(StoredMessage message)
    {
        if (IsDemo)
        {
            SetBody(SampleCatalog.BodyFor(message));
            RaiseReader();
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId);
        if (account is null)
            return;
        StoredBody? body;
        var key = new MessageKey(message.AccountId, message.FolderId, message.Uid);
        try
        {
            if (account.Mode == AccountMode.Personal)
            {
                body = await _services.Store.GetBodyAsync(key, CancellationToken.None).ConfigureAwait(true);
                if (body is not null)
                    body.Attachments = (await _services.Store.ListAttachmentsAsync(key, CancellationToken.None).ConfigureAwait(true)).ToList();
            }
            else
            {
                body = _services.Cache.GetBody(key);
                if (body is null && message.Uid > 0)
                {
                    var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
                    body = await _services.Mail.FetchBodyAsync(account, secrets, message.FolderId, message.Uid, CancellationToken.None).ConfigureAwait(true);
                    _services.Cache.SetBody(key, body);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException or MailKit.ServiceNotConnectedException or MailKit.Security.AuthenticationException or MailKit.ProtocolException)
        {
            // The preview stays; the reader says the full message could not be loaded (no message content is logged).
            ReaderStatus = "Could not load the full message: " + ex.GetType().Name;
            RaiseReader();
            return;
        }

        // The user may have moved on while the body loaded.
        if (!ReferenceEquals(SelectedMessage, message))
            return;
        if (body is not null)
            SetBody(body);
        RaiseReader();
    }

    private async Task DeleteAsync() => await MoveSpecialAsync("Trash");

    /// <summary>The selected rows (two or more with Shift/Ctrl); actions apply to all of them.</summary>
    private IReadOnlyList<StoredMessage> _selection = [];

    /// <summary>Called by the list with every selected row; the reader keeps showing the focused one.</summary>
    public void SetSelection(IReadOnlyList<StoredMessage> selection)
    {
        _selection = selection;
        SetSelectionCount(selection.Count);
    }

    private IReadOnlyList<StoredMessage> ActionTargets() =>
        _selection.Count > 1 ? _selection.ToList() : SelectedMessage is null ? [] : [SelectedMessage];

    private async Task MoveSpecialAsync(string folder)
    {
        var targets = ActionTargets();
        if (targets.Count == 0)
            return;
        if (IsDemo)
        {
            foreach (var message in targets)
                Messages.Remove(message);
            _unfilteredTotal = Math.Max(0, (_unfilteredTotal ?? targets.Count) - targets.Count);
            _selection = [];
            _selectionCount = 0;
            SelectMessage(Messages.FirstOrDefault());
            RaiseListState();
            return;
        }

        // Each message goes to its own account's folder; what can't be moved is reported, not dropped (spec §3.2, T05).
        var role = folder == "Trash" ? FolderRole.Trash : FolderRole.Archive;
        var plan = BulkActions.Plan(targets, _services.Config.Accounts, role);
        var reasons = plan.Skipped.Select(s => s.Reason).ToList();
        var done = 0;
        foreach (var step in plan.Steps)
        {
            var command = new RemoteMailCommand
            {
                Kind = role == FolderRole.Trash ? MailCommandKind.Delete : MailCommandKind.Archive,
                FolderId = step.Message.FolderId,
                Uid = step.Message.Uid,
                DestinationFolderId = step.Destination,
            };
            try
            {
                await _services.Sync.ApplyLocalCommandAsync(step.Account, command, CancellationToken.None).ConfigureAwait(true);
                done++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reasons.Add($"{ComposeIdentity.Label(step.Account)} could not be updated ({ex.GetType().Name})");
            }
        }

        _selection = [];
        await ReloadMessagesAsync().ConfigureAwait(true);
        await RefreshNavigationAsync().ConfigureAwait(true);
        if (reasons.Count > 0)
            Dialogs.Alert(BulkActions.Summary(role == FolderRole.Trash ? "Deleted" : "Archived", targets.Count, done, reasons), "Alondra1", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ToggleReadAsync()
    {
        var targets = ActionTargets();
        if (targets.Count == 0)
            return;
        // Several rows: mark all read when any is unread, otherwise all unread (like other mail apps).
        var markRead = targets.Count > 1 ? targets.Any(message => message.IsUnread) : targets[0].IsUnread;
        foreach (var message in targets)
        {
            message.IsUnread = !markRead;
            if (!IsDemo && _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId) is { } account)
            {
                await _services.Sync.ApplyLocalCommandAsync(account, new RemoteMailCommand
                {
                    Kind = MailCommandKind.SetRead,
                    FolderId = message.FolderId,
                    Uid = message.Uid,
                    Value = markRead,
                }, CancellationToken.None).ConfigureAwait(true);
            }

            MessageStateChanged?.Invoke(message);
        }

        RaiseReader();
        await RefreshNavigationAsync().ConfigureAwait(true);
    }

    // ---------- One pane at a time (client spec §5.1) ----------

    /// <summary>True when list and reader are shown one at a time; the reader then has a Back button.</summary>
    public bool IsSinglePane { get; private set; }
    public bool ShowBackButton { get; private set; }

    public void SetSinglePane(bool single, bool readerShown)
    {
        if (IsSinglePane == single && ShowBackButton == (single && readerShown))
            return;
        IsSinglePane = single;
        ShowBackButton = single && readerShown;
        OnPropertyChanged(nameof(IsSinglePane));
        OnPropertyChanged(nameof(ShowBackButton));
    }

    private void LoadOrganizerDemoOrEmpty()
    {
        if (IsDemo)
        {
            _events = SampleCatalog.Events().ToList();
            Tasks.Clear();
            foreach (var task in SampleCatalog.Tasks())
                Tasks.Add(new TaskRow(task));
            _notes = SampleCatalog.Notes().ToList();
            _contacts = SampleCatalog.Contacts().ToList();
            _eventsCoverage = "Sample events.";
            _contactsCoverage = "Sample contacts.";
            OrganizerNotice = "Sample data · Work";
        }
        else
        {
            _events = [];
            Tasks.Clear();
            _notes = [];
            _contacts = [];
            OrganizerNotice = _services.Config.Accounts.Count == 0
                ? "Add a Microsoft 365 or Google account to use this view."
                : "";
        }

        NoteTitles.Clear();
        foreach (var note in _notes)
            NoteTitles.Add(note.Title);
        SelectNote(_notes.FirstOrDefault()?.Title);
        RebuildContacts();
        BuildCalendar();
        UpdateTaskCount();
    }

    private async Task LoadOrganizerAsync()
    {
        var account = OrganizerAccount();
        if (account is null)
        {
            OrganizerNotice = OrganizerCopy.ManualUnavailable;
            OnPropertyChanged(nameof(OrganizerNotice));
            return;
        }

        try
        {
            var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
            secrets = await _services.OAuth.EnsureFreshAsync(account, secrets, account.Kind == AccountKind.Gmail ? Alondra1.Core.Security.TokenAudience.Google : Alondra1.Core.Security.TokenAudience.Graph, CancellationToken.None).ConfigureAwait(true);
            await _services.Secrets.SaveAsync(account.Id, secrets, CancellationToken.None).ConfigureAwait(true);
            if (IsCalendar)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Calendar, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? $"{status.AccountName}";
                if (status.Available)
                {
                    var start = new DateTimeOffset(_month.Year, _month.Month, 1, 0, 0, 0, TimeSpan.Zero);
                    _events = (await _services.Organizer.ListEventsAsync(account, secrets, start, start.AddMonths(1), CancellationToken.None).ConfigureAwait(true)).ToList();
                    _eventsCoverage = $"Events of {start:MMMM yyyy} loaded on this PC ({account.DisplayName}). Open other months in Calendar to search them.";
                    BuildCalendar();
                }
            }
            else if (IsTasks)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Tasks, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? status.AccountName;
                Tasks.Clear();
                if (status.Available)
                {
                    foreach (var task in await _services.Organizer.ListTasksAsync(account, secrets, CancellationToken.None).ConfigureAwait(true))
                        Tasks.Add(new TaskRow(task));
                }

                UpdateTaskCount();
            }
            else if (IsNotes)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Notes, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? OrganizerCopy.NotesAreAppNotes;
                if (status.Available)
                {
                    _notes = (await _services.Organizer.ListNotesAsync(account, secrets, CancellationToken.None).ConfigureAwait(true)).ToList();
                    NoteTitles.Clear();
                    foreach (var note in _notes)
                        NoteTitles.Add(note.Title);
                    SelectNote(_notes.FirstOrDefault()?.Title);
                }
            }
            else if (IsContacts)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Contacts, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? status.AccountName;
                if (status.Available)
                {
                    _contacts = (await _services.Organizer.ListContactsAsync(account, secrets, CancellationToken.None).ConfigureAwait(true)).ToList();
                    _contactsCoverage = $"Contacts loaded on this PC from {account.DisplayName}.";
                }

                RebuildContacts();
            }
        }
        catch (Exception ex)
        {
            OrganizerNotice = ex.Message;
        }

        OnPropertyChanged(nameof(OrganizerNotice));
    }

    private async Task ToggleTaskAsync(TaskRow? row)
    {
        if (row is null)
            return;
        UpdateTaskCount();
        if (IsDemo)
            return;
        var account = OrganizerAccount();
        if (account is null)
            return;
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        row.Source.Completed = row.Done;
        await _services.Organizer.SaveTaskAsync(account, secrets, row.Source, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task AddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle))
            return;
        var entry = new TaskEntry { Id = Guid.NewGuid().ToString("n"), Title = NewTaskTitle.Trim(), AccountName = IsDemo ? "Work" : "" };
        Tasks.Add(new TaskRow(entry));
        NewTaskTitle = "";
        OnPropertyChanged(nameof(NewTaskTitle));
        UpdateTaskCount();
        if (IsDemo)
            return;
        var account = OrganizerAccount();
        if (account is null)
            return;
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        await _services.Organizer.SaveTaskAsync(account, secrets, entry, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task SaveNoteAsync()
    {
        var note = _notes.FirstOrDefault(item => item.Title == SelectedNoteTitle);
        if (note is null)
            return;
        note.Body = NoteBody;
        if (IsDemo)
            return;
        var account = OrganizerAccount();
        if (account is null)
            return;
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        await _services.Organizer.SaveNoteAsync(account, secrets, note, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task NewNoteAsync()
    {
        var note = new NoteEntry { Id = Guid.NewGuid().ToString("n"), Title = "New note", Body = "", Revision = "1", UpdatedUtc = DateTimeOffset.UtcNow };
        _notes.Add(note);
        NoteTitles.Add(note.Title);
        SelectNote(note.Title);
        if (!IsDemo)
            await SaveNoteAsync().ConfigureAwait(true);
    }

    private void ShiftMonth(int delta)
    {
        _month = _month.AddMonths(delta);
        _chosenDay = 1;
        if (!IsDemo && IsCalendar)
            _ = LoadOrganizerAsync();
        else
            BuildCalendar();
    }

    private void BuildCalendar()
    {
        Days.Clear();
        MonthLabel = _month.ToString("MMMM yyyy");
        foreach (var name in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            Days.Add(new CalendarDay { Text = name, AccessName = name });
        var first = new DateTime(_month.Year, _month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        for (var blank = 0; blank < offset; blank++)
            Days.Add(new CalendarDay { Text = "" });
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        for (var day = 1; day <= days; day++)
        {
            var events = _events.Where(item => item.Start.Year == _month.Year && item.Start.Month == _month.Month && item.Start.Day == day).ToList();
            var label = events.Count == 0 ? "" : string.Join(", ", events.Select(item => item.Start.ToLocalTime().ToString("HH:mm") + " · " + item.Subject));
            Days.Add(new CalendarDay
            {
                Text = day.ToString(),
                Day = day,
                IsDay = true,
                HasEvent = events.Count > 0,
                IsSelected = day == _chosenDay,
                AccessName = string.IsNullOrEmpty(label) ? $"{day} {MonthLabel}" : $"{day} {MonthLabel}, {label}",
            });
        }

        var chosen = _events.Where(item => item.Start.Year == _month.Year && item.Start.Month == _month.Month && item.Start.Day == _chosenDay).ToList();
        var date = new DateTime(_month.Year, _month.Month, Math.Min(_chosenDay, days));
        AgendaText = date.ToString("ddd, MMM d") + " · " + (chosen.Count == 0 ? "No events" : chosen.Count == 1 ? "1 event" : $"{chosen.Count} events");
        AgendaEvents.Clear();
        foreach (var item in chosen.OrderBy(e => e.Start))
            AgendaEvents.Add(new AgendaEvent(item));
        OnPropertyChanged(nameof(MonthLabel));
        OnPropertyChanged(nameof(AgendaText));
    }

    private void RebuildContacts()
    {
        VisibleContacts.Clear();
        foreach (var contact in _contacts.Where(item => string.IsNullOrWhiteSpace(ContactQuery) || $"{item.Name} {item.Email}".Contains(ContactQuery, StringComparison.OrdinalIgnoreCase)))
            VisibleContacts.Add(contact);
        NoContacts = VisibleContacts.Count == 0;
        OnPropertyChanged(nameof(NoContacts));
    }

    private void UpdateTaskCount()
    {
        TaskCountText = $"{Tasks.Count(task => !task.Done)} open";
        OnPropertyChanged(nameof(TaskCountText));
    }

    private void RefreshAccountChoices()
    {
        AccountChoices.Clear();
        AccountChoices.Add("All accounts");
        if (IsDemo)
        {
            AccountChoices.Add("Work");
            AccountChoices.Add("Personal");
        }
        else
        {
            foreach (var account in _services.Config.Accounts)
                AccountChoices.Add(account.DisplayName);
        }

        // The last chosen account comes back after a restart, as long as it still exists.
        var remembered = IsDemo ? null : _services.Config.Layout.MailAccount;
        SelectedAccount = remembered is not null && AccountChoices.Contains(remembered) ? remembered : "All accounts";
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(IsAllAccounts));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(DemoBadge));
    }

    private IEnumerable<AccountConfig> AccountsInView()
    {
        if (SelectedAccount == "All accounts")
            return _services.Config.Accounts;
        return _services.Config.Accounts.Where(account => account.DisplayName == SelectedAccount);
    }

    /// <summary>
    /// Things a note can link to: upcoming events and contacts from the mail accounts (Microsoft 365, Google), loaded on
    /// demand. Accounts that are offline or lack the permission are skipped. Demo mode uses the sample data.
    /// </summary>
    public async Task<(IReadOnlyList<CalendarEntry> Events, IReadOnlyList<ContactEntry> Contacts)> LinkTargetsAsync()
    {
        if (IsDemo)
            return (SampleCatalog.Events().ToList(), SampleCatalog.Contacts().ToList());
        var events = new List<CalendarEntry>();
        var contacts = new List<ContactEntry>();
        foreach (var account in _services.Config.Accounts.Where(a => a.Kind != AccountKind.Manual))
        {
            try
            {
                var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
                secrets = await _services.OAuth.EnsureFreshAsync(account, secrets, account.Kind == AccountKind.Gmail ? Alondra1.Core.Security.TokenAudience.Google : Alondra1.Core.Security.TokenAudience.Graph, CancellationToken.None).ConfigureAwait(true);
                var start = DateTimeOffset.UtcNow.Date.AddDays(-14);
                if ((await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Calendar, CancellationToken.None).ConfigureAwait(true)).Available)
                    events.AddRange(await _services.Organizer.ListEventsAsync(account, secrets, start, start.AddDays(60), CancellationToken.None).ConfigureAwait(true));
                if ((await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Contacts, CancellationToken.None).ConfigureAwait(true)).Available)
                    contacts.AddRange(await _services.Organizer.ListContactsAsync(account, secrets, CancellationToken.None).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine("Link targets skipped for an account: " + ex.GetType().Name);
            }
        }

        return (events.OrderBy(e => e.Start).ToList(), contacts.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>Plain-text notes that earlier versions stored in the mail accounts' app folders (for a one-time import).</summary>
    public async Task<IReadOnlyList<NoteEntry>> AccountNotesAsync()
    {
        if (IsDemo)
            return SampleCatalog.Notes().ToList();
        var notes = new List<NoteEntry>();
        foreach (var account in _services.Config.Accounts.Where(a => a.Kind != AccountKind.Manual))
        {
            var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
            secrets = await _services.OAuth.EnsureFreshAsync(account, secrets, account.Kind == AccountKind.Gmail ? Alondra1.Core.Security.TokenAudience.Google : Alondra1.Core.Security.TokenAudience.Graph, CancellationToken.None).ConfigureAwait(true);
            if ((await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Notes, CancellationToken.None).ConfigureAwait(true)).Available)
                notes.AddRange(await _services.Organizer.ListNotesAsync(account, secrets, CancellationToken.None).ConfigureAwait(true));
        }

        return notes;
    }

    /// <summary>Notes spec §11: the event's meeting notes (the linked note, or a new one from the meeting template).</summary>
    private async Task OpenMeetingNotesAsync(AgendaEvent? item)
    {
        if (item is null || !Notes.IsAvailable)
            return;
        var e = item.Entry;
        SelectWorkspace("Notes");
        await Notes.OpenMeetingNotesAsync(new Alondra1.Core.Notes.NoteLink(Alondra1.Core.Notes.NoteLink.Event, e.Id, e.Subject, e.AccountId), e.Subject, item.When);
    }

    /// <summary>Opens the Calendar on the day of a linked event.</summary>
    public void OpenCalendarAt(DateTimeOffset when)
    {
        SelectWorkspace("Calendar");
        _month = new DateTime(when.Year, when.Month, 1);
        _chosenDay = when.Day;
        BuildCalendar();
    }

    /// <summary>Opens Contacts filtered to a name.</summary>
    public void OpenContact(string name)
    {
        SelectWorkspace("Contacts");
        ApplyContactQuery(name);
        ContactQueryChanged?.Invoke(name);
    }

    public event Action<string>? ContactQueryChanged;

    /// <summary>Opens a message a note links to, when it is in the list on this PC. False when it is not.</summary>
    public bool OpenMessageById(string id, string? accountId)
    {
        var found = Messages.FirstOrDefault(m => (m.MessageId == id || $"{m.FolderId}/{m.Uid}" == id) && (accountId is null || m.AccountId == accountId));
        if (found is null)
            return false;
        SelectWorkspace("Mail");
        SelectMessage(found);
        MessageOpened?.Invoke(found);
        return true;
    }

    public event Action<StoredMessage>? MessageOpened;

    // ---------- Shared search: module adapters and opening results ----------

    /// <summary>The local adapter of a module for the shared search. Nothing is sent to a server.</summary>
    private ISearchSource CreateSearchSource(SearchModule module) => module switch
    {
        SearchModule.Mail => new MailSearchSource(SearchAllMailAsync, MailAccounts, () => MailListView.Describe(Filter, AccountLabel)),
        SearchModule.Todo => new TaskSearchSource(() => Todo.Board),
        SearchModule.Notes => new NoteSearchSource(() => Notes.Board),
        SearchModule.Calendar => new CalendarSearchSource(() => _events, () => _eventsCoverage),
        _ => new ContactSearchSource(() => _contacts, () => _contactsCoverage),
    };

    /// <summary>All mail on this PC: the local index plus folders held only for this session.</summary>
    /// <remarks>
    /// Matches subject, sender, recipients and downloaded bodies (the index) and attachment file names. The Mail list
    /// filters (unread, sender, date range…) narrow it too; the result group says which filters applied.
    /// </remarks>
    private async Task<MailSearchPage> SearchAllMailAsync(string query, CancellationToken cancellationToken)
    {
        List<StoredMessage> found;
        int? indexTotal = null;
        var indexPage = 0;
        var byAttachment = await AttachmentMatchesAsync(new MailListFilter(AttachmentName: query), cancellationToken).ConfigureAwait(true) ?? new HashSet<MessageKey>();
        if (IsDemo)
        {
            var terms = Alondra1.Core.Search.SearchText.Terms(query);
            found = SampleCatalog.Messages().Where(m => Alondra1.Core.Search.SearchText.Matches(terms, m.Subject, m.FromName, m.FromAddress, m.Recipients, m.Preview)
                || byAttachment.Contains(new MessageKey(m.AccountId, m.FolderId, m.Uid))).ToList();
        }
        else
        {
            // Without list filters only a first page is read (the panel shows a few per module), with the full count.
            IReadOnlyList<StoredMessage> stored;
            if (Filter.IsActive)
                stored = await _services.Store.SearchAsync(null, null, query, cancellationToken).ConfigureAwait(true);
            else
            {
                (stored, var total) = await _services.Store.SearchPageAsync(null, null, query, SearchPageSize, cancellationToken).ConfigureAwait(true);
                (indexTotal, indexPage) = (total, stored.Count);
            }

            found = [.. stored, .. _services.Cache.Search(null, null, query)];
            // Attachment names are not in the text index: add those messages from their folders.
            var have = found.Select(m => new MessageKey(m.AccountId, m.FolderId, m.Uid)).ToHashSet();
            foreach (var folder in byAttachment.Where(k => !have.Contains(k)).GroupBy(k => (k.AccountId, k.FolderId)))
            {
                var uids = folder.Select(k => k.Uid).ToHashSet();
                var listed = await _services.Store.ListAsync(folder.Key.AccountId, folder.Key.FolderId, cancellationToken).ConfigureAwait(true);
                found.AddRange(listed.Concat(_services.Cache.List(folder.Key.AccountId, folder.Key.FolderId)).Where(m => uids.Contains(m.Uid)));
            }
        }

        if (!Filter.IsActive)
            return new MailSearchPage(found, indexTotal is { } t ? t + found.Count - indexPage : null);
        var keys = await AttachmentMatchesAsync(Filter, cancellationToken).ConfigureAwait(true);
        return new MailSearchPage(found.Where(m => MailListView.Matches(m, Filter, keys)).ToList());
    }

    private const int SearchPageSize = 200;

    private string AccountLabel(string id) => MailAccounts.FirstOrDefault(a => a.Id == id) is { } a ? ComposeIdentity.Label(a) : id;

    /// <summary>Messages with a matching attachment file name (names only, from attachments known on this PC); null without that filter.</summary>
    private async Task<IReadOnlySet<MessageKey>?> AttachmentMatchesAsync(MailListFilter filter, CancellationToken cancellationToken)
    {
        if (!MailListFilter.HasText(filter.AttachmentName))
            return null;
        var text = filter.AttachmentName!;
        if (IsDemo)
        {
            return SampleCatalog.Messages().Where(m => m.HasAttachments).SelectMany(m => SampleCatalog.BodyFor(m).Attachments)
                .Where(a => (filter.AccountId is null || a.AccountId == filter.AccountId) && MailListView.Contains(text, a.FileName))
                .Select(a => new MessageKey(a.AccountId, a.FolderId, a.Uid)).ToHashSet();
        }

        var keys = new HashSet<MessageKey>(await _services.Store.FindByAttachmentNameAsync(filter.AccountId, text, cancellationToken).ConfigureAwait(true));
        keys.UnionWith(_services.Cache.FindByAttachmentName(filter.AccountId, text));
        return keys;
    }

    /// <summary>
    /// Opens a search result in its own module with its identity (Mail: account and folder, then the message). Opening
    /// never changes, sends or downloads anything; a result that is gone says so.
    /// </summary>
    public async Task<bool> OpenSearchHitAsync(SearchHit hit)
    {
        switch (hit.Module)
        {
            case SearchModule.Mail:
                if (MailAccounts.FirstOrDefault(a => a.Id == hit.AccountId) is not { } account || hit.FolderId is null)
                    return false;
                var role = SpecialFolders.RoleOf(account, hit.FolderId);
                SelectWorkspace("Mail");
                SearchText = "";
                ApplyScope(role == FolderRole.Other ? new MailScope(account.Id, FolderRole.Other, hit.FolderId) : new MailScope(account.Id, role));
                await ReloadMessagesAsync().ConfigureAwait(true);
                var found = Messages.FirstOrDefault(m => m.AccountId == hit.AccountId && m.FolderId == hit.FolderId && m.Uid == hit.Uid);
                if (found is null)
                    return false;
                SelectMessage(found);
                MessageOpened?.Invoke(found);
                return true;
            case SearchModule.Todo:
                if (Todo.Board?.FindTask(hit.ObjectId) is null)
                    return false;
                Todo.OpenTask(hit.ObjectId);
                return true;
            case SearchModule.Notes:
                if (Notes.Board?.Find(hit.ObjectId) is null)
                    return false;
                SelectWorkspace("Notes");
                await Notes.OpenAsync(hit.ObjectId).ConfigureAwait(true);
                return true;
            case SearchModule.Calendar:
                if (_events.FirstOrDefault(e => e.Id == hit.ObjectId) is not { } ev)
                    return false;
                OpenCalendarAt(ev.Start.ToLocalTime());
                return true;
            default:
                if (_contacts.FirstOrDefault(c => c.Id == hit.ObjectId && c.AccountId == hit.AccountId) is not { } contact)
                    return false;
                OpenContact(string.IsNullOrWhiteSpace(contact.Email) ? contact.Name : contact.Email);
                return true;
        }
    }

    /// <summary>Creates a note from the selected email (text, and pictures when asked). Only on the user's request.</summary>
    public async Task CreateNoteFromMailAsync(bool withPictures)
    {
        if (SelectedMessage is not { } message || Notes is null)
            return;
        var text = ReaderBody ?? "";
        var pictures = new List<(string Name, byte[] Data)>();
        if (withPictures && !IsDemo && _services.Config.Accounts.FirstOrDefault(a => a.Id == message.AccountId) is { } account)
        {
            foreach (var attachment in Attachments.Where(a => a.IsPicture))
            {
                try
                {
                    using var stream = new MemoryStream();
                    var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
                    await _services.Mail.FetchAttachmentAsync(account, secrets, message.FolderId, message.Uid, attachment.FileName, stream, null, CancellationToken.None).ConfigureAwait(true);
                    pictures.Add((attachment.FileName, stream.ToArray()));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or MailKit.ServiceNotConnectedException or MailKit.ProtocolException or System.Net.Sockets.SocketException)
                {
                    Debug.WriteLine("Attachment skipped for the note: " + ex.GetType().Name);
                }
            }
        }

        SelectWorkspace("Notes");
        await Notes.CreateFromMailAsync(message, text, pictures).ConfigureAwait(true);
    }

    private AccountConfig? OrganizerAccount()
    {
        var named = SelectedAccount == "All accounts" ? null : SelectedAccount;
        return _services.Config.Accounts.FirstOrDefault(account =>
            account.Kind != AccountKind.Manual && (named is null || account.DisplayName == named))
            ?? _services.Config.Accounts.FirstOrDefault(account => account.Kind != AccountKind.Manual);
    }

    private OutgoingMail? BuildReply(bool all)
    {
        var message = SelectedMessage;
        if (message is null)
            return null;
        // The reply is sent from the account that received the message (Mail UI spec §3.2), never the first account.
        var account = MailAccounts.FirstOrDefault(a => a.Id == message.AccountId);
        return new OutgoingMail
        {
            AccountId = ComposeIdentity.DefaultAccount(MailAccounts, Scope, message.AccountId, null),
            To = all ? ComposeIdentity.ReplyAllRecipients(message, account?.Address ?? "") : [message.FromAddress],
            Subject = message.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? message.Subject : "Re: " + message.Subject,
            TextBody = $"\n\n---\n{ReaderBody}",
            InReplyTo = message.MessageId,
        };
    }

    private OutgoingMail? BuildForward()
    {
        var message = SelectedMessage;
        if (message is null)
            return null;
        return new OutgoingMail
        {
            AccountId = ComposeIdentity.DefaultAccount(MailAccounts, Scope, message.AccountId, null),
            Subject = message.Subject.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase) ? message.Subject : "Fw: " + message.Subject,
            TextBody = $"\n\n---\nFrom: {message.FromName}\n{ReaderBody}",
        };
    }

    private void UpdateStatus(string detail, ConnectionState? state = null, bool summary = false)
    {
        if (IsDemo)
        {
            StatusText = "Demo · Sample data";
            Connection = ConnectionState.Sample;
        }
        else if (_services.Config.Accounts.Count == 0)
        {
            StatusText = "No accounts";
            Connection = ConnectionState.NoAccounts;
        }
        else
        {
            var account = AccountsInView().FirstOrDefault() ?? _services.Config.Accounts[0];
            // A summary covers every account; other messages are about the account in view.
            StatusText = summary ? detail : $"{account.DisplayName} · {account.Mode} · {detail}";
            Connection = state ?? (Connection is ConnectionState.Sample or ConnectionState.NoAccounts ? ConnectionState.Ready : Connection);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ConnectionBrushKey));
        OnPropertyChanged(nameof(SyncToolTip));
        OnPropertyChanged(nameof(SyncAccessName));
        RaiseListState();
        ConnectionChanged?.Invoke();
    }

    /// <summary>Raised when a message's own state (read or unread) changed, so the list can redraw that row.</summary>
    public event Action<StoredMessage>? MessageStateChanged;

    /// <summary>Raised when the connection state changes, so the view can recolor the status dot.</summary>
    public event Action? ConnectionChanged;

    private static string AvailabilityLabel(StoredMessage message) => message.Availability switch
    {
        ItemAvailability.Downloaded => message.LocalOnly ? "On this PC" : "Available offline",
        ItemAvailability.RemoteOnly => "On the server",
        ItemAvailability.Pending => "Pending",
        ItemAvailability.Failed => "Failed",
        _ => "Reading pane",
    };

    private void SaveConfig() => _services.SaveConfig();

    private void RaiseChrome()
    {
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspaceGlyph));
        OnPropertyChanged(nameof(IsMail));
        OnPropertyChanged(nameof(IsCalendar));
        OnPropertyChanged(nameof(IsTasks));
        OnPropertyChanged(nameof(IsNotes));
        OnPropertyChanged(nameof(IsContacts));
        OnPropertyChanged(nameof(ShowOrganizer));
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(ShowSearchBox));
        OnPropertyChanged(nameof(ShowSearchButton));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(HasAccountSelector));
        OnPropertyChanged(nameof(ShowToolbarAccountSelector));
        OnPropertyChanged(nameof(HasSyncButton));
        OnPropertyChanged(nameof(CurrentWorkspace));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(DemoBadge));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(ShowToolbarFolders));
        OnPropertyChanged(nameof(ShowFolderCombo));
        OnPropertyChanged(nameof(PaneFoldersDock));
        OnPropertyChanged(nameof(FoldersDock));
        OnPropertyChanged(nameof(ReadingDock));
        OnPropertyChanged(nameof(FolderBreadth));
        OnPropertyChanged(nameof(ReadingBreadth));
        OnPropertyChanged(nameof(FoldersHidden));
        OnPropertyChanged(nameof(SidebarToggleLabel));
        OnPropertyChanged(nameof(FoldersAreBand));
        OnPropertyChanged(nameof(RowHeight));
    }

    private void RaiseReader()
    {
        OnPropertyChanged(nameof(ReadToggleLabel));
        OnPropertyChanged(nameof(ReadToggleIcon));
        OnPropertyChanged(nameof(SelectedMessage));
        OnPropertyChanged(nameof(ReaderStatus));
        OnPropertyChanged(nameof(ReaderSubject));
        OnPropertyChanged(nameof(ReaderSender));
        OnPropertyChanged(nameof(ReaderBody));
        OnPropertyChanged(nameof(ReaderMeta));
        OnPropertyChanged(nameof(HasAttachments));
    }

    public void ConfigStoreSave() => SaveConfig();

    /// <summary>Loads To Do or Google Tasks for the "Account tasks" view. Local tasks never call a provider.</summary>
    public Task RefreshAccountTasksAsync() => IsDemo || !IsTasks ? Task.CompletedTask : LoadOrganizerAsync();

    public void ServicesRefresh() => _services.RefreshOAuth();

    public async Task SaveDraft(AccountConfig account, OutgoingMail mail)
    {
        mail.FromAddress = account.Address;
        mail.FromName = account.DisplayName;
        await _services.Sync.SaveDraftAsync(account, mail, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Diagnostics for the reinstall check (--send-check): sends a message from the first manual account to the second,
    /// then syncs until it arrives (up to about two minutes). Returns a one-line result without addresses or content.
    /// </summary>
    public async Task<(bool Ok, string Detail)> RunSendCheckAsync()
    {
        var manual = _services.Config.Accounts.Where(account => account.Kind == AccountKind.Manual).ToList();
        if (manual.Count < 2)
            return (false, "send-check needs two manual accounts");
        var (from, to) = (manual[0], manual[1]);
        var token = "Alondra1 reinstall check " + Guid.NewGuid().ToString("N")[..12];
        var result = await Send(from, new OutgoingMail { To = [to.Address], Subject = token, TextBody = "Automatic check after a reinstall. You can delete this message." }).ConfigureAwait(true);
        if (result.Acceptance != SmtpAcceptance.Accepted)
            return (false, $"send-check: {from.DisplayName} could not send ({result.Acceptance})");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 5 : 10)).ConfigureAwait(true);
            var report = await _services.Sync.SyncAsync(to, CancellationToken.None).ConfigureAwait(true);
            if (!report.Ok)
                continue;
            var inbox = SpecialFolders.Resolve(to, "Inbox");
            var arrived = to.Mode == AccountMode.Personal
                ? (await _services.Store.ListAsync(to.Id, inbox, CancellationToken.None).ConfigureAwait(true)).Any(message => message.Subject == token)
                : _services.Cache.List(to.Id, inbox).Any(message => message.Subject == token);
            if (arrived)
                return (true, $"send-check: {from.DisplayName} → {to.DisplayName} received after {attempt + 1} sync(s)");
        }

        return (false, $"send-check: sent by {from.DisplayName} but not received by {to.DisplayName} in time");
    }

    public async Task<SendResult> Send(AccountConfig account, OutgoingMail mail)
    {
        mail.FromAddress = account.Address;
        mail.FromName = account.DisplayName;
        var result = await _services.Sync.SendAsync(account, mail, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
        UpdateStatus(result.Acceptance + (result.Error is null ? "" : " · " + result.Error));
        return result;
    }

}

/// <summary>An attachment in the reader: name, kind, size, and the stored description it came from.</summary>
public sealed class AttachmentRow(StoredAttachment source)
{
    public StoredAttachment Source { get; } = source;
    public string FileName => Source.FileName;
    public string SizeText => Source.Size switch
    {
        <= 0 => "",
        < 1024 => $"{Source.Size} B",
        < 1024 * 1024 => $"{Source.Size / 1024.0:0} KB",
        _ => $"{Source.Size / (1024.0 * 1024):0.0} MB",
    };
    public string Kind => Source.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Image" : "Attachment";
    public bool IsPicture => Kind == "Image";
    public string AccessName => string.IsNullOrEmpty(SizeText) ? FileName : $"{FileName}, {SizeText}";
}

public sealed class WorkspaceChoice : ObservableObject
{
    public WorkspaceChoice(string name, string glyph, bool soon, string icon, string? searchPlaceholder)
    {
        Name = name;
        Glyph = glyph;
        Soon = soon;
        Icon = icon;
        SearchPlaceholder = searchPlaceholder;
    }

    public string Name { get; }
    public string Glyph { get; }
    public bool Soon { get; }

    /// <summary>Alondra1 icon name (Themes/Icons.xaml).</summary>
    public string Icon { get; }

    /// <summary>Placeholder of the header search for this module, or null when the module has no search.</summary>
    public string? SearchPlaceholder { get; }

    public string AccessName => Soon ? Name + ", coming soon" : Name;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _inOverflow;

    /// <summary>True when the window is too narrow and this module is listed in the header's More menu instead.</summary>
    public bool InOverflow
    {
        get => _inOverflow;
        set => SetProperty(ref _inOverflow, value);
    }
}

/// <summary>Connection state shown next to the account selector (replaces the old status row).</summary>
public enum ConnectionState { Sample, NoAccounts, Ready, Syncing, Offline, Error }

/// <summary>An event in the Calendar's day list.</summary>
public sealed class AgendaEvent(CalendarEntry entry)
{
    public CalendarEntry Entry { get; } = entry;
    public string Subject => string.IsNullOrWhiteSpace(Entry.Subject) ? "(No title)" : Entry.Subject;
    public string Time => $"{Entry.Start.ToLocalTime():HH:mm}–{Entry.End.ToLocalTime():HH:mm}" + (string.IsNullOrEmpty(Entry.AccountName) ? "" : " · " + Entry.AccountName);
    /// <summary>Date and time as written at the top of the meeting notes.</summary>
    public string When => Entry.Start.ToLocalTime().ToString("dddd, d MMMM yyyy · HH:mm", System.Globalization.CultureInfo.CurrentCulture)
        + "–" + Entry.End.ToLocalTime().ToString(Entry.End.ToLocalTime().Date == Entry.Start.ToLocalTime().Date ? "HH:mm" : "d MMMM HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed class CalendarDay
{
    public string Text { get; init; } = "";
    public int Day { get; init; }
    public bool IsDay { get; init; }
    public bool HasEvent { get; init; }
    public bool IsSelected { get; init; }
    public string AccessName { get; init; } = "";
}

public sealed class TaskRow : ObservableObject
{
    public TaskRow(TaskEntry source)
    {
        Source = source;
        _done = source.Completed;
    }

    public TaskEntry Source { get; }
    public string Title => Source.Title;
    public string Folder => string.IsNullOrWhiteSpace(Source.ListName) ? "Tasks" : Source.ListName;
    private bool _done;
    public bool Done
    {
        get => _done;
        set => SetProperty(ref _done, value);
    }
}

public sealed class AppServices
{
    public required AppConfig Config { get; set; }
    public required ConfigFileStore ConfigStore { get; init; }
    public required SqlitePersonalStore Store { get; init; }
    public required DpapiSecretStore Secrets { get; init; }
    public required AttachmentFiles Files { get; init; }
    public required Alondra1.Mail.MailKitGateway Mail { get; init; }
    public required Alondra1.Providers.OAuthService OAuth { get; set; }
    public required Alondra1.Providers.OrganizerGateway Organizer { get; set; }
    public required Alondra1.Core.Sync.SyncCoordinator Sync { get; set; }
    public required Alondra1.Core.Ports.SessionCache Cache { get; init; }

    /// <summary>Local tasks (todo.db). Null only when the file could not be opened; <see cref="TaskStoreError"/> says why.</summary>
    public SqliteTaskStore? TaskStore { get; init; }
    public TaskFileStore? TaskFiles { get; init; }
    public Alondra1.Core.Tasks.TaskBoard? Tasks { get; init; }
    public Alondra1.Core.Cloud.CloudApi? CloudApi { get; init; }
    public Alondra1.Core.Cloud.CloudAccount? CloudAccount { get; init; }
    public Alondra1.Core.Cloud.CloudSyncEngine? CloudSync { get; init; }
    /// <summary>Cloud discovery (null when the user set a fixed server address); refreshed by the cloud controller.</summary>
    public Alondra1.Core.Cloud.Discovery.DiscoveryClient? Discovery { get; init; }
    public Alondra1.Core.Cloud.Discovery.EndpointSelector? Endpoints { get; init; }
    public Alondra1.Core.Notes.NotesBoard? NotesBoard { get; init; }
    public Alondra1.Core.Notes.NoteAssets? NoteAssets { get; init; }
    public string? TaskStoreError { get; init; }

    /// <summary>Alondra1's data folder (%LOCALAPPDATA%\alondra1, or a temporary folder in demo mode).</summary>
    public string Home { get; init; } = "";
    public Stopwatch Launch { get; } = Stopwatch.StartNew();

    public void SaveConfig() => ConfigStore.Save(Config);

    /// <summary>Applies a reviewed import (the Merged or Replaced result of <see cref="ConfigWorkflow.Preview"/>) and saves it.</summary>
    public void ApplyImported(AppConfig next)
    {
        next.DemoMode = Config.DemoMode && next.Accounts.Count == 0;
        Config.Appearance = next.Appearance;
        Config.Layout = next.Layout;
        Config.Reading = next.Reading;
        Config.OAuth = next.OAuth;
        Config.Accounts = next.Accounts;
        Config.SchemaVersion = ConfigSchema.Current;
        SaveConfig();
        RefreshOAuth();
    }

    public void RefreshOAuth()
    {
        OAuth = new Alondra1.Providers.OAuthService(Config.OAuth);
        Organizer = new Alondra1.Providers.OrganizerGateway(Config.OAuth);
        Sync = new SyncCoordinator(Mail, Store, Cache, Secrets, OAuth, new Alondra1.Core.Ports.SystemClock(), new NullConflicts());
    }

    public static async Task<AppServices> CreateAsync(string? home = null)
    {
        var root = home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Alondra1.Core.BuildEnvironment.DataFolder);
        Directory.CreateDirectory(root);
        var configStore = new ConfigFileStore(root);
        var config = configStore.Load();
        var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var secrets = new DpapiSecretStore(Path.Combine(root, "secrets"));
        var files = new AttachmentFiles(Path.Combine(root, "files"));
        var cache = new Alondra1.Core.Ports.SessionCache();
        var mail = new Alondra1.Mail.MailKitGateway();
        var oauth = new Alondra1.Providers.OAuthService(config.OAuth);
        var organizer = new Alondra1.Providers.OrganizerGateway(config.OAuth);
        var sync = new SyncCoordinator(mail, store, cache, secrets, oauth, new Alondra1.Core.Ports.SystemClock(), new NullConflicts());
        var (taskStore, taskFiles, board, taskError) = await OpenTasksAsync(root).ConfigureAwait(false);
        var (notesBoard, noteAssets) = await OpenNotesAsync(config, taskStore, root).ConfigureAwait(false);
        var (cloudApi, cloudAccount, cloudSync, discovery, endpoints) = CreateCloud(config, secrets, taskStore, board, notesBoard, noteAssets, taskFiles, root);
        return new AppServices
        {
            Discovery = discovery,
            Endpoints = endpoints,
            CloudApi = cloudApi,
            CloudAccount = cloudAccount,
            CloudSync = cloudSync,
            NotesBoard = notesBoard,
            NoteAssets = noteAssets,
            Home = root,
            TaskStore = taskStore,
            TaskFiles = taskFiles,
            Tasks = board,
            TaskStoreError = taskError,
            Config = config,
            ConfigStore = configStore,
            Store = store,
            Secrets = secrets,
            Files = files,
            Mail = mail,
            OAuth = oauth,
            Organizer = organizer,
            Sync = sync,
            Cache = cache,
        };
    }

    /// <summary>
    /// The optional Alondra1 cloud. Nothing here talks to the network until the user signs in; a bad address only
    /// disables the cloud features. Before the first sync, todo.db is copied to backups	odo-before-cloud-*.db.
    /// </summary>
    private static (Alondra1.Core.Cloud.CloudApi?, Alondra1.Core.Cloud.CloudAccount?, Alondra1.Core.Cloud.CloudSyncEngine?,
        Alondra1.Core.Cloud.Discovery.DiscoveryClient?, Alondra1.Core.Cloud.Discovery.EndpointSelector?) CreateCloud(
        AppConfig config, DpapiSecretStore secrets, SqliteTaskStore? taskStore, Alondra1.Core.Tasks.TaskBoard? board,
        Alondra1.Core.Notes.NotesBoard? notes, Alondra1.Core.Notes.NoteAssets? assets, TaskFileStore? taskFiles, string root)
    {
        if (taskStore is null || board is null)
            return (null, null, null, null, null);
        try
        {
            // Default address: endpoints come from signed discovery (cache or bundled list until the first refresh), with
            // failover between them. A fixed address (Settings or ALONDRA1_CLOUD_URL) must belong to this build's
            // environment: a dev build never talks to prod and the reverse.
            Alondra1.Core.Cloud.Discovery.DiscoveryClient? discovery = null;
            Alondra1.Core.Cloud.Discovery.EndpointSelector? endpoints = null;
            Alondra1.Core.Cloud.CloudApi api;
            if (config.Cloud.UsesDiscovery())
            {
                discovery = new Alondra1.Core.Cloud.Discovery.DiscoveryClient(Alondra1.Core.Cloud.Discovery.DiscoveryClient.DefaultHandler(),
                    Alondra1.Core.BuildEnvironment.Name, Alondra1.Core.BuildEnvironment.TrustedKeys, Alondra1.Core.BuildEnvironment.DiscoveryUrls,
                    Path.Combine(root, "discovery-cache.json"), Alondra1.Core.BuildEnvironment.BootstrapEndpoints, Alondra1.Core.BuildEnvironment.BootstrapValidUntil);
                var initial = discovery.Offline();
                if (initial is null)
                {
                    Debug.WriteLine("Alondra1 cloud disabled: no valid endpoint list (bundled list expired and no verified manifest cached)");
                    return (null, null, null, null, null);
                }
                endpoints = new Alondra1.Core.Cloud.Discovery.EndpointSelector(initial.Endpoints);
                var handler = new Alondra1.Core.Cloud.Discovery.EndpointFailoverHandler(endpoints, new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
                api = new Alondra1.Core.Cloud.CloudApi(initial.Endpoints[0].Url, new Alondra1.Core.Cloud.SecretStoreTokens(secrets), handler);
            }
            else
            {
                var address = config.Cloud.EffectiveUri();
                if (!Alondra1.Core.BuildEnvironment.AllowsCloudAddress(address))
                {
                    Debug.WriteLine($"Alondra1 cloud disabled: {address.Host} is not an address of the {Alondra1.Core.BuildEnvironment.Name} environment");
                    return (null, null, null, null, null);
                }
                api = new Alondra1.Core.Cloud.CloudApi(address, new Alondra1.Core.Cloud.SecretStoreTokens(secrets));
            }
            var account = new Alondra1.Core.Cloud.CloudAccount(api, taskStore);
            var engine = new Alondra1.Core.Cloud.CloudSyncEngine(board, taskStore, api, new Alondra1.Core.Ports.SystemClock(), async () =>
            {
                var folder = Path.Combine(root, "backups");
                Directory.CreateDirectory(folder);
                await taskStore.BackupToAsync(Path.Combine(folder, $"todo-before-cloud-{DateTime.Now:yyyyMMdd-HHmmss}.db")).ConfigureAwait(true);
            }, notes, assets, taskFiles);
            return (api, account, engine, discovery, endpoints);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            Debug.WriteLine("Alondra1 cloud disabled: " + ex.Message);
            return (null, null, null, null, null);
        }
    }

    /// <summary>
    /// Opens the local Notes module (notes live in todo.db; pictures below notes-assets). Notes in Trash longer than the
    /// retention period are deleted at start.
    /// </summary>
    private static async Task<(Alondra1.Core.Notes.NotesBoard?, Alondra1.Core.Notes.NoteAssets?)> OpenNotesAsync(AppConfig config, SqliteTaskStore? store, string root)
    {
        if (store is null)
            return (null, null);
        var clock = new Alondra1.Core.Ports.SystemClock();
        var notes = new Alondra1.Core.Notes.NotesBoard(store, clock);
        try
        {
            await notes.LoadAsync().ConfigureAwait(false);
            if (config.Notes.TrashRetentionDays > 0)
                await notes.PurgeTrashAsync(TimeSpan.FromDays(config.Notes.TrashRetentionDays)).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Debug.WriteLine("Notes unavailable: " + ex.Message);
            return (null, null);
        }

        var assets = new Alondra1.Core.Notes.NoteAssets(Path.Combine(root, "notes-assets"), store, () => clock.UtcNow)
        {
            MaxBytes = Math.Clamp(config.Notes.MaxPictureMB, 1, 100) * 1024L * 1024L,
        };
        return (notes, assets);
    }

    /// <summary>
    /// Opens the local task database. A failure (for example a file written by a newer Alondra1) disables only the
    /// To-do workspace; mail and the other workspaces keep working.
    /// </summary>
    private static async Task<(SqliteTaskStore?, TaskFileStore?, Alondra1.Core.Tasks.TaskBoard?, string?)> OpenTasksAsync(string root)
    {
        var taskStore = new SqliteTaskStore(Path.Combine(root, "todo.db"));
        try
        {
            await taskStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var board = new Alondra1.Core.Tasks.TaskBoard(taskStore, new Alondra1.Core.Ports.SystemClock());
            await board.LoadAsync().ConfigureAwait(false);
            var taskFiles = new TaskFileStore(Path.Combine(root, "task-files"));
            try
            {
                taskFiles.RemoveOrphans(board.Tasks);
                // Files attached before attachments synced are hashed (and uploaded) only after the user agrees
                // (Dev-only spec WIN-02); see CloudController.ApplyLegacyTaskFilesChoiceAsync.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine("Task file cleanup skipped: " + ex.Message);
            }

            return (taskStore, taskFiles, board, null);
        }
        catch (Exception ex) when (ex is Alondra1.Core.Tasks.TodoStorageException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await taskStore.DisposeAsync().ConfigureAwait(false);
            return (null, null, null, ex.Message);
        }
    }
}

public sealed class NullConflicts : Alondra1.Core.Ports.IConflictSink
{
    public void ReportPreservedBoth(string description) => Debug.WriteLine(description);
}
