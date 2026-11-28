using System.Text.Json;
using System.Text.Json.Nodes;
using Alondra1.Core.Notes;
using Alondra1.Core.Ports;
using Alondra1.Core.Tasks;

namespace Alondra1.Core.Cloud;

public sealed record MergePreview(int LocalTasks, int LocalLists, int LocalGroups, int LocalNotes = 0);

/// <summary>
/// Optional paid Cloud Sync of local tasks, lists and list groups (spec W3/W4). todo.db stays the source of the UI; every
/// local change is queued in the same transaction (<see cref="ICloudSyncStore"/>), pushed later, and changes from other
/// devices come back through <see cref="TaskBoard.ApplyRemoteAsync"/>, so TaskBoard remains the only writer.
/// Conflicts keep both versions; server errors never delete local data. Same protocol and rules as the Android app.
/// Call it from the UI thread (TaskBoard is single-threaded).
/// </summary>
public sealed class CloudSyncEngine(TaskBoard board, ICloudSyncStore store, CloudApi api, IClock clock, Func<Task>? backup = null,
    NotesBoard? notes = null, NoteAssets? assets = null, ITaskFiles? taskFiles = null, Alondra1.Core.Tags.TagBoard? tags = null)
{
    /// <summary>Attachment limits; the server's once known (GET /v1/task-files/limits).</summary>
    public long MaxTaskFileBytes { get; private set; } = 25L << 20;
    public int MaxTaskFilesPerTask { get; private set; } = 10;

    /// <summary>Push requests stay below the server's 8 MiB body limit (rich notes can be large).</summary>
    private const int MaxPushBytes = 6 << 20;
    private const int MaxDownloadsPerRun = 200;

    private static readonly JsonSerializerOptions Json = TodoTransfer.CreateOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncStatus Status { get; private set; } = new(SyncState.LocalOnly);
    public event Action<SyncStatus>? StatusChanged;

    /// <summary>
    /// Raised (once per run) when files attached on this PC before Cloud Sync handled attachments are waiting for the
    /// user's decision (Dev-only spec WIN-02). Until <see cref="SetLegacyTaskFilesChoiceAsync"/> says upload, they stay
    /// on this PC: not uploaded, and their hash is not sent, so no other device is told they exist in the cloud.
    /// </summary>
    public event Action? LegacyTaskFilesWaiting;

    private const string LegacyChoiceKey = "task_files_legacy";
    private const string FilesSinceKey = "task_files_since";

    private void Set(SyncState state, string? message = null, bool synced = false)
    {
        Status = new SyncStatus(state, synced ? clock.UtcNow : Status.LastSynced, message);
        StatusChanged?.Invoke(Status);
    }

    public async Task<bool> EnabledAsync(CancellationToken ct = default) => await store.GetMetaAsync("sync_enabled", ct).ConfigureAwait(true) == "1";
    public Task<string?> TenantAsync(CancellationToken ct = default) => store.GetMetaAsync("tenant", ct);

    public MergePreview Preview() => new(board.Tasks.Count, board.Lists.Count, board.Groups.Count, notes?.Notes.Count ?? 0);

    // ------------------------------------------------------------------ enable / pause

    /// <summary>
    /// First sync with <paramref name="tenantId"/>, after the user confirmed the merge preview: back up todo.db, start
    /// queueing local changes, download the cloud copy, merge it (nothing is replaced or deleted), then sync.
    /// </summary>
    public async Task<SyncStatus> EnableAsync(string tenantId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            Set(SyncState.Bootstrapping);
            if (backup is not null)
                await backup().ConfigureAwait(true);
            await store.SetMetaAsync("tenant", tenantId, ct).ConfigureAwait(true);
            await store.SetMetaAsync("bound_user", await store.GetMetaAsync("user_id", ct).ConfigureAwait(true), ct).ConfigureAwait(true);
            await store.SetMetaAsync("sync_enabled", "1", ct).ConfigureAwait(true);
            FilesSyncOn = true;
            if (taskFiles is not null)
                await FilesSinceAsync(ct).ConfigureAwait(true); // files attached before now need the user's consent to upload
            await store.SetRecordingAsync(true, ct).ConfigureAwait(true);
            await BootstrapAsync(tenantId, ct).ConfigureAwait(true);
        }
        catch (CloudApiException e)
        {
            return Fail(e);
        }
        finally
        {
            _gate.Release();
        }

        return await SyncNowAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Stops syncing; local changes keep being queued so nothing is missed when sync resumes.</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        await store.SetMetaAsync("sync_enabled", null, ct).ConfigureAwait(true);
        FilesSyncOn = false;
        Set(SyncState.Paused);
    }

    // ------------------------------------------------------------------ bootstrap

    private async Task BootstrapAsync(string tenantId, CancellationToken ct)
    {
        string? pageToken = null;
        string cursor;
        var remote = new HashSet<(string, string)>();
        do
        {
            var page = await api.CallAsync(HttpMethod.Get, "/v1/sync/bootstrap?limit=500" + (pageToken is null ? "" : "&page_token=" + Uri.EscapeDataString(pageToken)),
                tenant: tenantId, cancellationToken: ct).ConfigureAwait(true);
            cursor = page["cursor"]!.GetValue<string>();
            var items = (page["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            foreach (var item in items)
            {
                if (item["resource_type"]?.GetValue<string>() is { } t && item["resource_id"]?.GetValue<string>() is { } id)
                    remote.Add((t, id));
            }

            await ApplyAsync(items, null, ct).ConfigureAwait(true);
            pageToken = page["next_page_token"]?.GetValue<string>();
        }
        while (pageToken is not null);

        await AdoptCloudDefaultListAsync(ct).ConfigureAwait(true);
        var versions = await store.VersionsAsync(ct).ConfigureAwait(true);
        foreach (var g in board.Groups.Where(g => !versions.ContainsKey((CloudKinds.Group, g.Id)) && !remote.Contains((CloudKinds.Group, g.Id))))
            await store.EnqueueAsync(CloudKinds.Group, g.Id, false, ct).ConfigureAwait(true);
        foreach (var l in board.Lists.Where(l => !versions.ContainsKey((CloudKinds.List, l.Id)) && !remote.Contains((CloudKinds.List, l.Id))))
            await store.EnqueueAsync(CloudKinds.List, l.Id, false, ct).ConfigureAwait(true);
        foreach (var t in board.Tasks.Where(t => !versions.ContainsKey((CloudKinds.Task, t.Id)) && !remote.Contains((CloudKinds.Task, t.Id))))
            await store.EnqueueAsync(CloudKinds.Task, t.Id, false, ct).ConfigureAwait(true);
        if (notes is not null)
        {
            foreach (var f in notes.Folders.Where(f => !versions.ContainsKey((CloudKinds.NoteFolder, f.Id)) && !remote.Contains((CloudKinds.NoteFolder, f.Id))))
                await store.EnqueueAsync(CloudKinds.NoteFolder, f.Id, false, ct).ConfigureAwait(true);
            foreach (var n in notes.Notes.Where(n => !versions.ContainsKey((CloudKinds.Note, n.Id)) && !remote.Contains((CloudKinds.Note, n.Id))))
                await store.EnqueueAsync(CloudKinds.Note, n.Id, false, ct).ConfigureAwait(true);
        }

        if (tags is not null)
        {
            foreach (var t in tags.Tags.Where(t => !versions.ContainsKey((CloudKinds.Tag, t.Id)) && !remote.Contains((CloudKinds.Tag, t.Id))))
                await store.EnqueueAsync(CloudKinds.Tag, t.Id, false, ct).ConfigureAwait(true);
            foreach (var l in tags.AllLinks.Where(l => Alondra1.Core.Tags.TagRules.IsSynced(l) && !versions.ContainsKey((CloudKinds.TagLink, l.Id)) && !remote.Contains((CloudKinds.TagLink, l.Id))))
                await store.EnqueueAsync(CloudKinds.TagLink, l.Id, false, ct).ConfigureAwait(true);
        }
        await store.SetMetaAsync("cursor", cursor, ct).ConfigureAwait(true);
    }

    /// <summary>The cloud keeps one default list per person: a local default that never synced hands its tasks over.</summary>
    private async Task AdoptCloudDefaultListAsync(CancellationToken ct)
    {
        var versions = await store.VersionsAsync(ct).ConfigureAwait(true);
        var cloudDefault = board.Lists.FirstOrDefault(l => l.IsDefault && versions.ContainsKey((CloudKinds.List, l.Id)));
        if (cloudDefault is null)
            return;
        foreach (var local in board.Lists.Where(l => l.IsDefault && l.Id != cloudDefault.Id && !versions.ContainsKey((CloudKinds.List, l.Id))).ToList())
            await board.MergeDefaultListAsync(local.Id, cloudDefault.Id).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ sync now

    public async Task<SyncStatus> SyncNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var tenant = await store.GetMetaAsync("tenant", ct).ConfigureAwait(true);
            if (await api.Tokens.LoadRefreshAsync(ct).ConfigureAwait(true) is null)
                Set(SyncState.SignInRequired);
            else if (tenant is null)
                Set(SyncState.ReadyOff);
            else if (!await EnabledAsync(ct).ConfigureAwait(true))
            {
                FilesSyncOn = false;
                Set(SyncState.Paused);
            }
            else
            {
                Set(SyncState.Syncing);
                FilesSyncOn = true;
                if (taskFiles is not null)
                    await FilesSinceAsync(ct).ConfigureAwait(true);
                await PushAsync(tenant, ct).ConfigureAwait(true);
                await PullAsync(tenant, ct).ConfigureAwait(true);
                await MergeDuplicateTagsAsync(ct).ConfigureAwait(true);
                await DownloadAssetsAsync(tenant, ct).ConfigureAwait(true);
                await RefreshTaskFileLimitsAsync(tenant, ct).ConfigureAwait(true);
                // Conflict copies are created while pushing: send them in this same run, not at the next sync.
                if ((await store.OutboxAsync(ct).ConfigureAwait(true)).Any(i => !i.Attempted))
                    await PushAsync(tenant, ct).ConfigureAwait(true);
                var pending = await store.PendingCountAsync(ct).ConfigureAwait(true);
                Set(SyncState.Synced, pending > 0 ? $"{pending} changes still waiting" : null, synced: true);
            }
        }
        catch (CloudApiException e)
        {
            Fail(e);
        }
        finally
        {
            _gate.Release();
        }

        return Status;
    }

    private SyncStatus Fail(CloudApiException e)
    {
        if (e.IsNetwork)
            Set(SyncState.Offline, "Offline. Changes are saved on this PC and will sync later.");
        else if (e.Code == "ENTITLEMENT_REQUIRED")
        {
            var readOnly = e.Error?["entitlement_state"]?.GetValue<string>() == "read_only";
            Set(readOnly ? SyncState.ReadOnlyPlan : SyncState.PlanRequired,
                readOnly ? "The plan ended: cloud data is read-only. New changes stay on this PC." : "Cloud Sync needs a paid plan. Your tasks stay on this PC.");
        }
        else if (e.Code == "UNAUTHENTICATED" || e.Status == 401)
            Set(SyncState.AuthRequired, "Sign in again to resume Cloud Sync. Nothing was deleted.");
        else if (e.Code == "TENANT_NOT_FOUND")
        {
            _ = store.SetMetaAsync("sync_enabled", null, CancellationToken.None);
            Set(SyncState.ErrorPermanent, "You no longer have access to this space. Sync stopped; your tasks are kept on this PC.");
        }
        else if (e.Status is >= 500 or 429 or 408)
            Set(SyncState.ErrorRetryable, "The cloud is busy. Alondra1 will retry.");
        else
            Set(SyncState.ErrorPermanent, e.Message);
        return Status;
    }

    // ------------------------------------------------------------------ push

    private async Task PushAsync(string tenant, CancellationToken ct)
    {
        var outbox = (await store.OutboxAsync(ct).ConfigureAwait(true))
            .OrderBy(i => Array.IndexOf(CloudKinds.Order, i.EntityType)).ThenBy(i => i.Seq).ToList();
        var versions = await store.VersionsAsync(ct).ConfigureAwait(true);
        var batch = new List<(OutboxItem Item, JsonObject Mutation)>();
        var batchBytes = 0;
        foreach (var item in outbox)
        {
            var mutation = await MutationForAsync(item, versions.GetValueOrDefault((item.EntityType, item.EntityId)), ct).ConfigureAwait(true);
            if (mutation is null)
            {
                await store.CompleteAsync(item, null, ct).ConfigureAwait(true); // created and deleted before it ever synced
                continue;
            }

            if (item.EntityType == CloudKinds.Note)
                await UploadAssetsAsync(tenant, mutation, ct).ConfigureAwait(true);
            if (item.EntityType == CloudKinds.Task)
                await UploadTaskFilesAsync(tenant, mutation, ct).ConfigureAwait(true);
            var size = System.Text.Encoding.UTF8.GetByteCount(mutation.ToJsonString());
            if (batch.Count > 0 && (batch.Count >= 100 || batchBytes + size > MaxPushBytes))
            {
                await SendAsync(tenant, batch, ct).ConfigureAwait(true);
                batch.Clear();
                batchBytes = 0;
            }

            batch.Add((item, mutation));
            batchBytes += size;
        }

        if (batch.Count > 0)
            await SendAsync(tenant, batch, ct).ConfigureAwait(true);
    }

    private async Task SendAsync(string tenant, List<(OutboxItem Item, JsonObject Mutation)> mutations, CancellationToken ct)
    {
        await store.MarkAttemptedAsync(mutations.Select(m => m.Item.Seq).ToList(), ct).ConfigureAwait(true);
        var body = new JsonObject { ["mutations"] = new JsonArray(mutations.Select(m => (JsonNode)m.Mutation).ToArray()) };
        var response = await api.CallAsync(HttpMethod.Post, "/v1/sync/push", body, tenant, ct).ConfigureAwait(true);
        var results = (response["results"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        for (var i = 0; i < results.Count && i < mutations.Count; i++)
            await HandleResultAsync(mutations[i].Item, results[i], ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Pictures referenced by a note are uploaded before the note, so other devices never see a note whose pictures
    /// are not available yet. Content-addressed: a picture already in the cloud is not sent again.
    /// </summary>
    private async Task UploadAssetsAsync(string tenant, JsonObject mutation, CancellationToken ct)
    {
        if (assets is null)
            return;
        var note = (mutation["resource"] ?? mutation["changes"]) as JsonObject;
        foreach (var sha in (note?["assets"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
        {
            var stored = await assets.ReadAsync(sha, ct).ConfigureAwait(true);
            if (stored is null || stored.Value.Asset.Uploaded)
                continue;
            var data = stored.Value.Data;
            var mime = stored.Value.Asset.MimeType;
            using var response = await api.SendAuthorizedAsync(HttpMethod.Put, "/v1/note-assets/" + sha, () =>
            {
                var content = new ByteArrayContent(data);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
                return content;
            }, tenant, ct).ConfigureAwait(true);
            await CloudApi.EnsureSuccessAsync(response, ct).ConfigureAwait(true);
            await ((INoteStore)store).MarkAssetUploadedAsync(sha, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Files attached to a task go up before the task that lists them, so another device that sees the attachment can
    /// fetch it. Content-addressed (PUT /v1/task-files/{sha256}): a file already uploaded is not resent and an
    /// interrupted upload is sent again whole. A file the server refuses for good (too large, no room) is remembered
    /// as failed and the task still syncs. Bytes never go into task JSON.
    /// </summary>
    private async Task UploadTaskFilesAsync(string tenant, JsonObject mutation, CancellationToken ct)
    {
        if (taskFiles is null)
            return;
        var task = (mutation["resource"] ?? mutation["changes"]) as JsonObject;
        if (task?["attachments"] is not JsonArray list)
            return;
        var uploaded = await ShaSetAsync("task_files_uploaded", ct).ConfigureAwait(true);
        var failed = await FailedTaskFilesAsync(ct).ConfigureAwait(true);
        var since = await FilesSinceAsync(ct).ConfigureAwait(true);
        var choice = await LegacyTaskFilesChoiceAsync(ct).ConfigureAwait(true);
        var changed = false;
        foreach (var node in list.OfType<JsonObject>())
        {
            var attachment = node.Deserialize<TaskAttachment>(Json);
            if (attachment is null)
                continue;
            if (choice != true && IsLegacy(attachment, since, uploaded))
            {
                // Kept on this PC: the task syncs with the file's name and size only (relativePath is meaningless elsewhere).
                node.Remove("sha256");
                node.Remove("mediaType");
                if (choice is null && !_legacyRaised)
                {
                    _legacyRaised = true;
                    LegacyTaskFilesWaiting?.Invoke();
                }
                continue;
            }
            if (!TaskAttachment.IsHash(attachment.Sha256) || uploaded.Contains(attachment.Sha256!))
                continue;
            if (taskFiles.Resolve(attachment) is not { } path)
                continue;
            var sha = attachment.Sha256!;
            try
            {
                var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(true);
                using var response = await api.SendAuthorizedAsync(HttpMethod.Put, "/v1/task-files/" + sha, () =>
                {
                    var content = new ByteArrayContent(data);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    return content;
                }, tenant, ct).ConfigureAwait(true);
                await CloudApi.EnsureSuccessAsync(response, ct).ConfigureAwait(true);
                uploaded.Add(sha);
                failed.Remove(sha);
                changed = true;
            }
            // 408 UPLOAD_INCOMPLETE (the transfer broke off) is retried like 429/503, never recorded as a failure.
            catch (CloudApiException e) when (!e.IsNetwork && e.Status is not (401 or 403 or 408 or 429) && e.Status < 500)
            {
                failed[sha] = e.Message;
                changed = true;
            }
            catch (IOException)
            {
                // The file is in use or gone: try again next time.
            }
        }

        if (changed)
        {
            await store.SetMetaAsync("task_files_uploaded", new JsonArray(uploaded.Select(s => (JsonNode)s).ToArray()).ToJsonString(), ct).ConfigureAwait(true);
            await store.SetMetaAsync("task_files_failed", failed.Count == 0 ? null : JsonSerializer.Serialize(failed), ct).ConfigureAwait(true);
        }
    }

    private bool _legacyRaised;

    /// <summary>
    /// A file attached on this PC (its own copy, not one downloaded from the cloud) before this profile's Cloud Sync
    /// first handled attachments, and not already in cloud storage.
    /// </summary>
    private bool IsLegacy(TaskAttachment attachment, DateTimeOffset? since, HashSet<string> uploaded) =>
        since is { } start && attachment.AddedUtc < start && !string.IsNullOrEmpty(attachment.RelativePath)
        && !(TaskAttachment.IsHash(attachment.Sha256) && uploaded.Contains(attachment.Sha256!))
        && taskFiles?.Resolve(attachment) is not null;

    private async Task<DateTimeOffset?> FilesSinceAsync(CancellationToken ct)
    {
        if (await store.GetMetaAsync(FilesSinceKey, ct).ConfigureAwait(true) is { } text
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var since))
            return _sinceSeen = since;
        var now = clock.UtcNow;
        await store.SetMetaAsync(FilesSinceKey, now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(true);
        return _sinceSeen = now;
    }

    /// <summary>The user's decision about files attached before Cloud Sync handled attachments: true upload, false keep on this PC, null not asked yet (kept).</summary>
    public async Task<bool?> LegacyTaskFilesChoiceAsync(CancellationToken ct = default) =>
        _choiceSeen = await store.GetMetaAsync(LegacyChoiceKey, ct).ConfigureAwait(true) switch { "upload" => true, "keep" => false, _ => null };

    /// <summary>Records the decision. Choosing upload re-queues the tasks that list such files, so they go up on the next sync.</summary>
    public async Task SetLegacyTaskFilesChoiceAsync(bool upload, CancellationToken ct = default)
    {
        await store.SetMetaAsync(LegacyChoiceKey, upload ? "upload" : "keep", ct).ConfigureAwait(true);
        _choiceSeen = upload;
        _legacyRaised = false;
    }

    /// <summary>Tasks with files that the decision is about (for the prompt's wording and for re-queueing).</summary>
    public async Task<IReadOnlyList<TodoTask>> LegacyTaskFileTasksAsync(CancellationToken ct = default)
    {
        if (taskFiles is null)
            return [];
        var since = await FilesSinceAsync(ct).ConfigureAwait(true);
        var uploaded = await ShaSetAsync("task_files_uploaded", ct).ConfigureAwait(true);
        return board.Tasks.Where(t => t.Attachments.Any(a => IsLegacy(a, since, uploaded))).ToList();
    }

    private async Task<HashSet<string>> ShaSetAsync(string key, CancellationToken ct)
    {
        var set = await store.GetMetaAsync(key, ct).ConfigureAwait(true) is { } json && JsonNode.Parse(json) is JsonArray a
            ? a.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet()
            : [];
        if (key == "task_files_uploaded")
            _uploadedSeen = set;
        return set;
    }

    private async Task<Dictionary<string, string>> FailedTaskFilesAsync(CancellationToken ct)
    {
        var failed = await store.GetMetaAsync("task_files_failed", ct).ConfigureAwait(true) is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
            : [];
        _failedSeen = failed;
        return failed;
    }

    // Last known upload state, so the task details can show it without waiting (Dev-only spec U9).
    private HashSet<string>? _uploadedSeen;
    private Dictionary<string, string>? _failedSeen;
    private DateTimeOffset? _sinceSeen;
    private bool? _choiceSeen;

    /// <summary>Cloud Sync is on for this profile (as of the last enable, pause or sync).</summary>
    public bool FilesSyncOn { get; private set; }

    /// <summary>The transfer state of a file on this PC, from what this engine last read; null before the first read.</summary>
    public TaskFileTransfer? KnownTaskFileState(TaskAttachment attachment)
    {
        if (_uploadedSeen is null || !TaskAttachment.IsHash(attachment.Sha256))
            return null;
        var sha = attachment.Sha256!;
        if (_uploadedSeen.Contains(sha))
            return new TaskFileTransfer(TaskFileTransferState.Synced, null);
        if (_failedSeen?.GetValueOrDefault(sha) is { } error)
            return new TaskFileTransfer(TaskFileTransferState.Failed, error);
        if (_choiceSeen != true && _sinceSeen is { } since && attachment.AddedUtc < since && !string.IsNullOrEmpty(attachment.RelativePath))
            return new TaskFileTransfer(TaskFileTransferState.KeptOnThisPc, null);
        return new TaskFileTransfer(TaskFileTransferState.Queued, null);
    }

    /// <summary>Reads the upload state once, so <see cref="KnownTaskFileState"/> has something to show at start.</summary>
    public async Task WarmTaskFileStateAsync(CancellationToken ct = default)
    {
        FilesSyncOn = await EnabledAsync(ct).ConfigureAwait(true);
        await ShaSetAsync("task_files_uploaded", ct).ConfigureAwait(true);
        await FailedTaskFilesAsync(ct).ConfigureAwait(true);
        _choiceSeen = await LegacyTaskFilesChoiceAsync(ct).ConfigureAwait(true);
        if (await store.GetMetaAsync(FilesSinceKey, ct).ConfigureAwait(true) is { } text
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var since))
            _sinceSeen = since;
    }

    /// <summary>
    /// Retry for a file whose upload was refused (Dev-only spec U9): forget the error, queue the task that lists it again
    /// (its files go up before it is pushed) and sync now.
    /// </summary>
    public async Task<SyncStatus> RetryTaskFileAsync(string taskId, string sha256, CancellationToken ct = default)
    {
        var failed = await FailedTaskFilesAsync(ct).ConfigureAwait(true);
        if (failed.Remove(sha256))
            await store.SetMetaAsync("task_files_failed", failed.Count == 0 ? null : JsonSerializer.Serialize(failed), ct).ConfigureAwait(true);
        _failedSeen = failed;
        await board.UpdateTaskAsync(taskId, _ => { }, "Retry file", undoable: false).ConfigureAwait(true);
        return await SyncNowAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Whether the file is known to be in cloud storage, and why its upload failed (null when it did not).</summary>
    public async Task<(bool Uploaded, string? Error)> TaskFileStateAsync(string sha256, CancellationToken ct = default)
    {
        var uploaded = (await ShaSetAsync("task_files_uploaded", ct).ConfigureAwait(true)).Contains(sha256);
        return (uploaded, (await FailedTaskFilesAsync(ct).ConfigureAwait(true)).GetValueOrDefault(sha256));
    }

    private async Task RefreshTaskFileLimitsAsync(string tenant, CancellationToken ct)
    {
        if (taskFiles is null)
            return;
        try
        {
            var limits = await api.CallAsync(HttpMethod.Get, "/v1/task-files/limits", null, tenant, ct).ConfigureAwait(true);
            if (limits["max_file_bytes"]?.GetValue<long>() is > 0 and var bytes)
                MaxTaskFileBytes = bytes;
            if (limits["max_per_task"]?.GetValue<int>() is > 0 and var count)
                MaxTaskFilesPerTask = count;
        }
        catch (Exception e) when (e is CloudApiException or InvalidOperationException or FormatException)
        {
            // Keep the defaults.
        }
    }

    /// <summary>
    /// Downloads an attachment added on another device (when it is opened). True when its bytes are now on this PC;
    /// false when the cloud does not have them (not uploaded yet) or Cloud Sync is off.
    /// </summary>
    public async Task<bool> DownloadTaskFileAsync(string sha256, CancellationToken ct = default)
    {
        if (taskFiles is null || !TaskAttachment.IsHash(sha256) || await store.GetMetaAsync("tenant", ct).ConfigureAwait(true) is not { } tenant
            || !await EnabledAsync(ct).ConfigureAwait(true))
            return false;
        using var response = await api.SendAuthorizedAsync(HttpMethod.Get, "/v1/task-files/" + sha256, () => null, tenant, ct).ConfigureAwait(true);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        await CloudApi.EnsureSuccessAsync(response, ct).ConfigureAwait(true);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(true);
        return taskFiles.SaveDownloaded(sha256, data);
    }

    /// <summary>Downloads pictures of notes from other devices (a few per run; the rest next time).</summary>
    private async Task DownloadAssetsAsync(string tenant, CancellationToken ct)
    {
        if (assets is null)
            return;
        var pending = await PendingDownloadsAsync(ct).ConfigureAwait(true);
        if (pending.Count == 0)
            return;
        var left = new List<string>();
        var done = 0;
        foreach (var sha in pending)
        {
            if (assets.HasFile(sha))
                continue;
            if (done >= MaxDownloadsPerRun)
            {
                left.Add(sha);
                continue;
            }

            done++;
            if (!await DownloadAssetAsync(tenant, sha, ct).ConfigureAwait(true))
                left.Add(sha);
        }

        await store.SetMetaAsync("asset_downloads", left.Count == 0 ? null : new JsonArray(left.Select(s => (JsonNode)s).ToArray()).ToJsonString(), ct).ConfigureAwait(true);
    }

    /// <summary>One picture from the cloud (also used when a note is opened before its pictures arrived).</summary>
    public async Task<bool> DownloadAssetAsync(string tenant, string sha, CancellationToken ct = default)
    {
        if (assets is null)
            return false;
        try
        {
            using var response = await api.SendAuthorizedAsync(HttpMethod.Get, "/v1/note-assets/" + sha, () => null, tenant, ct).ConfigureAwait(true);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return true; // deleted in the cloud: nothing to fetch, do not retry
            await CloudApi.EnsureSuccessAsync(response, ct).ConfigureAwait(true);
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(true);
            return await assets.AddDownloadedAsync(sha, data, ct).ConfigureAwait(true);
        }
        catch (CloudApiException e) when (!e.IsNetwork)
        {
            return false;
        }
    }

    private async Task<List<string>> PendingDownloadsAsync(CancellationToken ct) =>
        await store.GetMetaAsync("asset_downloads", ct).ConfigureAwait(true) is { } json && JsonNode.Parse(json) is JsonArray a
            ? a.Select(n => n?.GetValue<string>()).OfType<string>().ToList()
            : [];

    private async Task QueueDownloadsAsync(IEnumerable<Note> incoming, CancellationToken ct)
    {
        if (assets is null)
            return;
        var missing = incoming.SelectMany(n => n.Assets).Where(sha => !assets.HasFile(sha)).ToList();
        if (missing.Count == 0)
            return;
        var pending = await PendingDownloadsAsync(ct).ConfigureAwait(true);
        pending.AddRange(missing.Where(m => !pending.Contains(m)));
        await store.SetMetaAsync("asset_downloads", new JsonArray(pending.Select(s => (JsonNode)s).ToArray()).ToJsonString(), ct).ConfigureAwait(true);
    }

    private async Task<JsonObject?> CurrentAsync(string type, string id, CancellationToken ct) => type switch
    {
        CloudKinds.Task => board.FindTask(id) is { } t ? (JsonObject)JsonSerializer.SerializeToNode(t, Json)! : null,
        CloudKinds.List => board.FindList(id) is { } l ? (JsonObject)JsonSerializer.SerializeToNode(l, Json)! : null,
        CloudKinds.Group => board.FindGroup(id) is { } g ? (JsonObject)JsonSerializer.SerializeToNode(g, Json)! : null,
        CloudKinds.NoteFolder => notes?.FindFolder(id) is { } f ? (JsonObject)JsonSerializer.SerializeToNode(f, Json)! : null,
        CloudKinds.Note => notes is not null && await notes.GetAsync(id, ct).ConfigureAwait(true) is { } n ? (JsonObject)JsonSerializer.SerializeToNode(n, Json)! : null,
        CloudKinds.Tag => tags?.Find(id) is { } tag ? (JsonObject)JsonSerializer.SerializeToNode(tag, Json)! : null,
        CloudKinds.TagLink => tags?.FindLink(id) is { } link ? (JsonObject)JsonSerializer.SerializeToNode(link, Json)! : null,
        _ => null,
    };

    private async Task<JsonObject?> MutationForAsync(OutboxItem item, SyncVersion? version, CancellationToken ct)
    {
        var current = item.Deleted ? null : await CurrentAsync(item.EntityType, item.EntityId, ct).ConfigureAwait(true);
        var m = new JsonObject { ["mutation_id"] = item.MutationId, ["resource_type"] = item.EntityType, ["resource_id"] = item.EntityId };
        if (current is null)
        {
            if (version is null)
                return null;
            m["action"] = "delete";
            m["base_version"] = version.Version;
        }
        else if (version is null)
        {
            m["action"] = "create";
            m["resource"] = current;
        }
        else
        {
            m["action"] = "patch";
            m["base_version"] = version.Version;
            m["changes"] = Diff(version.ServerJson, current);
        }

        return m;
    }

    /// <summary>Merge patch from the last server copy: removed fields become null.</summary>
    public static JsonObject Diff(string? serverJson, JsonObject current)
    {
        var patch = (JsonObject)current.DeepClone();
        if (serverJson is not null && JsonNode.Parse(serverJson) is JsonObject old)
        {
            foreach (var (key, _) in old)
            {
                if (!patch.ContainsKey(key))
                    patch[key] = null;
            }
        }

        return patch;
    }

    private async Task HandleResultAsync(OutboxItem item, JsonObject result, CancellationToken ct)
    {
        switch (result["status"]?.GetValue<string>())
        {
            case "applied":
                var record = result["record"] as JsonObject;
                await store.CompleteAsync(item, record is null ? null : VersionOf(item.EntityType, record), ct).ConfigureAwait(true);
                break;
            case "conflict":
                await KeepBothAsync(item, result["current"] as JsonObject, result["current_deleted"]?.GetValue<bool>() == true, ct).ConfigureAwait(true);
                await store.CompleteAsync(item, null, ct).ConfigureAwait(true);
                break;
            // Two devices made the same tag name offline: the cloud kept the first, and this PC's tag is merged into it
            // after the pull (MergeDuplicateTagsAsync), together with the links that named it. Nothing to report.
            case "rejected" when CloudKinds.IsTagKind(item.EntityType) && result["code"]?.GetValue<string>() is "TAG_NAME_TAKEN" or "INVALID_REFERENCE":
                await store.CompleteAsync(item, null, ct).ConfigureAwait(true);
                break;
            default:
                await store.AddIssueAsync(item.EntityType, item.EntityId, TitleOf(item.EntityType, item.EntityId),
                    "Not synced: " + (result["message"]?.GetValue<string>() ?? "the cloud did not accept this change.") + " It is still on this PC.", clock.UtcNow, ct).ConfigureAwait(true);
                await store.CompleteAsync(item, null, ct).ConfigureAwait(true);
                break;
        }
    }

    private static SyncVersion? VersionOf(string type, JsonObject record) =>
        record["version"]?.GetValue<long>() is { } v ? new SyncVersion(type, record["id"]!.GetValue<string>(), v, record[type]?.ToJsonString()) : null;

    private string TitleOf(string type, string id) => type switch
    {
        CloudKinds.Task => board.FindTask(id)?.Title,
        CloudKinds.List => board.FindList(id)?.Name,
        CloudKinds.Group => board.FindGroup(id)?.Name,
        CloudKinds.NoteFolder => notes?.FindFolder(id)?.Name,
        CloudKinds.Note => notes?.Find(id)?.DisplayTitle,
        CloudKinds.Tag => tags?.Find(id)?.Name is { } name ? "Tag “" + name + "”" : null,
        CloudKinds.TagLink => "Tag on an item",
        _ => null,
    } ?? "Item";

    /// <summary>
    /// Both sides changed. The cloud version takes the original id (so all devices converge); the local version is kept
    /// as a new task marked "(conflict copy)" and queued. The user sees it under Sync issues. Nothing is lost.
    /// Attachments are merged three-way on the original task instead (Dev-only spec BE-03): a file removed here is not
    /// brought back, files added on either side are kept, and the copy carries no attachments, so files are never
    /// duplicated. When only attachments differ, the merge is all there is: no copy and no sync issue.
    /// </summary>
    private async Task KeepBothAsync(OutboxItem item, JsonObject? current, bool currentDeleted, CancellationToken ct)
    {
        var title = TitleOf(item.EntityType, item.EntityId);
        // Tags: the cloud's state is adopted without a copy (a tag added on one device and removed on another resolve in
        // server order; the item itself is never changed by a tag conflict).
        var report = !CloudKinds.IsTagKind(item.EntityType);
        JsonArray? mergedFiles = null;
        if (item.EntityType == CloudKinds.Task && board.FindTask(item.EntityId) is { } mine)
        {
            var serverJson = (await store.VersionsAsync(ct).ConfigureAwait(true)).GetValueOrDefault((CloudKinds.Task, item.EntityId))?.ServerJson;
            var localJson = await CurrentAsync(CloudKinds.Task, item.EntityId, ct).ConfigureAwait(true);
            // Compared through the model, so formatting differences (Z vs +00:00, absent vs empty) are not edits.
            var baseTask = serverJson is null ? null : JsonSerializer.Deserialize<TodoTask>(serverJson, Json);
            var otherChanges = baseTask is null || OtherThanAttachments((JsonObject)JsonSerializer.SerializeToNode(baseTask, Json)!, localJson!);
            var remoteFiles = current?[item.EntityType]?["attachments"] as JsonArray;
            var remoteExists = current is not null && !currentDeleted;
            if (remoteExists)
            {
                var baseFiles = serverJson is null ? null : JsonNode.Parse(serverJson)?["attachments"] as JsonArray;
                mergedFiles = AttachmentMerge.Merge(baseFiles, localJson!["attachments"] as JsonArray, remoteFiles);
                if (AttachmentMerge.SameIds(mergedFiles, remoteFiles))
                    mergedFiles = null;
            }

            report = !remoteExists || otherChanges;
            if (report)
            {
                await board.AddTaskAsync(mine.ListId, mine.Title + " (conflict copy)", t =>
                {
                    t.Notes = mine.Notes; t.Important = mine.Important; t.Completed = mine.Completed; t.DueDate = mine.DueDate; t.DueTime = mine.DueTime;
                    t.MyDayDate = mine.MyDayDate; t.StartDate = mine.StartDate; t.Recurrence = mine.Recurrence;
                    t.Subtasks = mine.Subtasks.Select(s => s.Clone()).ToList();
                    t.Reminders = mine.Reminders.Select(r => new TaskReminder { At = r.At }).ToList();
                    // The original is gone on the server: the copy is where this PC's files stay listed.
                    if (!remoteExists)
                        t.Attachments = mine.Attachments.Select(a => a.Clone()).ToList();
                }).ConfigureAwait(true);
            }
        }

        string? copyId = null;
        if (item.EntityType == CloudKinds.Note && notes is not null && await notes.GetAsync(item.EntityId, ct).ConfigureAwait(true) is { } myNote)
            copyId = (await notes.ConflictCopyAsync(myNote).ConfigureAwait(true)).Id;

        if (current is not null && !currentDeleted)
            await ApplyAsync([new JsonObject { ["resource_type"] = item.EntityType, ["action"] = "upsert", ["resource_id"] = item.EntityId, ["record"] = current.DeepClone() }], null, ct, force: true).ConfigureAwait(true);
        if (currentDeleted)
            await ApplyAsync([new JsonObject { ["resource_type"] = item.EntityType, ["action"] = "delete", ["resource_id"] = item.EntityId }], null, ct, force: true).ConfigureAwait(true);
        if (mergedFiles is not null)
        {
            var files = mergedFiles.Deserialize<List<TaskAttachment>>(Json) ?? [];
            await board.UpdateTaskAsync(item.EntityId, t => t.Attachments = files, "Merge files", undoable: false).ConfigureAwait(true);
        }
        if (report)
            await store.AddIssueAsync(item.EntityType, item.EntityId, title,
                currentDeleted ? "It was deleted on another device while you edited it here. Your version was kept as a conflict copy."
                    : "It was changed on another device too. The other version is shown; yours was kept as a conflict copy.",
                clock.UtcNow, ct, copyId).ConfigureAwait(true);
    }

    /// <summary>True when the two task objects differ in more than attachments (and the modified time, which every edit changes).</summary>
    private static bool OtherThanAttachments(JsonObject a, JsonObject b) =>
        a.Select(p => p.Key).Union(b.Select(p => p.Key)).Where(k => k is not ("attachments" or "modifiedUtc"))
            .Any(k => !JsonNode.DeepEquals(a[k], b[k]));

    // ------------------------------------------------------------------ pull

    private async Task PullAsync(string tenant, CancellationToken ct)
    {
        var cursor = await store.GetMetaAsync("cursor", ct).ConfigureAwait(true);
        if (cursor is null)
        {
            await BootstrapAsync(tenant, ct).ConfigureAwait(true);
            return;
        }

        while (true)
        {
            JsonObject page;
            try
            {
                page = await api.CallAsync(HttpMethod.Get, "/v1/sync/changes?limit=500&cursor=" + Uri.EscapeDataString(cursor), tenant: tenant, cancellationToken: ct).ConfigureAwait(true);
            }
            catch (CloudApiException e) when (e.Code is "CURSOR_EXPIRED" or "INVALID_CURSOR")
            {
                // Too old: download everything again and merge; unsent local changes stay queued.
                await BootstrapAsync(tenant, ct).ConfigureAwait(true);
                return;
            }

            var changes = (page["changes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            cursor = page["cursor"]!.GetValue<string>();
            await ApplyAsync(changes, cursor, ct).ConfigureAwait(true);
            if (page["has_more"]?.GetValue<bool>() != true)
                break;
        }
    }

    /// <summary>
    /// Applies feed items in one transaction together with the cursor. Items with unsent local edits are skipped:
    /// the local edit is pushed next and the server reports a conflict if both changed.
    /// </summary>
    private async Task ApplyAsync(IReadOnlyList<JsonObject> items, string? cursor, CancellationToken ct, bool force = false)
    {
        var pending = (await store.OutboxAsync(ct).ConfigureAwait(true)).Select(i => (i.EntityType, i.EntityId)).ToHashSet();
        var changes = new TodoChangeSet();
        var versions = new List<SyncVersion>();
        var noteChanges = new NoteChangeSet();
        var noteVersions = new List<SyncVersion>();
        var tagChanges = new Alondra1.Core.Tags.TagChangeSet();
        var tagVersions = new List<SyncVersion>();
        foreach (var item in items)
        {
            var type = item["resource_type"]?.GetValue<string>();
            var id = item["resource_id"]?.GetValue<string>();
            if (type is null || id is null || !CloudKinds.Order.Contains(type) || (notes is null && CloudKinds.IsNoteKind(type))
                || (tags is null && CloudKinds.IsTagKind(type)))
                continue; // completions, settings and calendar events are not synced by this client
            if (!force && pending.Contains((type, id)))
                continue;
            if (CloudKinds.IsNoteKind(type))
            {
                CollectNote(item, type, id, noteChanges, noteVersions);
                continue;
            }

            if (CloudKinds.IsTagKind(type))
            {
                CollectTag(item, type, id, tagChanges, tagVersions);
                continue;
            }

            if (item["action"]?.GetValue<string>() == "delete")
            {
                switch (type)
                {
                    case CloudKinds.Task: if (board.FindTask(id) is not null) changes.DeleteTasks.Add(id); break;
                    case CloudKinds.List: if (board.FindList(id) is { IsDefault: false }) { changes.DeleteLists.Add(id); changes.DeleteTasks.AddRange(board.Tasks.Where(t => t.ListId == id).Select(t => t.Id)); } break;
                    case CloudKinds.Group: if (board.FindGroup(id) is not null) { changes.DeleteGroups.Add(id); foreach (var l in board.Lists.Where(l => l.GroupId == id)) { var c = l.Clone(); c.GroupId = null; changes.UpsertLists.Add(c); } } break;
                }

                continue;
            }

            if (item["record"] is not JsonObject record || record[type] is not JsonObject obj)
                continue;
            versions.Add(new SyncVersion(type, id, record["version"]!.GetValue<long>(), obj.ToJsonString()));
            switch (type)
            {
                case CloudKinds.Task:
                    var task = obj.Deserialize<TodoTask>(Json)!;
                    KeepDeviceReminderState(task, board.FindTask(id));
                    changes.UpsertTasks.Add(task);
                    break;
                case CloudKinds.List:
                    changes.UpsertLists.Add(obj.Deserialize<TaskList>(Json)!);
                    break;
                case CloudKinds.Group:
                    var group = obj.Deserialize<ListGroup>(Json)!;
                    group.Collapsed = board.FindGroup(id)?.Collapsed ?? false; // presentation is per device
                    changes.UpsertGroups.Add(group);
                    break;
            }
        }

        // Notes first, then tasks with the cursor: if the app stops in between, the page is applied again (idempotent).
        if (notes is not null && !noteChanges.IsEmpty)
        {
            await notes.ApplyRemoteAsync(noteChanges, noteVersions, null).ConfigureAwait(true);
            await QueueDownloadsAsync(noteChanges.UpsertNotes, ct).ConfigureAwait(true);
        }

        if (tags is not null && (!tagChanges.IsEmpty || tagVersions.Count > 0))
            await tags.ApplyRemoteAsync(tagChanges, tagVersions).ConfigureAwait(true);

        await board.ApplyRemoteAsync(changes, versions, cursor).ConfigureAwait(true);
        if (!changes.IsEmpty && changes.UpsertLists.Any(l => l.IsDefault))
            await AdoptCloudDefaultListAsync(ct).ConfigureAwait(true);
    }

    private void CollectNote(JsonObject item, string type, string id, NoteChangeSet changes, List<SyncVersion> versions)
    {
        if (item["action"]?.GetValue<string>() == "delete")
        {
            if (type == CloudKinds.Note && notes!.Find(id) is not null)
                changes.DeleteNotes.Add(id);
            else if (type == CloudKinds.NoteFolder && notes!.FindFolder(id) is not null)
                changes.DeleteFolders.Add(id); // its notes stay; they show in All Notes until moved
            return;
        }

        if (item["record"] is not JsonObject record || record[type] is not JsonObject obj)
            return;
        versions.Add(new SyncVersion(type, id, record["version"]!.GetValue<long>(), obj.ToJsonString()));
        if (type == CloudKinds.Note)
            changes.UpsertNotes.Add(obj.Deserialize<Note>(Json)!);
        else
            changes.UpsertFolders.Add(obj.Deserialize<NoteFolder>(Json)!);
    }

    private void CollectTag(JsonObject item, string type, string id, Alondra1.Core.Tags.TagChangeSet changes, List<SyncVersion> versions)
    {
        if (item["action"]?.GetValue<string>() == "delete")
        {
            if (type == CloudKinds.Tag && tags!.Find(id) is not null)
                changes.DeleteTags.Add(id);
            else if (type == CloudKinds.TagLink && tags!.FindLink(id) is not null)
                changes.DropLinks.Add(id);
            return;
        }

        if (item["record"] is not JsonObject record || record[type] is not JsonObject obj)
            return;
        versions.Add(new SyncVersion(type, id, record["version"]!.GetValue<long>(), obj.ToJsonString()));
        if (type == CloudKinds.Tag)
            changes.UpsertTags.Add(obj.Deserialize<Alondra1.Core.Tags.Tag>(Json)!);
        else
            changes.UpsertLinks.Add(obj.Deserialize<Alondra1.Core.Tags.TagLink>(Json)!);
    }

    /// <summary>A tag this PC created offline with a name the cloud already had is merged into the cloud's tag.</summary>
    private async Task MergeDuplicateTagsAsync(CancellationToken ct)
    {
        if (tags is null)
            return;
        var synced = (await store.VersionsAsync(ct).ConfigureAwait(true)).Keys.Where(k => k.Type == CloudKinds.Tag).Select(k => k.Id).ToHashSet();
        await tags.MergeUnsyncedDuplicatesAsync(synced).ConfigureAwait(true);
    }

    /// <summary>Reminder delivery state (delivered, dismissed, snoozed) belongs to this PC and is never taken from the cloud.</summary>
    private static void KeepDeviceReminderState(TodoTask incoming, TodoTask? local)
    {
        if (local is null)
            return;
        foreach (var reminder in incoming.Reminders)
        {
            if (local.Reminders.FirstOrDefault(r => r.Id == reminder.Id && r.At == reminder.At) is { } mine)
            {
                reminder.DeliveredFor = mine.DeliveredFor;
                reminder.Dismissed = mine.Dismissed;
                reminder.SnoozedUntilUtc = mine.SnoozedUntilUtc;
            }
        }
    }
}
