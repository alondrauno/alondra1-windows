using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Alondra1.App.ViewModels;
using Alondra1.Core.Cloud;
using Alondra1.Core.Mail;

namespace Alondra1.App.Cloud;

/// <summary>
/// UI side of the optional Alondra1 cloud: the To-do status indicator, background Cloud Sync (every few minutes and
/// shortly after a local change) and the Mail Archive action. Everything here is a no-op until the user signs in and
/// turns Cloud Sync on; local work never waits for the network.
/// </summary>
public sealed partial class CloudController : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _periodic = new();
    private readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _enabled;

    public CloudController(AppServices services)
    {
        _services = services;
        _periodic.Interval = TimeSpan.FromMinutes(Math.Clamp(services.Config.Cloud.SyncMinutes, 1, 120));
        _periodic.Tick += async (_, _) => await SyncAsync();
        _soon.Tick += async (_, _) => { _soon.Stop(); await SyncAsync(); };
        if (Engine is not null)
        {
            Engine.StatusChanged += _ => Raise();
            // Asked after the sync run that found such files, never in the middle of it.
            Engine.LegacyTaskFilesWaiting += () => Dispatcher.CurrentDispatcher.BeginInvoke(async () => await AskAboutLegacyTaskFilesAsync(), DispatcherPriority.ApplicationIdle);
            if (services.Tasks is not null)
                services.Tasks.Changed += OnLocalChange;
        }
    }

    public CloudAccount? Account => _services.CloudAccount;
    public CloudSyncEngine? Engine => _services.CloudSync;
    public ICloudSyncStore? Store => _services.TaskStore;
    public bool Available => Engine is not null;
    /// <summary>Cloud Sync is set up and on.</summary>
    public bool IsEnabled => _enabled;
    public Uri? BaseUri => Available ? _services.Config.Cloud.EffectiveUri() : null;

    /// <summary>Shown in the To-do toolbar only once Cloud Sync was set up.</summary>
    public bool ShowIndicator => _enabled || Engine?.Status.State is SyncState.Paused or SyncState.AuthRequired or SyncState.ErrorPermanent;

    public string StatusText => Engine?.Status.State switch
    {
        SyncState.Synced => Engine.Status.Message ?? "Synced",
        SyncState.Syncing or SyncState.Bootstrapping => "Syncing…",
        SyncState.Offline => "Offline",
        SyncState.Paused => "Sync paused",
        SyncState.AuthRequired or SyncState.SignInRequired => "Sign in to sync",
        SyncState.ReadOnlyPlan => "Plan ended · read-only",
        SyncState.PlanRequired => "Plan required",
        SyncState.ErrorRetryable => "Will retry",
        SyncState.ErrorPermanent => "Sync stopped",
        SyncState.ReadyOff => "Cloud Sync off",
        _ => "Local only",
    };

    public string StatusDetail
    {
        get
        {
            if (Engine is null)
                return "Cloud features are not available.";
            var detail = Engine.Status.Message ?? StatusText;
            return Engine.Status.LastSynced is { } at ? $"{detail}. Last synced {at.ToLocalTime():t}." : detail;
        }
    }

    /// <summary>Green when synced, amber when something waits for the user or the network.</summary>
    public bool IsHealthy => Engine?.Status.State is SyncState.Synced or SyncState.Syncing or SyncState.Bootstrapping;

    private void Raise()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(ShowIndicator));
    }

    /// <summary>Resumes Cloud Sync at startup when it was on. Never throws.</summary>
    public async Task StartAsync()
    {
        if (Engine is null)
            return;
        try
        {
            _enabled = await Engine.EnabledAsync();
            await Engine.WarmTaskFileStateAsync();
            Raise();
            if (_enabled)
            {
                _periodic.Start();
                await SyncAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Cloud start skipped: " + ex.Message);
        }
    }

    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;
    private readonly Random _jitter = new();

    /// <summary>
    /// Refreshes the endpoint list from signed discovery when it is due (15 minutes with jitter) or after a sync that
    /// could not reach the cloud. Only runs once the user uses the cloud; failures keep the current list.
    /// </summary>
    private async Task RefreshEndpointsAsync(bool force)
    {
        if (_services.Discovery is null || _services.Endpoints is null || (!force && DateTimeOffset.UtcNow < _nextDiscovery))
            return;
        _nextDiscovery = DateTimeOffset.UtcNow + Alondra1.Core.Cloud.Discovery.DiscoveryClient.NextRefresh(_jitter);
        try
        {
            var problems = new List<string>();
            var set = await _services.Discovery.ResolveAsync(problems, CancellationToken.None);
            if (set is not null)
                _services.Endpoints.Update(set.Endpoints);
            if (problems.Count > 0)
                Debug.WriteLine("Cloud discovery: " + string.Join("; ", problems));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine("Cloud discovery skipped: " + ex.Message);
        }
    }

    public async Task<SyncStatus?> SyncAsync()
    {
        if (Engine is null)
            return null;
        try
        {
            await RefreshEndpointsAsync(force: false);
            var status = await Engine.SyncNowAsync();
            if (status?.State == SyncState.Offline)
                await RefreshEndpointsAsync(force: true); // sustained gateway failure: maybe the endpoints moved
            return status;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine("Cloud sync failed: " + ex.GetType().Name);
            return Engine.Status;
        }
        finally
        {
            Raise();
        }
    }

    public async Task<SyncStatus> EnableAsync(string tenantId)
    {
        var status = await Engine!.EnableAsync(tenantId);
        _enabled = await Engine.EnabledAsync();
        if (_enabled)
            _periodic.Start();
        Raise();
        return status;
    }

    private bool _asking;

    /// <summary>
    /// Files attached on this PC before Cloud Sync handled attachments stay here until the user agrees to upload them
    /// (Dev-only spec WIN-02). Asked once; the answer is kept and can be changed on the Cloud page. Closing the
    /// question keeps them on this PC.
    /// </summary>
    public async Task AskAboutLegacyTaskFilesAsync(bool again = false)
    {
        if (Engine is null || _asking)
            return;
        _asking = true;
        try
        {
            if (!again && await Engine.LegacyTaskFilesChoiceAsync() is not null)
                return;
            var tasks = await Engine.LegacyTaskFileTasksAsync();
            if (tasks.Count == 0 && !again)
                return;
            var files = tasks.Sum(t => t.Attachments.Count);
            var answer = Dialogs.Alert(
                $"Files attached to {tasks.Count} task(s) on this PC before Cloud Sync handled attachments (about {files} file(s)) are on this PC only.\n\n" +
                "Upload them to your cloud space so your other devices can open them?\n\n" +
                "Yes: upload existing attachments.\nNo: keep them on this PC (the tasks still sync, without these files).\n\n" +
                "Files you attach from now on sync with Cloud Sync. You can change this later on the Cloud page.",
                "Existing attachments", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            await ApplyLegacyTaskFilesChoiceAsync(answer == System.Windows.MessageBoxResult.Yes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Legacy attachment question skipped: " + ex.Message);
        }
        finally
        {
            _asking = false;
        }
    }

    public async Task ApplyLegacyTaskFilesChoiceAsync(bool upload)
    {
        if (Engine is null)
            return;
        await Engine.SetLegacyTaskFilesChoiceAsync(upload);
        if (!upload || _services.Tasks is not { } board || _services.TaskFiles is not { } files)
            return;
        // Hash what has no hash yet and re-queue the tasks, so the files go up before the tasks on the next sync.
        foreach (var task in await Engine.LegacyTaskFileTasksAsync())
        {
            await board.UpdateTaskAsync(task.Id, t =>
            {
                foreach (var a in t.Attachments.Where(a => a.Sha256 is null))
                {
                    if (files.Resolve(a) is { } path)
                    {
                        var (sha, head) = Alondra1.Persistence.TaskFileStore.Hash(path);
                        a.Sha256 = sha;
                        a.MediaType ??= Alondra1.Persistence.TaskFileStore.Sniff(head);
                    }
                }
            }, "Prepare files for sync", undoable: false);
        }
        if (_enabled)
            await SyncAsync();
    }

    public async Task PauseAsync()
    {
        await Engine!.PauseAsync();
        _enabled = false;
        _periodic.Stop();
        Raise();
    }

    public async Task LogoutAsync()
    {
        await Account!.LogoutAsync(CancellationToken.None);
        _enabled = false;
        _periodic.Stop();
        await SyncAsync(); // reports "Sign in to sync" without any request
    }

    private async void OnLocalChange()
    {
        if (!_enabled || Store is null)
            return;
        try
        {
            // Remote changes also raise Changed but queue nothing; only real local edits schedule a sync.
            if (await Store.PendingCountAsync(CancellationToken.None) > 0)
            {
                _soon.Stop();
                _soon.Start();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine("Cloud change check skipped: " + ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------ Mail Archive

    /// <summary>
    /// Copies one message (raw MIME, read from the mail server) into the paid cloud archive of the personal space.
    /// The mailbox is not changed and no mail credentials leave this PC. Returns a sentence for the user.
    /// </summary>
    public async Task<string> ArchiveMessageAsync(StoredMessage message)
    {
        if (Account is null)
            return "Cloud features are not available.";
        if (!await Account.IsSignedInAsync(CancellationToken.None))
            return "Sign in to your Alondra1 account first (Settings → Cloud).";
        var mail = _services.Config.Accounts.FirstOrDefault(a => a.Id == message.AccountId);
        if (mail is null || message.Uid <= 0)
            return "This message cannot be archived.";
        var space = await Account.PersonalSpaceAsync(CancellationToken.None);
        var rights = await Account.EntitlementsAsync(space.Id, CancellationToken.None);
        if (!rights.ArchiveIngest)
            return rights.State == "read_only"
                ? "The plan has ended: the archive is read-only, so new messages cannot be added."
                : "Mail Archive is part of the paid plans. See Settings → Cloud.";

        var secrets = await _services.Secrets.LoadAsync(mail.Id, CancellationToken.None);
        using var raw = new MemoryStream();
        await _services.Mail.FetchRawAsync(mail, secrets, message.FolderId, message.Uid, raw, CancellationToken.None);
        if (raw.Length > ArchiveClient.MaxMessageBytes)
            return "This message is larger than 25 MB and cannot be archived.";
        var (archived, duplicate) = await new ArchiveClient(Account.Api).ArchiveAsync(space.Id, raw.ToArray(), mail.Address, CancellationToken.None);
        return duplicate ? $"“{archived.Subject}” was already in the cloud archive." : $"“{archived.Subject}” was copied to the cloud archive.";
    }
}
