using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Alondra1.Core.Cloud;

public sealed record ArchivedAttachment(int Index, string FileName, string ContentType, long Bytes);

public sealed record ArchivedMessage(
    string Id, string Subject, string From, IReadOnlyList<string> To, DateTimeOffset? SentAt, DateTimeOffset ArchivedAt, long Bytes,
    string SourceAccount, string Status, string Snippet, string Text, IReadOnlyList<ArchivedAttachment> Attachments)
{
    public static ArchivedMessage Parse(JsonObject o)
    {
        static DateTimeOffset? Time(JsonNode? n) => n?.GetValue<string>() is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;
        return new ArchivedMessage(
            o["id"]!.GetValue<string>(), o["subject"]?.GetValue<string>() ?? "", o["from"]?.GetValue<string>() ?? "",
            (o["to"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList(), Time(o["sent_at"]), Time(o["archived_at"]) ?? DateTimeOffset.MinValue,
            o["bytes"]?.GetValue<long>() ?? 0, o["source_account"]?.GetValue<string>() ?? "", o["status"]?.GetValue<string>() ?? "ready",
            o["snippet"]?.GetValue<string>() ?? "", o["text"]?.GetValue<string>() ?? "",
            (o["attachments"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new ArchivedAttachment(a["index"]!.GetValue<int>(),
                a["filename"]?.GetValue<string>() ?? "attachment", a["content_type"]?.GetValue<string>() ?? "", a["bytes"]?.GetValue<long>() ?? 0)).ToList());
    }
}

public sealed record ArchiveQuery(string? Text = null, string? From = null, string? To = null, DateOnly? After = null, DateOnly? Before = null,
    bool? HasAttachment = null, string? Cursor = null, int Limit = 25);

/// <summary>
/// Paid Mail Archive (spec §7): cloud copies of messages the user selected. The source mailbox is never changed; no
/// mail credentials or provider tokens are ever sent. Search covers the cloud archive only (local search stays free).
/// </summary>
public sealed class ArchiveClient(CloudApi api)
{
    public const long MaxMessageBytes = 25L << 20;

    /// <summary>Uploads one original message. Returns the stored copy and whether it was already archived.</summary>
    public async Task<(ArchivedMessage Message, bool Duplicate)> ArchiveAsync(string tenantId, byte[] rawMime, string sourceAccount, CancellationToken ct)
    {
        if (rawMime.Length == 0 || rawMime.Length > MaxMessageBytes)
            throw new CloudApiException(413, "MESSAGE_TOO_LARGE", "A message can be at most 25 MB, attachments included.");
        using var response = await api.SendAuthorizedAsync(HttpMethod.Post, "/v1/archive/ingest", () =>
        {
            var content = new ByteArrayContent(rawMime);
            content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
            content.Headers.TryAddWithoutValidation("X-Source-Account", sourceAccount);
            return content;
        }, tenantId, ct).ConfigureAwait(false);
        await CloudApi.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? [];
        return (ArchivedMessage.Parse((JsonObject)json["message"]!), json["duplicate"]?.GetValue<bool>() == true);
    }

    public async Task<(IReadOnlyList<ArchivedMessage> Messages, string? Next)> SearchAsync(string tenantId, ArchiveQuery q, CancellationToken ct)
    {
        var parts = new List<string> { "limit=" + q.Limit };
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add(key + "=" + Uri.EscapeDataString(value.Trim()));
        }

        Add("q", q.Text); Add("from", q.From); Add("to", q.To); Add("cursor", q.Cursor);
        Add("after", q.After?.ToString("yyyy-MM-dd")); Add("before", q.Before?.ToString("yyyy-MM-dd"));
        if (q.HasAttachment is { } has)
            parts.Add("has_attachment=" + (has ? "true" : "false"));
        var page = await api.CallAsync(HttpMethod.Get, "/v1/archive/messages?" + string.Join("&", parts), tenant: tenantId, cancellationToken: ct).ConfigureAwait(false);
        return ((page["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(ArchivedMessage.Parse).ToList(), page["next_cursor"]?.GetValue<string>());
    }

    public async Task<ArchivedMessage> GetAsync(string tenantId, string id, CancellationToken ct) =>
        ArchivedMessage.Parse((JsonObject)(await api.CallAsync(HttpMethod.Get, "/v1/archive/messages/" + Uri.EscapeDataString(id), tenant: tenantId, cancellationToken: ct)
            .ConfigureAwait(false))["message"]!);

    /// <summary>Downloads the original .eml or one attachment into <paramref name="destination"/>.</summary>
    public async Task DownloadAsync(string tenantId, string id, int? attachmentIndex, Stream destination, CancellationToken ct)
    {
        var path = "/v1/archive/messages/" + Uri.EscapeDataString(id) + (attachmentIndex is { } i ? "/attachments/" + i : "/raw");
        using var response = await api.SendAuthorizedAsync(HttpMethod.Get, path, () => null, tenantId, ct).ConfigureAwait(false);
        await CloudApi.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct) =>
        api.CallAsync(HttpMethod.Delete, "/v1/archive/messages/" + Uri.EscapeDataString(id), tenant: tenantId, cancellationToken: ct);
}
