using System.Text;

namespace WhatsDemo;

/// <summary>Subscriptions plus directory aliases persisted as <c>wb.toml</c>.</summary>
public sealed record WhatsBoxDocument(
    IReadOnlyList<string> Subscribe,
    IReadOnlyDictionary<string, DirectoryAlias> Directory)
{
    public WhatsBoxDocument()
        : this([], new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal)) { }
}

/// <summary>Read/write the demo's <c>wb.toml</c> (subscribe list + jid → handle/name).</summary>
public static class WhatsBoxToml
{
    public const string FileName = "wb.toml";

    public static string PathIn(string directory)
        => Path.Combine(directory, FileName);

    public static WhatsBoxDocument Load(string path)
    {
        if (!File.Exists(path))
            return new();
        return Parse(File.ReadAllText(path));
    }

    public static void Save(string path, WhatsBoxDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Format(document));
        File.Move(tmp, path, overwrite: true);
    }

    public static string Format(WhatsBoxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        sb.Append("subscribe = [");
        var topics = DirectoryBook.ChatTopics(document.Subscribe);
        if (topics.Count == 0)
        {
            sb.Append("]\n");
        }
        else
        {
            sb.Append('\n');
            foreach (var topic in topics)
                sb.Append("  ").Append(Quote(topic)).Append(",\n");
            sb.Append("]\n");
        }

        var aliases = document.Directory
            .Where(static pair => pair.Value.HasLabel && DirectoryBook.IsLookupId(pair.Key) && !DirectoryBook.IsPhoneId(pair.Key))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        if (aliases.Count == 0)
            return sb.ToString();

        sb.Append('\n').Append("[directory]\n");
        foreach (var (jid, alias) in aliases)
        {
            sb.Append(Quote(jid)).Append(" = { ");
            var first = true;
            if (alias.Handle is { } handle)
            {
                sb.Append("handle = ").Append(Quote(handle));
                first = false;
            }

            if (alias.Name is { } name)
            {
                if (!first)
                    sb.Append(", ");
                sb.Append("name = ").Append(Quote(name));
                first = false;
            }

            if (DirectoryBook.FriendlyPhone(alias.Pn) is { } pn)
            {
                if (!first)
                    sb.Append(", ");
                sb.Append("pn = ").Append(Quote(pn));
                first = false;
            }

            if (alias.Me)
            {
                if (!first)
                    sb.Append(", ");
                sb.Append("me = true");
            }

            sb.Append(" }\n");
        }

        return sb.ToString();
    }

    public static WhatsBoxDocument Parse(string text)
    {
        var subscribe = new List<string>();
        var directory = new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return new(subscribe, directory);

        var section = "";
        string? tableKey = null;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                ParseTableHeader(line, out section, out tableKey);
                continue;
            }

            if (section.Length == 0 && TryConsumeKey(line, "subscribe", out var subscribeRest))
            {
                var buffer = subscribeRest;
                while (!TryParseStringArray(buffer, subscribe) && i + 1 < lines.Length)
                {
                    i++;
                    buffer += " " + StripComment(lines[i]).Trim();
                }

                continue;
            }

            if (section == "directory" && tableKey is { } key
                && TryConsumeKey(line, "handle", out var handleRest)
                && TryParseQuoted(handleRest.Trim(), out var handle))
            {
                directory[key] = directory.TryGetValue(key, out var existing)
                    ? existing.Merge(new DirectoryAlias(handle, null))
                    : new DirectoryAlias(handle, null);
                continue;
            }

            if (section == "directory" && tableKey is { } nameKey
                && TryConsumeKey(line, "name", out var nameRest)
                && TryParseQuoted(nameRest.Trim(), out var name))
            {
                directory[nameKey] = directory.TryGetValue(nameKey, out var existing)
                    ? existing.Merge(new DirectoryAlias(null, name))
                    : new DirectoryAlias(null, name);
                continue;
            }

            if (section == "directory" && tableKey is { } pnKey
                && TryConsumeKey(line, "pn", out var pnRest)
                && TryParseQuoted(pnRest.Trim(), out var pn))
            {
                directory[pnKey] = directory.TryGetValue(pnKey, out var existing)
                    ? existing.Merge(new DirectoryAlias(null, null, pn))
                    : new DirectoryAlias(null, null, pn);
                continue;
            }

            if (section == "directory" && tableKey is not null
                && TryConsumeKey(line, "kind", out _))
            {
                continue;
            }

            if (section == "directory" && tableKey is { } meKey
                && TryConsumeKey(line, "me", out var meRest)
                && TryParseBool(meRest.Trim(), out var me))
            {
                directory[meKey] = directory.TryGetValue(meKey, out var existing)
                    ? existing.Merge(new DirectoryAlias(null, null, Me: me))
                    : new DirectoryAlias(null, null, Me: me);
                continue;
            }

            if (section == "directory" && tableKey is null
                && TryParseQuotedKey(line, out var jid, out var inline)
                && TryParseInlineAlias(inline, out var alias))
            {
                directory[jid] = directory.TryGetValue(jid, out var existing)
                    ? existing.Merge(alias)
                    : alias;
            }
        }

        return new(DirectoryBook.ChatTopics(subscribe), directory);
    }

    static void ParseTableHeader(string line, out string section, out string? tableKey)
    {
        section = "";
        tableKey = null;
        if (line.Length < 3 || line[0] != '[' || line[^1] != ']')
            return;
        var inner = line[1..^1].Trim();
        if (inner.Equals("directory", StringComparison.OrdinalIgnoreCase))
        {
            section = "directory";
            return;
        }

        const string prefix = "directory.";
        if (!inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;
        section = "directory";
        var key = inner[prefix.Length..].Trim();
        if (TryParseQuoted(key, out var quoted))
            tableKey = quoted;
        else if (key.Length > 0)
            tableKey = key;
    }

    static bool TryParseQuotedKey(string line, out string key, out string rest)
    {
        key = "";
        rest = "";
        if (!TryParseQuoted(line, out key, out var after) || after.Length == 0)
            return false;
        after = after.TrimStart();
        if (after.Length == 0 || after[0] != '=')
            return false;
        rest = after[1..].Trim();
        return key.Length > 0;
    }

    static bool TryParseInlineAlias(string text, out DirectoryAlias alias)
    {
        alias = default;
        text = text.Trim();
        if (text.Length < 2 || text[0] != '{' || text[^1] != '}')
            return false;
        string? handle = null;
        string? name = null;
        string? pn = null;
        var me = false;
        var inner = text[1..^1];
        foreach (var part in SplitTopLevel(inner, ','))
        {
            var piece = part.Trim();
            if (TryConsumeKey(piece, "handle", out var handleRest)
                && TryParseQuoted(handleRest.Trim(), out var h))
                handle = h;
            else if (TryConsumeKey(piece, "name", out var nameRest)
                     && TryParseQuoted(nameRest.Trim(), out var n))
                name = n;
            else if (TryConsumeKey(piece, "pn", out var pnRest)
                     && TryParseQuoted(pnRest.Trim(), out var p))
                pn = p;
            else if (TryConsumeKey(piece, "kind", out _))
                continue;
            else if (TryConsumeKey(piece, "me", out var meRest)
                     && TryParseBool(meRest.Trim(), out var flag))
                me = flag;
        }

        alias = new DirectoryAlias(handle, name, pn, me);
        return alias.HasBody;
    }

    static bool TryParseStringArray(string text, List<string> into)
    {
        var span = text.Trim();
        if (span.Length == 0 || span[0] != '[')
            return false;
        span = span[1..];
        var items = new List<string>();
        while (true)
        {
            span = span.TrimStart();
            if (span.Length == 0)
                return false;
            if (span[0] == ']')
            {
                into.AddRange(items);
                return true;
            }
            if (span[0] == ',')
            {
                span = span[1..];
                continue;
            }

            if (!TryParseQuoted(span, out var item, out var rest))
                return false;
            if (item.Length > 0)
                items.Add(item);
            span = rest;
        }
    }

    static bool TryParseBool(string text, out bool value)
    {
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    static bool TryConsumeKey(string line, string key, out string rest)
    {
        rest = "";
        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            return false;
        var after = line[key.Length..].TrimStart();
        if (after.Length == 0 || after[0] != '=')
            return false;
        rest = after[1..].TrimStart();
        return true;
    }

    static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                quoted = !quoted;
            else if (c == separator && !quoted)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                quoted = !quoted;
            else if (c == '#' && !quoted)
                return line[..i];
        }

        return line;
    }

    static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }

        sb.Append('"');
        return sb.ToString();
    }

    static bool TryParseQuoted(string text, out string value)
        => TryParseQuoted(text.AsSpan(), out value, out _);

    static bool TryParseQuoted(string text, out string value, out string rest)
        => TryParseQuoted(text.AsSpan(), out value, out rest);

    static bool TryParseQuoted(ReadOnlySpan<char> text, out string value, out string rest)
    {
        value = "";
        rest = "";
        text = text.TrimStart();
        if (text.Length < 2 || text[0] != '"')
            return false;
        var sb = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                sb.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '"')
            {
                value = sb.ToString();
                rest = text[(i + 1)..].ToString();
                return true;
            }

            sb.Append(c);
        }

        return false;
    }
}
