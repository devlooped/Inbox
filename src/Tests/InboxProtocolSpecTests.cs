namespace Tests;

/// <summary>
/// Structural checks on the shipped Inbox Client Protocol (ICP) spec (<c>docs/INBOX.md</c>).
/// The artifact is the spec; this test drives that file on disk, not a reimplementation.
/// </summary>
public class InboxProtocolSpecTests
{
    static string LoadSpec()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "docs", "INBOX.md");
        if (File.Exists(fromOutput))
            return File.ReadAllText(fromOutput);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "INBOX.md");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("docs/INBOX.md not found from " + AppContext.BaseDirectory);
    }

    readonly string spec = LoadSpec();

    [Fact]
    public void Spec_is_non_empty_prose()
    {
        Assert.True(spec.Length > 8_000, $"spec too short: {spec.Length}");
        Assert.Contains("# Inbox Client Protocol (ICP)", spec, StringComparison.Ordinal);
        Assert.Contains("JSON-RPC 2.0", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_is_ndjson_no_batch_stderr_logs_initialize_first()
    {
        Assert.Contains("NDJSON", spec, StringComparison.Ordinal);
        Assert.Contains("One JSON object per line", spec, StringComparison.Ordinal);
        Assert.Contains("batch arrays MUST NOT", spec, StringComparison.Ordinal);
        Assert.Contains("stderr", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never protocol", spec, StringComparison.Ordinal);
        Assert.Contains("MUST be the first RPC", spec, StringComparison.Ordinal);
        Assert.Contains("\"0.1\"", spec, StringComparison.Ordinal);
        Assert.Contains("named `params` object", spec, StringComparison.Ordinal);
        Assert.Contains("-32000", spec, StringComparison.Ordinal);
        Assert.Contains("stable token", spec, StringComparison.Ordinal);
        Assert.Contains("method name: `event`", spec, StringComparison.Ordinal);
        Assert.Contains("`params.topic`", spec, StringComparison.Ordinal);
        Assert.Contains("`params.kind`", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_states_are_new_offline_online()
    {
        Assert.Contains("`new`", spec, StringComparison.Ordinal);
        Assert.Contains("`offline`", spec, StringComparison.Ordinal);
        Assert.Contains("`online`", spec, StringComparison.Ordinal);
        Assert.Contains("States are exactly `new` | `offline` | `online`", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Common_methods_are_whatsbox_v1_nouns()
    {
        string[] methods =
        [
            "initialize",
            "session.connect",
            "session.pair",
            "session.disconnect",
            "session.logout",
            "session.status",
            "subscribe",
            "unsubscribe",
            "directory.list",
            "directory.get",
            "messages.send",
            "messages.read",
        ];

        var index = IndexSection();
        foreach (var method in methods)
            Assert.Contains($"`{method}`", index, StringComparison.Ordinal);

        // Product-native verbs must not appear as rows in the method index.
        Assert.DoesNotContain("discord.send", index, StringComparison.Ordinal);
        Assert.DoesNotContain("slack.postMessage", index, StringComparison.Ordinal);
        Assert.DoesNotContain("graph.chats", index, StringComparison.Ordinal);
        Assert.DoesNotContain("telegram.sendMessage", index, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.postMessage", index, StringComparison.Ordinal);
        Assert.Contains("There are **no** per-product method names", spec, StringComparison.Ordinal);
        Assert.Contains("No history / search / backfill RPCs", spec, StringComparison.Ordinal);
        Assert.Contains("canonical topic", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("directory.list", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_kinds_and_session_kinds_match_whatsbox_plus_auth()
    {
        foreach (var kind in new[] { "message", "reaction", "ack", "meta" })
            Assert.Contains($"`{kind}`", spec, StringComparison.Ordinal);

        foreach (var part in new[] { "text", "image", "video", "audio", "document", "sticker", "location", "unknown" })
            Assert.Contains($"`{part}`", spec, StringComparison.Ordinal);

        foreach (var kind in new[] { "online", "offline", "logged_out", "overflow", "qr", "oauth", "device_code", "token_required", "paired", "pair_error" })
            Assert.Contains($"`{kind}`", spec, StringComparison.Ordinal);

        Assert.Contains("$session", spec, StringComparison.Ordinal);
        Assert.Contains("$directory", spec, StringComparison.Ordinal);
        Assert.Contains("`upsert`", spec, StringComparison.Ordinal);
        Assert.Contains("`remove`", spec, StringComparison.Ordinal);
        Assert.Contains("`ready`", spec, StringComparison.Ordinal);
        Assert.Contains("always `contents[]`", spec, StringComparison.Ordinal);
        Assert.Contains("`kind: text` / `kind: image` envelopes", spec, StringComparison.Ordinal);
        Assert.Contains("Envelope `text` / `path` / `react`", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_are_store_based_relative_paths()
    {
        Assert.Contains("initialize.files", spec, StringComparison.Ordinal);
        Assert.Contains("relative paths", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RPC payloads MUST carry", spec, StringComparison.Ordinal);
        Assert.Contains("files_required", spec, StringComparison.Ordinal);
        Assert.Contains("path_escape", spec, StringComparison.Ordinal);
        Assert.Contains("text-only", spec, StringComparison.Ordinal);
        Assert.Contains("Exclusive lock", spec, StringComparison.Ordinal);
        Assert.Contains("stdin EOF", spec, StringComparison.Ordinal);
        Assert.Contains("never bytes on the RPC", spec, StringComparison.Ordinal);
        Assert.Contains("One process, one store", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_discord_slack_teams_telegram_matrix()
    {
        Assert.Contains("## 15. Discord mapping", spec, StringComparison.Ordinal);
        Assert.Contains("## 16. Slack mapping", spec, StringComparison.Ordinal);
        Assert.Contains("## 17. Microsoft Teams mapping", spec, StringComparison.Ordinal);
        Assert.Contains("## 18. Telegram mapping", spec, StringComparison.Ordinal);
        Assert.Contains("### 18.1 Bot API", spec, StringComparison.Ordinal);
        Assert.Contains("### 18.2 MTProto user client", spec, StringComparison.Ordinal);
        Assert.Contains("## 19. Matrix mapping", spec, StringComparison.Ordinal);
        Assert.Contains("WhatsApp reference profile", spec, StringComparison.Ordinal);

        Assert.Contains("self-bot", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bot only", spec, StringComparison.Ordinal);
        Assert.Contains("Socket Mode", spec, StringComparison.Ordinal);
        Assert.Contains("notificationUrl", spec, StringComparison.Ordinal);
        Assert.Contains("getUpdates", spec, StringComparison.Ordinal);
        Assert.Contains("MTProto", spec, StringComparison.Ordinal);
        Assert.Contains("/sync", spec, StringComparison.Ordinal);
        Assert.Contains("Olm/Megolm", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_matrix_covers_auth_reply_react_read_files_difficulty()
    {
        var matrix = MatrixSection();
        foreach (var product in new[] { "WhatsApp", "Discord", "Slack", "Teams", "Telegram Bot", "Telegram user", "Matrix" })
            Assert.Contains(product, matrix, StringComparison.Ordinal);

        foreach (var header in new[] { "Auth", "Live path", "Reply", "Reactions", "Mark-read", "Files", "Diff" })
            Assert.Contains(header, matrix, StringComparison.Ordinal);

        Assert.Contains("`attachments`", matrix, StringComparison.Ordinal);

        Assert.Contains("thread_ts", spec, StringComparison.Ordinal);
        Assert.Contains("conversations.mark", spec, StringComparison.Ordinal);
        Assert.Contains("hosted ingress", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsupported", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("| `live` |", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("| `e2ee` |", matrix, StringComparison.Ordinal);
        Assert.Contains("\"quote\"", matrix, StringComparison.Ordinal);
        Assert.Contains("\"context\"", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("per_message", matrix, StringComparison.Ordinal);
    }

    [Fact]
    public void Gaps_have_degraded_behavior_not_silent_ignore()
    {
        Assert.Contains("not “unsupported, ignore”", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Silent no-ops are forbidden", spec, StringComparison.Ordinal);
        Assert.Contains("`read: \"none\"`", spec, StringComparison.Ordinal);
        Assert.Contains("`reply: \"context\"`", spec, StringComparison.Ordinal);
        Assert.Contains("`reply: \"quote\"`", spec, StringComparison.Ordinal);
        Assert.Contains("**not** aliases", spec, StringComparison.Ordinal);
        Assert.Contains("MUST NOT put JSON `true` or `false` on `reply`, `read`, or `attachments`", spec, StringComparison.Ordinal);
        Assert.Contains("`false` is not `\"none\"`", spec, StringComparison.Ordinal);
        Assert.Contains("`attachments: \"single\"`", spec, StringComparison.Ordinal);
        Assert.Contains("capability: \"attachments\"", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("per_message", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reply\":true", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("\"read\":false", spec, StringComparison.Ordinal);
        Assert.Contains("There is **no** `live` capability", spec, StringComparison.Ordinal);
        Assert.Contains("There is **no** `e2ee` capability", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("\"e2ee\":", spec, StringComparison.Ordinal);
        Assert.Contains("`profile: \"bot\"`", spec, StringComparison.Ordinal);
        Assert.Contains("identity: \"bot\"", spec, StringComparison.Ordinal);
        Assert.Contains("### 2.5 Hosted ingress", spec, StringComparison.Ordinal);
        Assert.Contains("no** `webhook.register` method", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_implements_common_rpcs_once()
    {
        Assert.Contains("## 12. Client-once rules", spec, StringComparison.Ordinal);
        Assert.Contains("implements the methods, events, store/files rules, and error tokens in this document **once**", spec, StringComparison.Ordinal);
        Assert.Contains("Opaque `topic` / `by` / `context` strings", spec, StringComparison.Ordinal);
        Assert.Contains("`product` + `identity` + `capabilities`", spec, StringComparison.Ordinal);
        Assert.Contains("`user` / `group`", spec, StringComparison.Ordinal);
        Assert.Contains("WhatsBox `0.1` client codec", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook.register", IndexSection(), StringComparison.Ordinal);
        Assert.Contains("Clients MUST NOT branch on how the daemon obtained an event", spec, StringComparison.Ordinal);
        Assert.Contains("Always `messages.read` with `by` copied from the event", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("{to, ids, by?}", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosted_ingress_is_behind_the_bridge_not_a_new_rpc()
    {
        Assert.Contains("### 2.5 Hosted ingress", spec, StringComparison.Ordinal);
        Assert.Contains("Azure Web PubSub", spec, StringComparison.Ordinal);
        Assert.Contains("Service Bus", spec, StringComparison.Ordinal);
        Assert.Contains("SignalR", spec, StringComparison.Ordinal);
        Assert.Contains("Mapping stays in the bridge", spec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MUST NOT be advertised as `\"poll\"`", spec, StringComparison.Ordinal);
        Assert.Contains("There is **no** `live` capability", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("\"live\":\"push\"", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("\"live\": \"push\"", spec, StringComparison.Ordinal);
        Assert.Contains("offline catch-up", spec, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Send_reply_react_shape_matches_whatsbox()
    {
        Assert.Contains("\"contents\":", spec, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"text\"", spec, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"hello\"", spec, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"out/photo.jpg\"", spec, StringComparison.Ordinal);
        Assert.Contains("\"reply\": {\"id\":", spec, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"reaction\"", spec, StringComparison.Ordinal);
        Assert.Contains("\"by\": \"me\"", spec, StringComparison.Ordinal);
        Assert.Contains("emoji", spec, StringComparison.Ordinal);
        Assert.Contains("\"context\":", spec, StringComparison.Ordinal);
        Assert.Contains("{to, contents, reply?, context?}", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("{to, text?, path?, reply?, react?}", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_is_opaque_grouping_not_a_topic()
    {
        Assert.Contains("### 4.5 `context`", spec, StringComparison.Ordinal);
        Assert.Contains("e.context ?? e.id", spec, StringComparison.Ordinal);
        Assert.Contains("`capabilities.reply` is `\"context\"`", spec, StringComparison.Ordinal);
        Assert.Contains("`reply: \"quote\"` daemons", spec, StringComparison.Ordinal);
        Assert.Contains("is **not** a topic", spec, StringComparison.Ordinal);
        Assert.Contains("Group by `context`", spec, StringComparison.Ordinal);
        Assert.Contains("Discord **threads are channels**", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("`reply: \"thread\"`", spec, StringComparison.Ordinal);
        Assert.Contains("### 21.7 Stricter agnostic Reply", spec, StringComparison.Ordinal);
        Assert.Contains("zero** capability branch", spec, StringComparison.Ordinal);
    }

    string IndexSection()
    {
        var start = spec.IndexOf("## 13. v1 method index", StringComparison.Ordinal);
        Assert.True(start >= 0, "missing method index section");
        var end = spec.IndexOf("\n## 14.", start, StringComparison.Ordinal);
        Assert.True(end > start, "method index not bounded");
        return spec[start..end];
    }

    string MatrixSection()
    {
        var start = spec.IndexOf("## 20. Capability and difficulty matrix", StringComparison.Ordinal);
        Assert.True(start >= 0, "missing capability matrix section");
        var end = spec.IndexOf("\n## 21.", start, StringComparison.Ordinal);
        Assert.True(end > start, "matrix not bounded");
        return spec[start..end];
    }
}
