using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Notes;
using Alondra1.Core.Organizer;
using Alondra1.Core.Tasks;

namespace Alondra1.Core.Search;

/// <summary>The modules the header search covers (Mail UI spec §8.1). Chat is not a search destination.</summary>
public enum SearchModule { Mail, Calendar, Todo, Notes, Contacts }

/// <summary>
/// How far the header search reaches. <see cref="Module"/> filters the current module in place (Mail: the folder and
/// account the list shows); <see cref="AllMail"/> searches every account and folder kept on this PC;
/// <see cref="All"/> searches all five modules. Search never broadens on its own.
/// </summary>
public enum SearchReach { Module, AllMail, All }

/// <summary>One result: enough identity to open the object in its own module, and text to recognize it.</summary>
public sealed record SearchHit(
    SearchModule Module,
    string ObjectId,
    string Title,
    string Snippet,
    string Source,
    DateTimeOffset? When,
    string? AccountId = null,
    string? FolderId = null,
    long Uid = 0);

/// <summary>The results of one module: the first hits, how many matched, and what was (not) searched.</summary>
public sealed record SearchGroup(SearchModule Module, IReadOnlyList<SearchHit> Hits, int Total, string Coverage, bool Failed = false)
{
    public static SearchGroup Error(SearchModule module) =>
        new(module, [], 0, $"{SearchScopes.ModuleName(module)} couldn't be searched. Other results are still shown.", Failed: true);
}

/// <summary>A module adapter of the shared search: local data only, no query text leaves the PC.</summary>
public interface ISearchSource
{
    SearchModule Module { get; }
    Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>Names, labels and the rule that keeps the chosen reach when the user changes modules.</summary>
public static class SearchScopes
{
    public static readonly IReadOnlyList<SearchModule> AllModules =
        [SearchModule.Mail, SearchModule.Calendar, SearchModule.Todo, SearchModule.Notes, SearchModule.Contacts];

    public static string ModuleName(SearchModule module) => module switch
    {
        SearchModule.Todo => "To-do",
        _ => module.ToString(),
    };

    /// <summary>The module of a workspace name, or null (an unknown or removed module such as Chat).</summary>
    public static SearchModule? FromWorkspace(string? workspace) => workspace switch
    {
        "Mail" => SearchModule.Mail,
        "Calendar" => SearchModule.Calendar,
        "To-do" => SearchModule.Todo,
        "Notes" => SearchModule.Notes,
        "Contacts" => SearchModule.Contacts,
        _ => null,
    };

    /// <summary>
    /// The reach after switching to <paramref name="module"/>: an explicit All stays All; All mail is a Mail choice, so
    /// another module falls back to its own search (never to a broader one).
    /// </summary>
    public static SearchReach Effective(SearchReach chosen, SearchModule module) =>
        chosen == SearchReach.AllMail && module != SearchModule.Mail ? SearchReach.Module : chosen;

    /// <summary>The placeholder and accessible name of the box: "Search mail", "Search all mail", "Search all".</summary>
    public static string Placeholder(SearchReach reach, SearchModule module) => reach switch
    {
        SearchReach.All => "Search all",
        SearchReach.AllMail => "Search all mail",
        _ => module switch
        {
            SearchModule.Todo => "Search tasks",
            _ => $"Search {ModuleName(module).ToLowerInvariant()}",
        },
    };

    /// <summary>The modules a results panel searches, or empty when the module filters its own view in place.</summary>
    public static IReadOnlyList<SearchModule> PanelModules(SearchReach reach, SearchModule module) => reach switch
    {
        SearchReach.All => AllModules,
        SearchReach.AllMail => [SearchModule.Mail],
        // Calendar has no list to filter, so its own search shows results too.
        _ => module == SearchModule.Calendar ? [SearchModule.Calendar] : [],
    };

    public static SearchReach ParseReach(string? text) => Enum.TryParse<SearchReach>(text, out var reach) ? reach : SearchReach.Module;
}

/// <summary>Case-insensitive matching of every word of a query, and short snippets around the first match.</summary>
public static class SearchText
{
    public static string[] Terms(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('"')).Where(t => t.Length > 0).ToArray();

    /// <summary>True when every term occurs in at least one of the fields.</summary>
    public static bool Matches(string[] terms, params string?[] fields) =>
        terms.Length > 0 && terms.All(term => fields.Any(field => field?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true));

    /// <summary>Plain text around the first term found (single line, at most about 2 × radius characters).</summary>
    public static string Snippet(string? text, string[] terms, int radius = 60)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= radius * 2)
            return flat;
        var at = terms.Select(t => flat.IndexOf(t, StringComparison.CurrentCultureIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - radius / 2);
        var end = Math.Min(flat.Length, start + radius * 2);
        return (start > 0 ? "…" : "") + flat[start..end].Trim() + (end < flat.Length ? "…" : "");
    }
}

/// <summary>
/// Runs the module adapters of one query side by side. Each group is reported as soon as it is ready, so slow modules
/// never hold back fast ones; a failing module reports an error group and the others continue. A newer query cancels
/// the older one, and late results of an older query are dropped (Mail UI spec §8.2).
/// </summary>
public sealed class SearchCoordinator
{
    private CancellationTokenSource? _current;
    private long _request;

    public long CurrentRequest => Interlocked.Read(ref _request);

    public void Cancel()
    {
        Interlocked.Increment(ref _request);
        _current?.Cancel();
        _current = null;
    }

    /// <summary>
    /// Searches the sources and calls <paramref name="report"/> for each finished group of this request (on the caller's
    /// context). Returns false when the request was replaced before it finished.
    /// </summary>
    public async Task<bool> RunAsync(string query, IReadOnlyList<ISearchSource> sources, int limit, Action<SearchGroup> report)
    {
        Cancel();
        var request = Interlocked.Increment(ref _request);
        var cts = new CancellationTokenSource();
        _current = cts;
        var pending = sources.Select(source => Run(source, query, limit, cts.Token)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(true);
            pending.Remove(done);
            var group = await done.ConfigureAwait(true);
            if (request != CurrentRequest)
                return false;
            report(group);
        }

        return request == CurrentRequest;
    }

    private static async Task<SearchGroup> Run(ISearchSource source, string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            return await source.SearchAsync(query, limit, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new SearchGroup(source.Module, [], 0, "Cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only the error category is logged: never the query, message text or addresses.
            System.Diagnostics.Debug.WriteLine($"Search source {source.Module} failed: {ex.GetType().Name}");
            return SearchGroup.Error(source.Module);
        }
    }
}

/// <summary>
/// Mail kept on this PC across every account and folder (or one account), from the local full-text index. Messages
/// whose body was not downloaded match on their headers only, and the coverage says so.
/// </summary>
public sealed class MailSearchSource(
    Func<string, CancellationToken, Task<IReadOnlyList<StoredMessage>>> search,
    IReadOnlyList<AccountConfig> accounts) : ISearchSource
{
    public SearchModule Module => SearchModule.Mail;

    public async Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var found = await search(query, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var names = AccountNames(accounts);
        var terms = SearchText.Terms(query);
        var unique = found
            .Where(m => names.ContainsKey(m.AccountId))
            .GroupBy(m => (m.AccountId, m.FolderId, m.Uid)).Select(g => g.First())
            .OrderByDescending(m => m.ReceivedUtc).ThenBy(m => m.AccountId, StringComparer.Ordinal).ThenBy(m => m.FolderId, StringComparer.Ordinal).ThenByDescending(m => m.Uid)
            .ToList();
        var hits = unique.Take(limit).Select(m =>
        {
            var account = accounts.First(a => a.Id == m.AccountId);
            var from = string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName;
            return new SearchHit(SearchModule.Mail, $"{m.AccountId}/{m.FolderId}/{m.Uid}",
                string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject,
                $"{from} — {SearchText.Snippet(m.Preview, terms)}",
                $"{names[m.AccountId]} · {SpecialFolders.LabelOf(account, m.FolderId)}",
                m.ReceivedUtc, m.AccountId, m.FolderId, m.Uid);
        }).ToList();
        var headersOnly = unique.Any(m => !m.BodyDownloaded);
        var coverage = accounts.Count == 0 ? "No mail account is set up."
            : $"Mail kept on this PC in {Plural(accounts.Count, "account")}" + (headersOnly ? "; some messages only by sender and subject (body not downloaded)." : ".");
        return new SearchGroup(SearchModule.Mail, hits, unique.Count, coverage);
    }

    /// <summary>Account names for results; names used twice get the address so results are never mixed up.</summary>
    public static Dictionary<string, string> AccountNames(IReadOnlyList<AccountConfig> accounts)
    {
        string Name(AccountConfig a) => string.IsNullOrWhiteSpace(a.DisplayName) ? a.Address : a.DisplayName;
        var duplicates = accounts.GroupBy(Name, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return accounts.GroupBy(a => a.Id).Select(g => g.First())
            .ToDictionary(a => a.Id, a => duplicates.Contains(Name(a)) && !string.IsNullOrWhiteSpace(a.Address) && Name(a) != a.Address ? $"{Name(a)} ({a.Address})" : Name(a));
    }

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>Tasks in every list on this PC (title, notes, list name and steps).</summary>
public sealed class TaskSearchSource(Func<TaskBoard?> board) : ISearchSource
{
    public SearchModule Module => SearchModule.Todo;

    public Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (board() is not { } tasks)
            return Task.FromResult(new SearchGroup(Module, [], 0, "To-do isn't available on this PC."));
        var terms = SearchText.Terms(query);
        var found = tasks.Search(query).ToList();
        var hits = found.Take(limit).Select(t => new SearchHit(Module, t.Id, t.Title,
            SearchText.Snippet(t.Notes, terms), $"{tasks.ListName(t.ListId)} · {(t.Completed ? "Completed" : "Open")}",
            t.DueDate is { } due ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue)) : null)).ToList();
        return Task.FromResult(new SearchGroup(Module, hits, found.Count, "All task lists on this PC."));
    }
}

/// <summary>Notes on this PC from the notes index (titles and text, including lists and tables). Notes in the trash are left out.</summary>
public sealed class NoteSearchSource(Func<NotesBoard?> board) : ISearchSource
{
    public SearchModule Module => SearchModule.Notes;

    public async Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (board() is not { } notes)
            return new SearchGroup(Module, [], 0, "Notes aren't available on this PC.");
        var terms = SearchText.Terms(query);
        var ids = await notes.SearchAsync(query, 500, cancellationToken).ConfigureAwait(true);
        var found = ids.Select(notes.Find).OfType<NoteSummary>().Where(n => n.TrashedUtc is null)
            .OrderByDescending(n => n.ModifiedUtc).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        var hits = found.Take(limit).Select(n => new SearchHit(Module, n.Id, n.DisplayTitle, SearchText.Snippet(n.Preview, terms),
            n.ArchivedUtc is null ? notes.FolderName(n.FolderId) : "Archive", n.ModifiedUtc)).ToList();
        return new SearchGroup(Module, hits, found.Count, "All notes on this PC (not the trash).");
    }
}

/// <summary>
/// Calendar events already loaded on this PC. Events come from the accounts' providers, so the coverage names what
/// was searched instead of claiming the whole calendar.
/// </summary>
public sealed class CalendarSearchSource(Func<IReadOnlyList<CalendarEntry>> loaded, Func<string> coverage) : ISearchSource
{
    public SearchModule Module => SearchModule.Calendar;

    public Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var terms = SearchText.Terms(query);
        var found = loaded().Where(e => SearchText.Matches(terms, e.Subject, e.AccountName))
            .OrderBy(e => e.Start).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var hits = found.Take(limit).Select(e => new SearchHit(Module, e.Id, string.IsNullOrWhiteSpace(e.Subject) ? "(no title)" : e.Subject,
            "", e.AccountName, e.Start, e.AccountId)).ToList();
        return Task.FromResult(new SearchGroup(Module, hits, found.Count, coverage()));
    }
}

/// <summary>Contacts already loaded on this PC (name, email, phone); the account keeps duplicates apart.</summary>
public sealed class ContactSearchSource(Func<IReadOnlyList<ContactEntry>> loaded, Func<string> coverage) : ISearchSource
{
    public SearchModule Module => SearchModule.Contacts;

    public Task<SearchGroup> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var terms = SearchText.Terms(query);
        var found = loaded().Where(c => SearchText.Matches(terms, c.Name, c.Email, c.Phone))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.AccountId, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var hits = found.Take(limit).Select(c => new SearchHit(Module, c.Id, string.IsNullOrWhiteSpace(c.Name) ? c.Email : c.Name,
            string.Join(" · ", new[] { c.Email, c.Phone }.Where(s => !string.IsNullOrWhiteSpace(s))), c.AccountName, null, c.AccountId)).ToList();
        return Task.FromResult(new SearchGroup(Module, hits, found.Count, coverage()));
    }
}
