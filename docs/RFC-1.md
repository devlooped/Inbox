# RFC-1: Subscribe by JID, directory-owned identity, and reply ContextInfo

**Status:** Accepted  

**Date:** 2026-08-20  
**Affects:** protocol (`docs/PRODUCT.md`), native `whatsbox`, managed `WhatsBox` client  
**Motivating consumer:** `src/WhatsDemo`

---

## 1. Summary

Building a real companion REPL against v0.1 showed three contract holes that look small on the wire and are fatal in the product:

1. **`subscribe` / `unsubscribe` resolved phones and names inside the daemon.** The bus mixed addressing with directory search, failed atomically on ambiguous names, and hid `directory.list` — the API that is supposed to be the address book.
2. **Directory rows were mutated from live message metadata.** Sender push names and from-me `RecipientAlt` phone numbers were written onto the wrong chats. After a few inbound events, a group named *Nosotros* became *agus*, and the paired account inherited a peer’s `pn`.
3. **`messages.send` reply used WhatsApp `ContextInfo` incorrectly.** Setting `remoteJid` on a same-chat quote rendered `Group • {name}`. Omitting `quotedMessage` and sending `participant` as the literal `"me"` produced no quote bubble at all. WhatsApp does not resolve quotes server-side.

This RFC argues those fixes belong in the **protocol and both libraries**, not only in the demo. The demo is the smallest client that actually *uses* subscribe, directory, send, reply, and mark-read together; the failures showed up there first.

---

## 2. Motivation

v0.1 describes whatsbox as an address book plus a live pub/sub of chats the client asked for. The daemon owns the WhatsApp socket and a LID-first directory. The client owns UX: who to watch, how to label them, which message to quote.

A companion REPL (`WhatsDemo`) is the right stress test for that split. It must:

- pair, persist a store, and restore subscriptions across restarts
- let a human type a group subject or a phone and end up subscribed to a **canonical JID**
- show `@` completions for subscribed chats (self always present; groups by subject, users by handle)
- send text, including replies (`@[topic]:[msgid]`)
- mark inbound group messages read

Every one of those paths hit a daemon or protocol assumption that was convenient for a one-shot RPC and wrong for a long-lived client.

---

## 3. Problems

### 3.1 Subscribe as a resolver

**Was:** `subscribe` / `unsubscribe` / `initialize.subscribe` accepted LID, PN JID, phone digits, and (briefly) unique directory names. The daemon usync’d phones and, if a name matched one row, subscribed that chat.

**What broke:**

| Input | What happened |
|---|---|
| `/subscribe Nosotros` | `invalid_topic` until name lookup was added; then the daemon owned search that `directory.list` already provides |
| `/subscribe +54…` | Native `IsOnWhatsApp` on subscribe, not on the directory |
| Ambiguous `"Ana"` | Whole call failed; no way to pick |
| Phone while offline | `not_found` or a temporary PN topic, depending on path |

The demo (and any agent) then could not implement a picker. Search lived in the wrong process. `directory.list` was unused for the most important “find a chat” action.

**Change:** Subscribe/unsubscribe accept **canonical JIDs only** (LID, `@g.us`, unmapped PN JID, `$directory`). Names, handles, and bare phones are `invalid_topic`. The client lists the directory and passes `row.topic`. Send/read/`directory.get` still accept phones.

### 3.2 Directory identity mixed with message envelopes

**Was:** `touchDirectoryFromMessage` upserted the chat row with `ev.Name` (push name) and `ev.PN`. From-me 1:1 events used `RecipientAlt` as PN on the **sender** path.

**What broke:**

- A group subject was overwritten by the last author’s push name (`Nosotros` → `agus`). `@` completion then showed `@agus` for a group, so the subscribed group looked missing.
- From-me mapping stole the peer’s phone onto the self LID and labelled the peer with the account’s push name (`Kzu`).
- Demo `wb.toml` then persisted the wrong `name` / `pn`. Restart made it look like a client bug.

**Change (native):**

- Do not apply sender push name or sender PN onto a **group** row.
- Do not map sender→PN on **from-me** 1:1 events (recipient alt is the peer).
- Do not rename the 1:1 peer with our own `PushName` on from-me.

**Change (demo, defensive):** Author/`topicName` events must not overwrite an existing group subject. `kind` is not stored in `wb.toml`; it is `DirectoryBook.KindOf(jid)` (`@g.us` → group, `@lid` / PN / digits → user).

### 3.3 Reply `ContextInfo` was not a WhatsApp quote

whatsmeow does not offer `Reply()`. A quote is `ExtendedTextMessage.ContextInfo`:

| Field | Role |
|---|---|
| `stanzaID` | Original message id |
| `participant` | Original **author JID** (1:1 and groups) |
| `quotedMessage` | Snapshot of the body; WhatsApp does **not** fetch this |
| `remoteJID` | Only when quoting a **different** chat |

**Was:** Native set `remoteJid` to the destination chat. `by: "me"` was forwarded as the string `"me"`, so `participant` was omitted. `reply.text` did not exist on the RPC, so `quotedMessage` was empty.

**What broke:**

- 1:1 quote rendered **`Group • Analía`** / **`Group • @danielkzu`**.
- Group quote rendered **`Group • Nosotros`**.
- After dropping `remoteJid`, the Group chrome went away — and so did the bubble, because there was still no stub and no participant LID.

**Change:**

- Protocol: `reply.text` optional; native always attaches `QuotedMessage.Conversation`.
- Resolve `by: "me"` to the paired LID before building `participant` (1:1 and groups).
- Never set `remoteJid` on a same-chat quote.
- C# `MessageReply(Id, By, Text?)`.
- Demo: `@[topic]:[msgid]` fills `text` from the in-memory last-message cache.

### 3.4 Group mark-read without `by`

**Was:** Demo called `ReadAsync(topic, [id])` for every inbound text. Protocol requires `by` in groups and **forbids** it in 1:1.

**What broke:** First inbound group message printed `invalid_params: by is required for groups` in the REPL, looking like a send failure.

**Change:** Demo passes `by` only when `topic` is `@g.us`. Native already enforced the rule; the consumer was wrong.

---

## 4. Proposed contract (already prototyped in this workspace)

### 4.1 Protocol

**`subscribe` / `unsubscribe` / `initialize.subscribe`**

- Accept LID, group JID (`@g.us`), PN JID (`@s.whatsapp.net`), `$directory`.
- Reject phones, names, handles (`invalid_topic`).
- Unmapped PN JID remains a temporary topic until `remap`.

**`messages.send.reply`**

```json
{ "id": "3EB0…", "by": "me", "text": "original body" }
```

`id` + `by` required. `text` optional but required for a visible quote on clients that do not have history (every whatsbox consumer).

**Send / read / `directory.get`**

Unchanged: LID, PN JID, or phone; daemon usyncs phones.

### 4.2 Native

- `resolveSubscribeTopic` separate from `resolveTopic` (the latter still usyncs for send/get).
- `applyMapping` on usync so a later `directory.get` sees the row (subscribe itself no longer usyncs).
- `touchDirectoryFromMessage` and from-me mapping as in §3.2.
- `replyContext(chat, id, by, text)`: `stanzaID`, `participant` if `by` is a JID, `quotedMessage`, no `remoteJid`.
- Debug: `send reply to=… id=… by=… text=…` at verbosity `debug`.

### 4.3 Managed client (`WhatsBox`)

- `MessageReply.Text`.
- `TopicsParams` documented as canonical JIDs.
- Stderr of the sidecar also written to `Console.Error` so a TUI can show native debug lines.
- README examples: `ListDirectoryAsync` then `SubscribeAsync(row.Topic)`.

### 4.4 Demo (proof of the client-side half)

Not a protocol change, but the required consumer pattern:

| Concern | Demo behaviour |
|---|---|
| Find a chat | LID/`@g.us` pass through; otherwise `directory.list`; 1 hit auto-subscribe; N hits completion picker |
| Persist | `.store/wb.toml` — subscribe list + `jid → handle/name/pn/me` (no `kind`) |
| Self | Always subscribed; labelled `me`; excluded from `/unsubscribe` |
| `@` | Subscribed chats; groups by subject; users `@handle` / `@name` |
| `/unsubscribe` | Completion of subscribed chats minus self |
| Reply | `RecentChats` supplies `id`, `by`, and **text** |
| Group read | `ReadAsync(..., by)` only for `@g.us` |
| Debug loop | `demo.ps1` packs `WhatsBox` + `WhatsBox.{rid}` 42.42.42, publishes Debug, runs with repo cwd |

---

## 5. Trade-offs

| Choice | Rejected | Why |
|---|---|---|
| Subscribe is JID-only; client lists directory | Daemon unique-name / phone subscribe | Search and disambiguation are UX. `directory.list` already paginates and matches name/`pn`/handle/JID. Ambiguous `"Ana"` needs a picker, not `invalid_topic`. |
| Phones still allowed on send/get | Phones nowhere | Send-to-number without a directory row remains useful. Subscribe is “watch this chat”; the chat’s primary key is LID/`@g.us`. |
| Keep unmapped PN as temporary subscribe topic | Reject PN JID too | `remap` is already in the spec. Forcing LID-only would break the first subscribe before populate finishes. |
| Optional `reply.text`, always emit stub | Daemon looks up quote text | v1 stores **no** message bodies. The client that just displayed the line is the only place that has the text. |
| `participant` = resolved LID even for `by: "me"` | Omit participant in 1:1 | whatsmeow examples set participant to the quoted author in DMs. `"me"` is not a JID; the phone ignores the quote. |
| No `remoteJid` on same-chat quotes | Set `remoteJid` to the chat | WhatsApp treats it as a citation **from** that chat → `Group • {name}`. |
| Do not persist demo `kind` | Store `kind` in toml | Redundant with `@g.us` / `@lid` / `@s.whatsapp.net`. Stale `kind` caused group rows to look like users. |
| Breaking subscribe input | Keep phone/name on subscribe “for convenience” | Convenience was a trap: usync on subscribe, name collisions, no picker. One release of “phones work on subscribe” trains clients incorrectly. |

**Compatibility:** Any client that today calls `SubscribeAsync(["+15551234567"])` or `subscribe` with a group subject must list first. Send/read/get are unchanged. `reply.text` is additive; old clients still send, they just will not get a quote bubble.

---

## 6. Impact on a demo (and any TUI / agent)

Without these changes the REPL cannot be honest:

- **Subscribe by name** (`Nosotros`) only works if the **app** lists and, if needed, asks. That is the intended product: directory is the address book; subscribe is a JID set.
- **`@` list** is only as good as directory identity. If inbound events rename groups and mix `pn`, completions lie. Users think the group is missing when it is labelled as the last speaker.
- **Replies** are the difference between a companion and a second keyboard. Correlation ids (`id`+`by`) are required; they are not sufficient for rendering. The demo must keep a tiny last-message cache and pass `text`. The daemon must not invent `remoteJid`.
- **Group read** must copy `by` from the event. The protocol was already strict; the demo had to learn it.
- **Self-chat** is a first-class 1:1 (own LID). It stays subscribed, shows as `me`, and cannot be unsubscribed — otherwise the default send target disappears after a subscribe result that omitted it.

Dogfooding cost: Debug must restore **locally packed** `WhatsBox` 42.42.42 plus `WhatsBox.{rid}`, or the REPL keeps running a published sidecar that still sets `remoteJid`. `demo.ps1` exists because `dotnet run` on the demo project does not rebuild the Go binary.

Usability **gain** once the contract is this RFC:

```
/subscribe Nosotros          → directory.list → one group → subscribe LID
/subscribe Ana               → two rows → picker → subscribe chosen topic
@[Nosotros] hello            → send to group JID
@[Nosotros]:[3EB0…] thanks   → reply with id, by=author LID, text=cached body
/unsubscribe                 → list subscribed chats except me
```

No daemon heuristics. The REPL’s completions are the disambiguation UI other clients will have to invent anyway.

---

## 7. Test evidence

Native protocol tests cover: subscribe LID+group succeeds; phone/name/`$foo` are `invalid_topic` (atomic); PN subscribe still remaps; group inbound does not rename the subject; `reply.by=me` becomes the paired LID; `reply.text` reaches `SendText`.

`reply_test.go` asserts no `remoteJid`, `participant` set for a 1:1 LID author, and a `quotedMessage` stub even when text is empty.

Demo tests cover: JID vs `directory.list` resolve and multi-hit picker; group name not overwritten by author events; `KindOf` from suffix; `@` lists group subjects; `/unsubscribe` completions omit self; `ReplyTo` includes cached text.

---

## 8. Recommendation

Land the protocol wording in `PRODUCT.md` (already drafted), ship native + `WhatsBox` together (sidecar and `MessageReply.Text` must not skew), and treat WhatsDemo as the reference client for:

- list-then-subscribe
- last-message cache for `reply.text`
- `me` as a subscription that is not user-removable
- group `messages.read.by`

Do not put name or phone resolution back on `subscribe`. Do not look up quote bodies in the daemon unless v2 stores message text — which v1 explicitly refuses.
