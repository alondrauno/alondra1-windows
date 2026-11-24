using System.Text.Json.Nodes;

namespace Alondra1.Core.Notes;

/// <summary>How the user resolves a note conflict (Notes spec §10.2: user selection or manual merge).</summary>
public enum NoteConflictChoice
{
    /// <summary>Keep the version from the other device (the note); the conflict copy goes to Trash.</summary>
    KeepOther,
    /// <summary>Keep this device's version: it replaces the note's content; the conflict copy goes to Trash.</summary>
    KeepMine,
    /// <summary>Put both versions into the note (shared parts once), to finish by hand; the conflict copy goes to Trash.</summary>
    Combine,
    /// <summary>Keep both as separate notes.</summary>
    KeepBoth,
}

public enum DiffSide { Both, OnlyOther, OnlyMine }

public sealed record DiffLine(string Text, DiffSide Side);

/// <summary>
/// Comparing and combining two versions of a note. Blocks (paragraphs, lists, tables, pictures…) and text lines are
/// aligned by their longest common sequence: what both versions share appears once, and where they differ the other
/// device's part comes first, then this device's. Nothing from either version is dropped. Same rules as the Android app.
/// </summary>
public static class NoteMerge
{
    public const string ConflictSuffix = " (conflict copy)";

    /// <summary>Above this many compared pairs, differing middles are listed one after the other without alignment.</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>The title a conflict copy had before " (conflict copy)" was added.</summary>
    public static string OriginalTitle(string copyTitle) =>
        copyTitle.EndsWith(ConflictSuffix, StringComparison.Ordinal) ? copyTitle[..^ConflictSuffix.Length] : copyTitle;

    /// <summary>Lines of both texts, marked as shared or as only in one version.</summary>
    public static IReadOnlyList<DiffLine> DiffLines(string other, string mine) =>
        Align(Lines(other), Lines(mine), (p, q) => p == q).Select(r => new DiffLine(r.Item, r.Side)).ToList();

    public static string CombineText(string other, string mine) => string.Join("\n", DiffLines(other, mine).Select(l => l.Text));

    /// <summary>A document with every block of both versions; plain-text notes (no document) count as paragraphs.</summary>
    public static JsonObject CombineDocuments(JsonObject? other, string otherText, JsonObject? mine, string mineText)
    {
        var merged = Align(Blocks(other, otherText), Blocks(mine, mineText), (a, b) => JsonNode.DeepEquals(a, b))
            .Select(r => r.Item.DeepClone()).ToArray();
        var result = (other ?? mine)?.DeepClone() as JsonObject ?? new JsonObject();
        result["schemaVersion"] = Math.Max(VersionOf(other), VersionOf(mine));
        result["type"] = "doc";
        result["content"] = new JsonArray(merged);
        return result;
    }

    /// <summary>A document of plain paragraphs (one per line).</summary>
    public static JsonObject ParagraphDocument(string text) =>
        new() { ["schemaVersion"] = 1, ["type"] = "doc", ["content"] = new JsonArray([.. Paragraphs(text)]) };

    public static JsonObject Paragraph(string text, bool italic = false)
    {
        var p = new JsonObject { ["type"] = "paragraph" };
        if (text.Length > 0)
        {
            var node = new JsonObject { ["type"] = "text", ["text"] = text };
            if (italic)
                node["marks"] = new JsonArray(new JsonObject { ["type"] = "italic" });
            p["content"] = new JsonArray(node);
        }

        return p;
    }

    private static int VersionOf(JsonObject? doc) => doc?["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 1;

    private static List<string> Lines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    private static List<JsonNode> Paragraphs(string text) => [.. Lines(text).Select(line => (JsonNode)Paragraph(line))];

    private static List<JsonNode> Blocks(JsonObject? doc, string text) =>
        doc?["content"] is JsonArray content ? content.OfType<JsonNode>().ToList() : text.Length == 0 ? [] : Paragraphs(text);

    /// <summary>Longest-common-subsequence alignment of two sequences (common prefix and suffix are matched first).</summary>
    public static List<(T Item, DiffSide Side)> Align<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> same)
    {
        var start = 0;
        while (start < a.Count && start < b.Count && same(a[start], b[start]))
            start++;
        var endA = a.Count;
        var endB = b.Count;
        while (endA > start && endB > start && same(a[endA - 1], b[endB - 1]))
        {
            endA--;
            endB--;
        }

        var result = new List<(T, DiffSide)>();
        for (var i = 0; i < start; i++)
            result.Add((a[i], DiffSide.Both));
        int n = endA - start, m = endB - start;
        if ((long)n * m > MaxCells)
        {
            for (var i = start; i < endA; i++)
                result.Add((a[i], DiffSide.OnlyOther));
            for (var j = start; j < endB; j++)
                result.Add((b[j], DiffSide.OnlyMine));
        }
        else
        {
            // lcs[i, j] = length of the common subsequence of a[start+i..endA) and b[start+j..endB).
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = same(a[start + i], b[start + j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            var pendingA = new List<T>();
            var pendingB = new List<T>();
            void Flush()
            {
                result.AddRange(pendingA.Select(item => (item, DiffSide.OnlyOther)));
                result.AddRange(pendingB.Select(item => (item, DiffSide.OnlyMine)));
                pendingA.Clear();
                pendingB.Clear();
            }

            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (same(a[start + x], b[start + y]))
                {
                    Flush();
                    result.Add((a[start + x], DiffSide.Both));
                    x++;
                    y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                    pendingA.Add(a[start + x++]);
                else
                    pendingB.Add(b[start + y++]);
            }

            while (x < n)
                pendingA.Add(a[start + x++]);
            while (y < m)
                pendingB.Add(b[start + y++]);
            Flush();
        }

        for (var i = endA; i < a.Count; i++)
            result.Add((a[i], DiffSide.Both));
        return result;
    }
}

/// <summary>The note "Meeting notes" creates for a calendar event (Notes spec §11). Same template on Android.</summary>
public static class MeetingNotes
{
    public static string Title(string subject) => (string.IsNullOrWhiteSpace(subject) ? "Meeting" : subject.Trim()) + " – meeting notes";

    /// <param name="when">The event's date and time, already formatted for the user.</param>
    /// <param name="description">The event's description, if any.</param>
    public static (JsonObject Doc, string Text) Document(string when, string? description = null)
    {
        var content = new JsonArray { NoteMerge.Paragraph(when, italic: true) };
        var text = new List<string> { when };
        foreach (var line in (description ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0).Take(200))
        {
            content.Add(NoteMerge.Paragraph(line));
            text.Add(line);
        }

        static JsonObject Heading(string t) => new() { ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = 2 }, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = t }) };
        static JsonObject Empty() => new() { ["type"] = "paragraph" };
        content.Add(Heading("Agenda"));
        content.Add(new JsonObject { ["type"] = "bulletList", ["content"] = new JsonArray(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(Empty()) }) });
        content.Add(Heading("Notes"));
        content.Add(Empty());
        content.Add(Heading("Action items"));
        content.Add(new JsonObject
        {
            ["type"] = "taskList",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "taskItem", ["attrs"] = new JsonObject { ["checked"] = false }, ["content"] = new JsonArray(Empty()) }),
        });
        text.AddRange(["Agenda", "", "Notes", "", "Action items", ""]);
        return (new JsonObject { ["schemaVersion"] = 1, ["type"] = "doc", ["content"] = content }, string.Join("\n", text));
    }
}
