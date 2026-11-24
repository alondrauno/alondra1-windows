using System.Text.Json.Nodes;

namespace Alondra1.Core.Cloud;

public sealed record CloudSpace(string Id, string Type, string Name, string Role);

public sealed record CloudPlan(string Code, string Name, long PriceCents, string Currency, string Interval, bool StartingAt, string PriceStatus, IReadOnlyList<string> Features);

/// <summary>Effective rights in a space, exactly as the server reports them (the server is authoritative).</summary>
public sealed record CloudEntitlements(
    string Plan, string PlanName, string State, bool Sync, bool SyncWrite, bool Archive, bool ArchiveIngest, bool ArchiveSearch,
    DateTimeOffset? EffectiveUntil, bool ManageBilling, long ArchiveMessages, long ArchiveBytes)
{
    public static CloudEntitlements From(JsonObject o)
    {
        bool F(string key) => o["features"]?[key]?.GetValue<bool>() == true;
        long U(string key) => o["usage"]?[key]?.GetValue<long>() ?? 0;
        var until = o["effective_until"]?.GetValue<string>();
        return new CloudEntitlements(
            o["plan"]?.GetValue<string>() ?? "free", o["plan_name"]?.GetValue<string>() ?? "Free", o["entitlement_state"]?.GetValue<string>() ?? "inactive",
            F("sync"), F("sync_write"), F("mail_archive"), F("archive_ingest"), F("archive_search"),
            until is null ? null : DateTimeOffset.Parse(until, System.Globalization.CultureInfo.InvariantCulture),
            o["entitlements"] is JsonArray list && list.Any(item => item?.GetValue<string>() == "billing.manage"),
            U("archive_messages"), U("archive_bytes"));
    }

    public string Describe() => State switch
    {
        "active" => "Cloud Sync and Mail Archive are included.",
        "grace" => $"The plan has ended; everything keeps working until {EffectiveUntil?.ToLocalTime():d}.",
        "read_only" => "The plan has ended: cloud data and the archive are read-only. New changes stay on this PC.",
        _ => "Free. Cloud Sync and Mail Archive are paid features.",
    };
}

public sealed record CloudDevice(string Id, string Name, string Platform, DateTimeOffset LastSeen, bool Current);

/// <summary>Optional Alondra1 account: email one-time-code sign-in. The local apps never need it.</summary>
public sealed class CloudAccount(CloudApi api, ICloudSyncStore store)
{
    public CloudApi Api => api;

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken) =>
        await api.Tokens.LoadRefreshAsync(cancellationToken).ConfigureAwait(false) is not null;

    public Task<string?> EmailAsync(CancellationToken cancellationToken) => store.GetMetaAsync("email", cancellationToken);

    /// <summary>Returns the challenge id. The answer is the same whether or not the address has an account.</summary>
    public async Task<string> StartAsync(string email, CancellationToken cancellationToken) =>
        (await api.AnonymousAsync(HttpMethod.Post, "/v1/auth/email/start", new JsonObject { ["email"] = email.Trim() }, cancellationToken).ConfigureAwait(false))
        ["challenge_id"]!.GetValue<string>();

    public async Task VerifyAsync(string email, string challengeId, string code, CancellationToken cancellationToken)
    {
        var result = await api.AnonymousAsync(HttpMethod.Post, "/v1/auth/email/verify", new JsonObject
        {
            ["email"] = email.Trim(), ["code"] = code.Trim(), ["challenge_id"] = challengeId,
            ["device"] = new JsonObject { ["name"] = Environment.MachineName, ["platform"] = "windows" },
        }, cancellationToken).ConfigureAwait(false);
        await api.SaveSessionAsync(result, cancellationToken).ConfigureAwait(false);
        await store.SetMetaAsync("email", result["user"]?["email"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
        await store.SetMetaAsync("user_id", result["user"]?["id"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudSpace>> SpacesAsync(CancellationToken cancellationToken) =>
        ((await api.CallAsync(HttpMethod.Get, "/v1/tenants", cancellationToken: cancellationToken).ConfigureAwait(false))["tenants"] as JsonArray ?? [])
        .OfType<JsonObject>()
        .Select(o => new CloudSpace(o["id"]!.GetValue<string>(), o["type"]!.GetValue<string>(), o["name"]?.GetValue<string>() ?? "", o["role"]!.GetValue<string>()))
        .ToList();

    /// <summary>The caller's personal space, created on first use.</summary>
    public async Task<CloudSpace> PersonalSpaceAsync(CancellationToken cancellationToken)
    {
        var existing = (await SpacesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Type == "personal" && s.Role == "owner");
        if (existing is not null)
            return existing;
        var created = await api.CallAsync(HttpMethod.Post, "/v1/tenants", new JsonObject { ["type"] = "personal" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new CloudSpace(created["id"]!.GetValue<string>(), "personal", created["name"]?.GetValue<string>() ?? "Personal", "owner");
    }

    public async Task<CloudEntitlements> EntitlementsAsync(string tenantId, CancellationToken cancellationToken) =>
        CloudEntitlements.From(await api.CallAsync(HttpMethod.Get, "/v1/me/entitlements", tenant: tenantId, cancellationToken: cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<CloudPlan>> PlansAsync(CancellationToken cancellationToken) =>
        ((await api.AnonymousAsync(HttpMethod.Get, "/v1/billing/plans", null, cancellationToken).ConfigureAwait(false))["plans"] as JsonArray ?? [])
        .OfType<JsonObject>()
        .Select(o => new CloudPlan(o["code"]!.GetValue<string>(), o["name"]!.GetValue<string>(), o["price_cents"]!.GetValue<long>(), o["currency"]!.GetValue<string>(),
            o["interval"]?.GetValue<string>() ?? "", o["starting_at"]?.GetValue<bool>() == true, o["price_status"]?.GetValue<string>() ?? "",
            (o["features"] as JsonArray ?? []).Select(f => f!.GetValue<string>()).ToList()))
        .ToList();

    public async Task<IReadOnlyList<CloudDevice>> DevicesAsync(CancellationToken cancellationToken) =>
        ((await api.CallAsync(HttpMethod.Get, "/v1/devices", cancellationToken: cancellationToken).ConfigureAwait(false))["devices"] as JsonArray ?? [])
        .OfType<JsonObject>()
        .Select(o => new CloudDevice(o["id"]!.GetValue<string>(), o["name"]!.GetValue<string>(), o["platform"]!.GetValue<string>(),
            DateTimeOffset.Parse(o["last_seen_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), o["current"]?.GetValue<bool>() == true))
        .ToList();

    public Task RemoveDeviceAsync(string id, CancellationToken cancellationToken) =>
        api.CallAsync(HttpMethod.Delete, "/v1/devices/" + Uri.EscapeDataString(id), cancellationToken: cancellationToken);

    /// <summary>Ends this PC's session. Local tasks, mail and settings stay; Cloud Sync pauses until the next sign-in.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await api.CallAsync(HttpMethod.Post, "/v1/auth/logout", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CloudApiException)
        {
            // Offline or already signed out: forget the session locally anyway.
        }

        api.Tokens.Access = null;
        await api.Tokens.SaveRefreshAsync(null, cancellationToken).ConfigureAwait(false);
        await store.SetMetaAsync("sync_enabled", null, cancellationToken).ConfigureAwait(false);
    }
}
