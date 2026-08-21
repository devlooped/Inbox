using Inbox;

namespace WhatsDemo;

/// <summary>
/// One contact, persisted under its canonical LID/group JID. Alternate ids
/// (<c>pn</c>, <c>handle</c>) live on the body and are indexed in memory.
/// </summary>
public readonly record struct DirectoryAlias(string? Handle, string? Name, string? Pn = null, bool Me = false)
{
    public string? Label => Me ? "me" : NonEmpty(Handle) ?? NonEmpty(Name);

    public bool HasLabel => Label is not null;

    public bool HasBody => HasLabel || NonEmpty(Pn) is not null || Me;

    public DirectoryAlias Merge(DirectoryAlias other)
        => new(
            NonEmpty(other.Handle) ?? Handle,
            NonEmpty(other.Name) ?? Name,
            NonEmpty(other.Pn) ?? Pn,
            Me || other.Me);

    public static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Canonical <c>lid/jid → properties</c> store plus in-memory lookup maps
/// (pn, handle, topic) rebuilt on import and on each update.
/// </summary>
public sealed class DirectoryBook
{
    readonly Dictionary<string, DirectoryAlias> byTopic = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> lookup = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    public bool HasLabel(string? id)
    {
        if (id is null || !IsLookupId(id))
            return false;
        lock (gate)
            return TryResolve(id, out var alias) && alias.HasLabel;
    }

    /// <summary><c>handle ?? name ?? id</c>, preferring explicit event fields when present.</summary>
    public string Display(string? id, string? handle = null, string? name = null)
    {
        if (id is "me" || IsMe(id))
            return "me";
        if (DirectoryAlias.NonEmpty(handle) is { } h)
            return h;
        if (DirectoryAlias.NonEmpty(name) is { } n)
            return n;
        if (id is null || !IsLookupId(id))
            return id ?? "";
        lock (gate)
        {
            if (TryResolve(id, out var alias) && alias.Label is { } label)
                return label;
        }

        return id;
    }

    public bool Remember(DirectoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var changed = Remember(row.Topic, row.Handle, row.Name, row.Pn, fromDirectory: true);
        if (row.Participants is { } parts)
        {
            foreach (var part in parts)
                changed |= Remember(part.Topic, part.Handle, part.Name, part.Pn, fromDirectory: true);
        }

        return changed;
    }

    public bool Remember(string? id, string? handle, string? name, string? pn = null, bool fromDirectory = false)
    {
        if (id is null || !IsLookupId(id))
            return false;
        var incoming = new DirectoryAlias(
            DirectoryAlias.NonEmpty(handle),
            DirectoryAlias.NonEmpty(name),
            FriendlyPhone(pn) ?? FriendlyPhone(id));
        if (!incoming.HasBody)
            return false;

        lock (gate)
            return TryFriendlyPhone(id, out var phone)
                ? MergeIntoCanonical(phone, incoming)
                : Put(id, incoming, fromDirectory);
    }

    public void Import(IReadOnlyDictionary<string, DirectoryAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        foreach (var (id, alias) in aliases.OrderBy(static pair => IsPhoneId(pair.Key) ? 1 : 0))
        {
            Remember(id, alias.Handle, alias.Name, alias.Pn ?? FriendlyPhone(id));
            if (alias.Me)
                MarkMe(id);
        }
    }

    public bool IsMe(string? id)
    {
        if (id is "me")
            return true;
        if (id is null || !IsLookupId(id))
            return false;
        lock (gate)
            return TryResolve(id, out var alias) && alias.Me;
    }

    /// <summary>Marks this topic as the current user. Clears <c>me</c> on every other row.</summary>
    public bool MarkMe(string? topic)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var (key, alias) in byTopic.ToArray())
            {
                if (!alias.Me || key == topic)
                    continue;
                byTopic[key] = alias with { Me = false };
                changed = true;
            }

            if (topic is null || !IsLookupId(topic))
            {
                if (changed)
                    RebuildLookups();
                return changed;
            }

            if (byTopic.TryGetValue(topic, out var existing))
            {
                if (!existing.Me)
                {
                    byTopic[topic] = existing with { Me = true };
                    changed = true;
                }
            }
            else
            {
                byTopic[topic] = new DirectoryAlias(null, null, Me: true);
                changed = true;
            }

            if (changed)
                RebuildLookups();
            return changed;
        }
    }

    /// <summary>Canonical LID/group JID keys only; <c>pn</c>/<c>handle</c> are fields, not extra keys.</summary>
    public IReadOnlyDictionary<string, DirectoryAlias> Snapshot()
    {
        lock (gate)
        {
            return byTopic
                .Where(static pair => pair.Value.HasBody && !IsPhoneId(pair.Key))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        }
    }

    public static bool IsLookupId(string? id)
        => id is { Length: > 0 } && id[0] != '$' && !id.Equals("me", StringComparison.Ordinal);

    public static bool IsGroupId(string? id)
        => id is { Length: > 0 } && id.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

    public static bool IsUserLid(string? id)
        => id is { Length: > 0 } && id.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);

    public static bool IsPnJid(string? id)
        => id is { Length: > 0 } && (
            id.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("@c.us", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>group</c> for <c>@g.us</c>, <c>user</c> for <c>@lid</c> / PN / phone digits.</summary>
    public static string? KindOf(string? id)
        => IsGroupId(id) ? "group"
            : IsUserLid(id) || IsPhoneId(id) ? "user"
            : null;

    public static bool IsPhoneId(string? id)
        => TryFriendlyPhone(id, out _);

    /// <summary>
    /// Digits only: strips <c>+</c> and <c>@s.whatsapp.net</c> / <c>@c.us</c>,
    /// and inserts Argentina's mobile <c>9</c> after country code <c>54</c> when missing.
    /// </summary>
    public static string? FriendlyPhone(string? id)
        => TryFriendlyPhone(id, out var phone) ? phone : null;

    /// <summary>Phone arguments become digits; LID/group/handle are unchanged.</summary>
    public static string NormalizeTopic(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var trimmed = id.Trim();
        return TryFriendlyPhone(trimmed, out var phone) ? phone : trimmed;
    }

    public static bool TryFriendlyPhone(string? id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? phone)
    {
        phone = null;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var s = id.Trim();
        const string wa = "@s.whatsapp.net";
        const string cUs = "@c.us";
        if (s.EndsWith(wa, StringComparison.OrdinalIgnoreCase))
            s = s[..^wa.Length];
        else if (s.EndsWith(cUs, StringComparison.OrdinalIgnoreCase))
            s = s[..^cUs.Length];
        if (s.StartsWith('+'))
            s = s[1..];
        if (s.Length == 0)
            return false;
        foreach (var c in s)
        {
            if (!char.IsDigit(c))
                return false;
        }

        phone = CanonicalArgentinaMobile(s);
        return true;
    }

    /// <summary>
    /// WhatsApp stores Argentine mobiles as <c>54 9 + area + subscriber</c>.
    /// Domestic typing is <c>54 + area + subscriber</c> (no <c>9</c>). Insert it.
    /// </summary>
    static string CanonicalArgentinaMobile(string digits)
    {
        if (digits.StartsWith("54", StringComparison.Ordinal)
            && digits.Length >= 12
            && digits[2] != '9')
            return "549" + digits[2..];
        return digits;
    }

    public static IReadOnlyList<string> ChatTopics(IEnumerable<string> topics)
        => [.. topics.Where(IsLookupId).Distinct(StringComparer.Ordinal)];

    bool TryResolve(string id, out DirectoryAlias alias)
    {
        if (byTopic.TryGetValue(id, out alias) && alias.HasBody)
            return true;
        if (lookup.TryGetValue(id, out var topic) && byTopic.TryGetValue(topic, out alias) && alias.HasBody)
            return true;
        if (TryFriendlyPhone(id, out var phone)
            && lookup.TryGetValue(phone, out topic)
            && byTopic.TryGetValue(topic, out alias)
            && alias.HasBody)
            return true;
        alias = default;
        return false;
    }

    bool Put(string topic, DirectoryAlias incoming, bool fromDirectory = false)
    {
        incoming = incoming with { Pn = ClaimPn(topic, incoming.Pn) };
        if (byTopic.TryGetValue(topic, out var existing))
        {
            if (IsGroupId(topic) && !fromDirectory)
                incoming = incoming with { Name = existing.Name, Handle = existing.Handle };
            var merged = existing.Merge(incoming);
            if (merged == existing)
            {
                RebuildLookups();
                return false;
            }

            byTopic[topic] = merged;
            RebuildLookups();
            return true;
        }

        byTopic[topic] = incoming;
        RebuildLookups();
        return true;
    }

    string? ClaimPn(string topic, string? pn)
    {
        if (pn is null)
            return null;
        List<string>? stolen = null;
        foreach (var (other, alias) in byTopic)
        {
            if (other != topic && alias.Pn == pn)
                (stolen ??= []).Add(other);
        }

        if (stolen is not null)
        {
            foreach (var other in stolen)
                byTopic[other] = byTopic[other] with { Pn = null };
        }

        return pn;
    }

    bool MergeIntoCanonical(string pn, DirectoryAlias incoming)
    {
        if (lookup.TryGetValue(pn, out var topic))
            return Put(topic, incoming with { Pn = pn });
        if (incoming.Handle is { } handle && lookup.TryGetValue(handle, out var byHandle))
            return Put(byHandle, incoming with { Pn = pn });

        foreach (var (canonical, existing) in byTopic)
        {
            if (existing.Pn == pn)
                return Put(canonical, incoming with { Pn = pn });
        }

        return false;
    }

    void RebuildLookups()
    {
        lookup.Clear();
        foreach (var (topic, alias) in byTopic)
        {
            AddLookup(topic, topic);
            AddLookup(alias.Pn, topic);
            AddLookup(alias.Handle, topic);
            if (alias.Pn is { } pn && pn.StartsWith("549", StringComparison.Ordinal) && pn.Length >= 13)
                AddLookup("54" + pn[3..], topic);
        }
    }

    void AddLookup(string? key, string topic)
    {
        if (DirectoryAlias.NonEmpty(key) is { } id)
            lookup[id] = topic;
    }
}
