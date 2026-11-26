using System.Text.Json.Nodes;

namespace Alondra1.Core.Cloud;

/// <summary>
/// Three-way merge of a task's attachments after a sync conflict (Dev-only spec BE-03, review U4; the shared rule is
/// contract/fixtures/todo-v1/merge-attachments.json). Start from the server's current list, drop what this device
/// removed since the last server copy it saw, add what this device added since then unless the server already lists
/// it (same id or same SHA-256). Removals on either side are never reversed. Unknown fields travel unchanged.
/// </summary>
public static class AttachmentMerge
{
    public static JsonArray Merge(JsonArray? @base, JsonArray? local, JsonArray? remote)
    {
        var baseIds = Ids(@base);
        var localItems = Items(local);
        var localIds = localItems.Select(Id).OfType<string>().ToHashSet();
        var removedHere = baseIds.Where(id => !localIds.Contains(id)).ToHashSet();
        var result = Items(remote).Where(r => Id(r) is not { } id || !removedHere.Contains(id)).ToList();
        foreach (var added in localItems.Where(l => Id(l) is { } id && !baseIds.Contains(id)))
        {
            if (result.Any(r => Id(r) == Id(added) || (Sha(r) is { } sha && sha == Sha(added))))
                continue;
            result.Add(added);
        }
        return new JsonArray(result.Select(n => n.DeepClone()).ToArray());
    }

    /// <summary>True when the two lists hold the same attachments in the same order (ids only).</summary>
    public static bool SameIds(JsonArray? a, JsonArray? b) => Items(a).Select(Id).SequenceEqual(Items(b).Select(Id));

    private static List<JsonObject> Items(JsonArray? list) => list?.OfType<JsonObject>().ToList() ?? [];
    private static HashSet<string> Ids(JsonArray? list) => Items(list).Select(Id).OfType<string>().ToHashSet();
    private static string? Id(JsonObject o) => (o["id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    private static string? Sha(JsonObject o) => (o["sha256"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
