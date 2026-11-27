namespace Alondra1.Core.Mail;

/// <summary>Where a message sits in its conversation: the conversation, its size, and whether this is its newest message.</summary>
public sealed record ThreadPlace(string Key, int Count, bool IsNewest);

/// <summary>
/// Conversations (Mail UI spec §5.2, T08) from thread headers only: Message-ID, In-Reply-To and References link
/// messages of the same account. Never grouped by subject alone and never across accounts; a message without these
/// headers (or whose links match nothing in the list) stays on its own.
/// </summary>
public static class MailThreads
{
    /// <summary>"&lt;a@b&gt;" and "a@b" are the same id; ids are compared exactly otherwise (they are case-sensitive).</summary>
    public static string Normalize(string? id) => (id ?? "").Trim().Trim('<', '>').Trim();

    /// <summary>The ids in a References or In-Reply-To header ("&lt;a@x&gt; &lt;b@y&gt;"), in order, without duplicates.</summary>
    public static IReadOnlyList<string> Ids(string? header)
    {
        var result = new List<string>();
        foreach (var part in (header ?? "").Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var id = Normalize(part);
            if (id.Length > 0 && !result.Contains(id))
                result.Add(id);
        }

        return result;
    }

    /// <summary>The References for a reply to <paramref name="parent"/>: its References (or In-Reply-To), then its Message-ID; at most 20.</summary>
    public static string ReferencesFor(StoredMessage parent)
    {
        var ids = Ids(parent.References).ToList();
        if (ids.Count == 0)
            ids.AddRange(Ids(parent.InReplyTo));
        var own = Normalize(parent.MessageId);
        if (own.Length > 0 && !ids.Contains(own))
            ids.Add(own);
        return string.Join(' ', ids.TakeLast(20).Select(id => $"<{id}>"));
    }

    /// <summary>Conversation of every message in <paramref name="messages"/> (keyed by account, folder and uid).</summary>
    public static Dictionary<MessageKey, ThreadPlace> Build(IReadOnlyList<StoredMessage> messages)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            var root = x;
            while (parent[root] != root)
                root = parent[root];
            while (parent[x] != root)
            {
                var next = parent[x];
                parent[x] = root;
                x = next;
            }
            return root;
        }

        void Union(string a, string b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
                parent[rb] = ra;
        }

        // Nodes are scoped to the account, so the same Message-ID in two accounts is never one conversation.
        string Node(StoredMessage m, string id) => m.AccountId + "\u001f" + id;
        string Self(StoredMessage m) => Normalize(m.MessageId) is { Length: > 0 } id ? Node(m, id) : $"{m.AccountId}\u001f#{m.FolderId}\u001f{m.Uid}";

        // Only ids that belong to a message in the list (or are shared by several of them) can link messages.
        foreach (var m in messages)
        {
            var self = Self(m);
            parent.TryAdd(self, self);
            foreach (var id in Ids(m.InReplyTo).Concat(Ids(m.References)))
                Union(self, Node(m, id));
        }

        var groups = messages.GroupBy(m => Find(Self(m))).ToList();
        var result = new Dictionary<MessageKey, ThreadPlace>();
        foreach (var group in groups)
        {
            var members = group.ToList();
            var newest = members.OrderByDescending(m => m.ReceivedUtc).ThenByDescending(m => m.Uid).First();
            foreach (var m in members)
                result[new MessageKey(m.AccountId, m.FolderId, m.Uid)] = new ThreadPlace(group.Key, members.Count, ReferenceEquals(m, newest));
        }

        return result;
    }

    /// <summary>
    /// Conversation view of a date-sorted list: each conversation at the place of its newest message, its other
    /// messages right after it (newest first). Sets <see cref="StoredMessage.ThreadCount"/> and <see cref="StoredMessage.IsThreadReply"/>.
    /// </summary>
    public static List<StoredMessage> Arrange(IReadOnlyList<StoredMessage> sorted)
    {
        var places = Build(sorted);
        var byThread = sorted.GroupBy(m => places[new MessageKey(m.AccountId, m.FolderId, m.Uid)].Key)
            .ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<StoredMessage>(sorted.Count);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in sorted)
        {
            var key = places[new MessageKey(m.AccountId, m.FolderId, m.Uid)].Key;
            if (!done.Add(key))
                continue;
            var members = byThread[key];
            var newestFirst = members.OrderByDescending(x => x.ReceivedUtc).ThenByDescending(x => x.Uid).ToList();
            // The conversation's first row is the one the sort put first (newest when sorted newest first).
            var head = m;
            result.Add(head);
            head.ThreadCount = members.Count;
            head.IsThreadReply = false;
            foreach (var other in newestFirst.Where(x => !ReferenceEquals(x, head)))
            {
                other.ThreadCount = members.Count;
                other.IsThreadReply = true;
                result.Add(other);
            }
        }

        return result;
    }

    /// <summary>Clears the conversation marks (conversation view off).</summary>
    public static void Clear(IEnumerable<StoredMessage> messages)
    {
        foreach (var m in messages)
        {
            m.ThreadCount = 1;
            m.IsThreadReply = false;
        }
    }
}
