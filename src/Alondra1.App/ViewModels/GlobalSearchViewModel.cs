using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Alondra1.Core.Search;

namespace Alondra1.App.ViewModels;

/// <summary>One row of the search results panel: a module heading (with its count and coverage) or a result.</summary>
public sealed class SearchRow
{
    private SearchRow(SearchModule module, SearchHit? hit, string title, string detail, string meta)
    {
        Module = module;
        Hit = hit;
        Title = title;
        Detail = detail;
        Meta = meta;
    }

    public SearchModule Module { get; }
    public SearchHit? Hit { get; }
    public bool IsHeading => Hit is null;
    public string Title { get; }
    public string Detail { get; }
    public string Meta { get; }

    public string Icon => Module switch
    {
        SearchModule.Mail => "Mail",
        SearchModule.Calendar => "Calendar",
        SearchModule.Todo => "Todo",
        SearchModule.Notes => "Notes",
        _ => "Contacts",
    };

    public string AccessName => IsHeading ? $"{Title}. {Detail}" : $"{SearchScopes.ModuleName(Module)}: {Title}. {Meta}. {Detail}";

    public static SearchRow Heading(SearchGroup group)
    {
        var name = SearchScopes.ModuleName(group.Module);
        var count = group.Failed ? "error" : group.Total == 0 ? "no results" : group.Total > group.Hits.Count ? $"{group.Hits.Count} of {group.Total}" : group.Total.ToString(System.Globalization.CultureInfo.CurrentCulture);
        return new SearchRow(group.Module, null, $"{name} · {count}", group.Coverage, "");
    }

    public static SearchRow Result(SearchHit hit)
    {
        var when = hit.When is { } at ? Describe(at, hit.Module) : "";
        return new SearchRow(hit.Module, hit, hit.Title, hit.Snippet, string.IsNullOrEmpty(when) ? hit.Source : $"{hit.Source} · {when}");
    }

    private static string Describe(DateTimeOffset at, SearchModule module)
    {
        var local = at.ToLocalTime();
        if (module is SearchModule.Calendar)
            return local.ToString("ddd d MMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        if (module is SearchModule.Todo)
            return "Due " + local.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        return local.Date == DateTime.Today ? local.ToString("t", System.Globalization.CultureInfo.CurrentCulture) : local.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// The shared header search (Mail UI spec §8): the chosen reach, the label of what is searched, and the grouped results
/// panel for All, All mail and Calendar. In-place module filtering (the Mail list, tasks, notes, contacts) stays with
/// the modules; this class only says which of the two applies.
/// </summary>
public sealed partial class GlobalSearchViewModel : ObservableObject
{
    public const int PerModule = 8;
    private readonly SearchCoordinator _coordinator = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Func<SearchModule, ISearchSource> _source;
    private readonly Func<string> _mailScopeLabel;
    private readonly Action<SearchReach> _remember;
    private readonly List<SearchGroup> _groups = [];
    private IReadOnlyList<SearchModule> _searching = [];

    public GlobalSearchViewModel(Func<SearchModule, ISearchSource> source, Func<string> mailScopeLabel, SearchReach chosen, Action<SearchReach> remember)
    {
        _source = source;
        _mailScopeLabel = mailScopeLabel;
        _remember = remember;
        Chosen = chosen;
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunAsync();
        };
    }

    /// <summary>What the user picked; <see cref="Reach"/> is what applies in the current module.</summary>
    public SearchReach Chosen { get; private set; }
    public SearchModule Module { get; private set; } = SearchModule.Mail;
    public SearchReach Reach => SearchScopes.Effective(Chosen, Module);
    public string Query { get; private set; } = "";

    public ObservableCollection<SearchRow> Rows { get; } = [];

    /// <summary>True when this reach shows the results panel instead of filtering the module in place.</summary>
    public bool UsesPanel => SearchScopes.PanelModules(Reach, Module).Count > 0;

    [ObservableProperty]
    private bool _isPanelOpen;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isSearching;

    public string Placeholder => SearchScopes.Placeholder(Reach, Module);

    /// <summary>The scope chip: what this search covers, always visible next to the box.</summary>
    public string ScopeLabel => Reach switch
    {
        SearchReach.All => "All",
        SearchReach.AllMail => "All mail",
        _ => Module == SearchModule.Mail ? _mailScopeLabel() : SearchScopes.ModuleName(Module),
    };

    public string ScopeToolTip => Reach switch
    {
        SearchReach.All => "Searching Mail, Calendar, To-do, Notes and Contacts on this PC. Choose to search only this module.",
        SearchReach.AllMail => "Searching mail in every account and folder kept on this PC.",
        _ => Module == SearchModule.Mail ? $"Searching {_mailScopeLabel()}. Choose to search all mail or all of Alondra1." : $"Searching {SearchScopes.ModuleName(Module)}. Choose to search all of Alondra1.",
    };

    public string ScopeAccessName => $"Search scope: {ScopeLabel}";

    public bool HasResults => Rows.Any(r => !r.IsHeading);

    /// <summary>The module changed: the reach follows the rules (All stays, All mail falls back), the query is kept.</summary>
    public void SetModule(SearchModule module)
    {
        Module = module;
        RaiseScope();
        Rerun();
    }

    /// <summary>The user chose a reach in the scope menu. It is remembered (an explicit All persists across modules).</summary>
    public void Choose(SearchReach reach)
    {
        Chosen = reach;
        _remember(reach);
        RaiseScope();
        Rerun();
    }

    /// <summary>The Mail folder or account changed: refresh the chip text.</summary>
    public void MailScopeChanged()
    {
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(ScopeToolTip));
        OnPropertyChanged(nameof(ScopeAccessName));
    }

    /// <summary>Typing: debounced (300 ms) so obsolete queries never run; Enter runs at once.</summary>
    public void SetQuery(string text, bool now = false)
    {
        Query = text;
        _debounce.Stop();
        if (!UsesPanel || string.IsNullOrWhiteSpace(text))
        {
            Clear();
            return;
        }

        if (now)
            _ = RunAsync();
        else
            _debounce.Start();
    }

    public void Close() => IsPanelOpen = false;

    public void Clear()
    {
        _coordinator.Cancel();
        _groups.Clear();
        Rows.Clear();
        IsSearching = false;
        Status = "";
        IsPanelOpen = false;
        OnPropertyChanged(nameof(HasResults));
    }

    private void Rerun()
    {
        if (UsesPanel && !string.IsNullOrWhiteSpace(Query))
            _ = RunAsync();
        else
            Clear();
    }

    private void RaiseScope()
    {
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(Reach));
        OnPropertyChanged(nameof(UsesPanel));
        OnPropertyChanged(nameof(Placeholder));
        MailScopeChanged();
    }

    public async Task RunAsync()
    {
        var query = Query.Trim();
        var modules = SearchScopes.PanelModules(Reach, Module);
        if (query.Length == 0 || modules.Count == 0)
        {
            Clear();
            return;
        }

        _searching = modules;
        _groups.Clear();
        Rows.Clear();
        IsSearching = true;
        IsPanelOpen = true;
        Status = $"Searching {Describe(modules)} for “{query}”…";
        var sources = modules.Select(_source).ToList();
        var finished = await _coordinator.RunAsync(query, sources, PerModule, group =>
        {
            _groups.Add(group);
            Rebuild();
            Status = $"Searching {Describe(modules)} for “{query}”… {_groups.Count} of {modules.Count} done";
        });
        if (!finished)
            return;
        IsSearching = false;
        var total = _groups.Where(g => !g.Failed).Sum(g => g.Total);
        var failed = _groups.Where(g => g.Failed).Select(g => SearchScopes.ModuleName(g.Module)).ToList();
        Status = (total == 0 ? $"No results for “{query}” in {Describe(modules)}." : $"{total} {(total == 1 ? "result" : "results")} for “{query}” in {Describe(modules)}.")
            + (failed.Count > 0 ? $" Not searched: {string.Join(", ", failed)}." : "");
    }

    /// <summary>Groups in the fixed module order, so results don't jump around as modules finish.</summary>
    private void Rebuild()
    {
        Rows.Clear();
        foreach (var module in _searching)
        {
            if (_groups.FirstOrDefault(g => g.Module == module) is not { } group)
                continue;
            Rows.Add(SearchRow.Heading(group));
            foreach (var hit in group.Hits)
                Rows.Add(SearchRow.Result(hit));
        }

        OnPropertyChanged(nameof(HasResults));
    }

    private string Describe(IReadOnlyList<SearchModule> modules) =>
        modules.Count > 1 ? "all of Alondra1" : Reach == SearchReach.AllMail ? "all mail" : SearchScopes.ModuleName(modules[0]);
}
