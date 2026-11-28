using Alondra1.Core.Cloud;
using Alondra1.Core.Ports;

namespace Alondra1.Core.Tags;

/// <summary>
/// The tag catalog and assignments on this PC: the only writer of tags (like TaskBoard for tasks). Works offline and
/// without an account; when Cloud Sync is on, the store queues each change in the same transaction. Use from the UI
/// thread. Tags are never required and never change the tagged items themselves.
/// </summary>
public sealed class TagBoard(ITagStore store, IClock clock)
{
    private readonly Dictionary<string, Tag> _tags = [];
    private readonly Dictionary<string, TagLink> _links = [];
    private readonly Dictionary<string, MailTagTarget> _mail = [];

    public event Action? Changed;

    /// <summary>Tags by name (culture order).</summary>
    public IReadOnlyList<Tag> Tags => _tags.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();

    public Tag? Find(string id) => _tags.GetValueOrDefault(id);

    public TagLink? FindLink(string id) => _links.GetValueOrDefault(id);

    /// <summary>Every link on this PC, removed ones included (for Cloud Sync set-up).</summary>
    public IReadOnlyCollection<TagLink> AllLinks => _links.Values;

    public Tag? FindByName(string name) => _tags.Values.FirstOrDefault(t => TagRules.NameKey(t.Name) == TagRules.NameKey(name));

    public static string NewId() => Guid.NewGuid().ToString("N");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadTagsAsync(cancellationToken).ConfigureAwait(true);
        _tags.Clear();
        _links.Clear();
        _mail.Clear();
        foreach (var t in snapshot.Tags)
            _tags[t.Id] = t;
        foreach (var l in snapshot.Links)
            _links[l.Id] = l;
        foreach (var m in snapshot.MailTargets)
            _mail[m.Ref] = m;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The live tags on one item, by name.</summary>
    public IReadOnlyList<Tag> TagsOf(string objectType, string objectRef) =>
        _links.Values.Where(l => l.Live && l.ObjectType == objectType && l.ObjectRef == objectRef)
            .Select(l => _tags.GetValueOrDefault(l.TagId)).OfType<Tag>()
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The live links of a tag (all item types).</summary>
    public IReadOnlyList<TagLink> LinksOf(string tagId) => _links.Values.Where(l => l.Live && l.TagId == tagId).ToList();

    public int UsageCount(string tagId) => _links.Values.Count(l => l.Live && l.TagId == tagId);

    /// <summary>
    /// The refs of items of one type carrying the tags: any of them, or (matchAll) every one of them.
    /// An empty tag list matches nothing.
    /// </summary>
    public IReadOnlySet<string> ItemsWith(string objectType, IReadOnlyCollection<string> tagIds, bool matchAll = false)
    {
        if (tagIds.Count == 0)
            return new HashSet<string>();
        var byItem = _links.Values.Where(l => l.Live && l.ObjectType == objectType && tagIds.Contains(l.TagId))
            .GroupBy(l => l.ObjectRef);
        return byItem.Where(g => !matchAll || tagIds.All(id => g.Any(l => l.TagId == id))).Select(g => g.Key).ToHashSet();
    }

    /// <summary>Items of a type that carry no live tag (for an "Untagged" filter): those of <paramref name="refs"/> without links.</summary>
    public bool IsTagged(string objectType, string objectRef) => _links.Values.Any(l => l.Live && l.ObjectType == objectType && l.ObjectRef == objectRef && _tags.ContainsKey(l.TagId));

    public MailTagTarget? MailTarget(string mailRef) => _mail.GetValueOrDefault(mailRef);

    // ------------------------------------------------------------------ catalog

    public async Task<Tag> CreateAsync(string name, string? color = null)
    {
        var clean = TagRules.CleanName(name);
        if (FindByName(clean) is not null)
            throw new TagException($"You already have a tag named “{FindByName(clean)!.Name}”.");
        if (_tags.Count >= TagRules.MaxTags)
            throw new TagException($"You can have at most {TagRules.MaxTags} tags.");
        CheckColor(color);
        var now = Stamp();
        var tag = new Tag { Id = NewId(), Name = clean, Color = color, CreatedUtc = now, ModifiedUtc = now };
        var changes = new TagChangeSet();
        changes.UpsertTags.Add(tag);
        await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
        _tags[tag.Id] = tag;
        Changed?.Invoke();
        return tag;
    }

    /// <summary>Renames a tag. Every chip and rule shows the new name, because they refer to the tag by id.</summary>
    public async Task RenameAsync(string id, string name)
    {
        var tag = _tags.GetValueOrDefault(id) ?? throw new TagException("That tag was deleted.");
        var clean = TagRules.CleanName(name);
        if (FindByName(clean) is { } other && other.Id != id)
            throw new TagException($"You already have a tag named “{other.Name}”.");
        await SaveTagAsync(tag, t => t.Name = clean).ConfigureAwait(true);
    }

    public async Task SetColorAsync(string id, string? color)
    {
        CheckColor(color);
        var tag = _tags.GetValueOrDefault(id) ?? throw new TagException("That tag was deleted.");
        await SaveTagAsync(tag, t => t.Color = color).ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes a tag and removes it from every item; the items themselves are never deleted. Only the tag's deletion is
    /// sent to the cloud, which removes its links there too.
    /// </summary>
    public async Task DeleteAsync(string id)
    {
        if (!_tags.ContainsKey(id))
            return;
        var changes = new TagChangeSet();
        changes.DeleteTags.Add(id);
        foreach (var l in _links.Values.Where(l => l.TagId == id))
            (TagRules.IsSynced(l) ? changes.DropLinks : changes.DeleteLinks).Add(l.Id);
        await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
        _tags.Remove(id);
        foreach (var linkId in changes.DropLinks.Concat(changes.DeleteLinks))
            _links.Remove(linkId);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ assignments

    /// <summary>
    /// Sets exactly these tags on the items (adds the missing, removes the others). Used by the tag picker for one
    /// item and for a multi-selection. <paramref name="mailTargets"/> remembers where tagged mail is on this PC.
    /// </summary>
    public async Task SetTagsAsync(string objectType, IReadOnlyCollection<string> objectRefs, IReadOnlyCollection<string> tagIds,
        IEnumerable<MailTagTarget>? mailTargets = null)
    {
        var wanted = tagIds.Where(_tags.ContainsKey).ToHashSet();
        if (wanted.Count != tagIds.Count)
            throw new TagException("A chosen tag was deleted. Choose the tags again.");
        var changes = new TagChangeSet();
        foreach (var objectRef in objectRefs)
        {
            var current = _links.Values.Where(l => l.ObjectType == objectType && l.ObjectRef == objectRef).ToDictionary(l => l.TagId);
            foreach (var tagId in wanted)
            {
                if (current.TryGetValue(tagId, out var link) && link.Live)
                    continue;
                changes.UpsertLinks.Add(link is null ? NewLink(tagId, objectType, objectRef) : Touch(link, removed: false));
            }

            foreach (var link in current.Values.Where(l => l.Live && !wanted.Contains(l.TagId)))
                changes.UpsertLinks.Add(Touch(link, removed: true));
            var after = current.Values.Count(l => l.Live && wanted.Contains(l.TagId)) + wanted.Count(id => !current.TryGetValue(id, out var l) || !l.Live);
            if (after > TagRules.MaxTagsPerItem)
                throw new TagException($"An item can have at most {TagRules.MaxTagsPerItem} tags.");
        }

        changes.MailTargets.AddRange(mailTargets ?? []);
        await ApplyLinksAsync(changes).ConfigureAwait(true);
    }

    /// <summary>Adds (or removes) one tag on the items, leaving their other tags as they are (bulk add/remove).</summary>
    public async Task SetTagAsync(string objectType, IReadOnlyCollection<string> objectRefs, string tagId, bool on, IEnumerable<MailTagTarget>? mailTargets = null)
    {
        if (!_tags.ContainsKey(tagId))
            throw new TagException("That tag was deleted.");
        var changes = new TagChangeSet();
        foreach (var objectRef in objectRefs)
        {
            var id = TagRules.LinkId(tagId, objectType, objectRef);
            var link = _links.GetValueOrDefault(id);
            if (on && link?.Live != true)
            {
                if (TagsOf(objectType, objectRef).Count >= TagRules.MaxTagsPerItem)
                    throw new TagException($"An item can have at most {TagRules.MaxTagsPerItem} tags.");
                changes.UpsertLinks.Add(link is null ? NewLink(tagId, objectType, objectRef) : Touch(link, removed: false));
            }
            else if (!on && link?.Live == true)
            {
                changes.UpsertLinks.Add(Touch(link, removed: true));
            }
        }

        changes.MailTargets.AddRange(mailTargets ?? []);
        await ApplyLinksAsync(changes).ConfigureAwait(true);
    }

    /// <summary>An item was deleted on this PC: its links go too (the cloud removes its own copies when the item's deletion arrives).</summary>
    public async Task ItemDeletedAsync(string objectType, string objectRef)
    {
        var changes = new TagChangeSet();
        foreach (var l in _links.Values.Where(l => l.ObjectType == objectType && l.ObjectRef == objectRef))
            (objectType == TagRules.Mail || !TagRules.IsSynced(l) ? changes.DeleteLinks : changes.DropLinks).Add(l.Id);
        if (changes.IsEmpty)
            return;
        await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
        foreach (var id in changes.DeleteLinks.Concat(changes.DropLinks))
            _links.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes the links of items of a type that no longer exist on this PC (deleted here or by sync). Links of synced
    /// types are dropped locally only: the cloud deletes its copies with the item. Returns how many were removed.
    /// </summary>
    public async Task<int> PruneAsync(string objectType, IReadOnlySet<string> existing)
    {
        var gone = _links.Values.Where(l => l.ObjectType == objectType && !existing.Contains(l.ObjectRef)).ToList();
        if (gone.Count == 0)
            return 0;
        var changes = new TagChangeSet();
        foreach (var l in gone)
            (TagRules.IsSynced(l) ? changes.DropLinks : changes.DeleteLinks).Add(l.Id);
        await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
        foreach (var l in gone)
            _links.Remove(l.Id);
        Changed?.Invoke();
        return gone.Count;
    }

    private async Task ApplyLinksAsync(TagChangeSet changes)
    {
        if (changes.IsEmpty)
            return;
        await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
        foreach (var l in changes.UpsertLinks)
            _links[l.Id] = l;
        foreach (var m in changes.MailTargets)
            _mail[m.Ref] = m;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ sync

    /// <summary>Changes from the cloud (applied without queueing). Deleted tags take their links on this PC with them.</summary>
    public async Task ApplyRemoteAsync(TagChangeSet changes, IReadOnlyCollection<SyncVersion> versions)
    {
        foreach (var tagId in changes.DeleteTags)
        {
            foreach (var l in _links.Values.Where(l => l.TagId == tagId && !changes.DeleteLinks.Contains(l.Id)))
                changes.DropLinks.Add(l.Id);
        }

        await store.ApplyRemoteTagsAsync(changes, versions, CancellationToken.None).ConfigureAwait(true);
        foreach (var t in changes.UpsertTags)
            _tags[t.Id] = t;
        foreach (var id in changes.DeleteTags.Concat(changes.DropTags))
            _tags.Remove(id);
        foreach (var l in changes.UpsertLinks)
            _links[l.Id] = l;
        foreach (var id in changes.DeleteLinks.Concat(changes.DropLinks))
            _links.Remove(id);
        if (!changes.IsEmpty)
            Changed?.Invoke();
    }

    /// <summary>
    /// Two devices created a tag with the same name offline; the cloud kept the first. The local one (never accepted by
    /// the cloud) is merged into it: its items get the cloud tag, and it disappears. Unrelated names are never merged.
    /// Returns how many tags were merged.
    /// </summary>
    public async Task<int> MergeUnsyncedDuplicatesAsync(IReadOnlySet<string> syncedTagIds)
    {
        var merged = 0;
        foreach (var group in _tags.Values.GroupBy(t => TagRules.NameKey(t.Name)).Where(g => g.Count() > 1).ToList())
        {
            var keep = group.FirstOrDefault(t => syncedTagIds.Contains(t.Id));
            if (keep is null)
                continue;
            foreach (var extra in group.Where(t => t.Id != keep.Id && !syncedTagIds.Contains(t.Id)).ToList())
            {
                var changes = new TagChangeSet();
                foreach (var l in _links.Values.Where(l => l.TagId == extra.Id).ToList())
                {
                    changes.DeleteLinks.Add(l.Id);
                    if (l.Live && _links.GetValueOrDefault(TagRules.LinkId(keep.Id, l.ObjectType, l.ObjectRef))?.Live != true)
                        changes.UpsertLinks.Add(NewLink(keep.Id, l.ObjectType, l.ObjectRef));
                }

                changes.DropTags.Add(extra.Id);
                await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
                _tags.Remove(extra.Id);
                foreach (var id in changes.DeleteLinks)
                    _links.Remove(id);
                foreach (var l in changes.UpsertLinks)
                    _links[l.Id] = l;
                merged++;
            }
        }

        if (merged > 0)
            Changed?.Invoke();
        return merged;
    }

    // ------------------------------------------------------------------ helpers

    private async Task SaveTagAsync(Tag tag, Action<Tag> change)
    {
        var next = tag.Clone();
        change(next);
        next.ModifiedUtc = Stamp();
        var changes = new TagChangeSet();
        changes.UpsertTags.Add(next);
        await store.ApplyTagsAsync(changes, CancellationToken.None).ConfigureAwait(true);
        _tags[next.Id] = next;
        Changed?.Invoke();
    }

    private TagLink NewLink(string tagId, string objectType, string objectRef)
    {
        var now = Stamp();
        return new TagLink { Id = TagRules.LinkId(tagId, objectType, objectRef), TagId = tagId, ObjectType = objectType, ObjectRef = objectRef, CreatedUtc = now, ModifiedUtc = now };
    }

    private TagLink Touch(TagLink link, bool removed)
    {
        var next = link.Clone();
        next.Removed = removed;
        next.ModifiedUtc = Stamp();
        return next;
    }

    private static void CheckColor(string? color)
    {
        if (color is not null && !TagRules.Colors.Contains(color))
            throw new TagException("Choose one of the tag colors.");
    }

    private string Stamp() => clock.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
}
