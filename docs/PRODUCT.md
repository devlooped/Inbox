# whatsbox specification

**Status:** v0.1 (session-locked)  
**License (product):** MIT  
**Language:** Go  
**Library:** [whatsmeow](https://github.com/tulir/whatsmeow)

whatsbox is a local WhatsApp companion: one process owns a linked-device session and exposes it as a **JSON-RPC 2.0 pub/sub bus over stdio**. Clients subscribe to chats (and two system topics), send a small set of actions, and receive live events. It is **not** an archive, a search engine, or a WhatsApp CLI.

This document is the product contract. `external/whatsmeow` and `external/wacli` in this workspace are **reference only**; whatsbox is a greenfield repo and does not share databases, packages, or command surface with wacli.

---

## 1. Product

### 1.1 What it is

A locked WhatsApp Web companion that is:

1. An **address book** (directory of users and chats, no transcripts).
2. A **live pub/sub** of chats the client asked for.
3. A **same-machine blob channel** (paths on disk, never bytes on the RPC).

One binary. One process. One store. One WhatsApp socket.

### 1.2 Who it is for

Agents and local apps that can spawn a process, speak newline-delimited JSON-RPC, and optionally share a directory for files. Not humans typing commands (pairing QR rendering is the client’s job).

### 1.3 v1 does

- Pair via QR (in-band, on `$session`).
- Connect, auto-reconnect, disconnect, logout.
- Directory populate + list/get + live `$directory` updates.
- Subscribe/unsubscribe to chats by JID (LID-first).
- Receive live messages, receipts (`ack`), and in-chat `meta` on the chat topic.
- Send text, send file (if `files` is set), reply, react.
- Explicit mark-read (`messages.read`). Never automatic.

### 1.4 v1 does not

- Message history, search, backfill, export, FTS.
- Store message bodies or last-message previews.
- Typing indicators or “available” presence.
- Edit, revoke (“delete for everyone”).
- Pair-code / phone pairing, passkey / WebAuthn pairing.
- Channels, status broadcasts, calls, blocklist admin, group admin (add/remove/promote as RPCs).
- MCP / ACP / A2A as the native protocol.
- Unix/TCP socket mux (stdio only).
- Named multi-account in one process.
- Topic wildcards (`#`, `+`, `$all`).
- Default store path.

---

## 2. Process and store

### 2.1 Invocation

```text
whatsbox [--store ABSOLUTE_PATH] [--version] [--help]
```

- The process reads JSON-RPC from **stdin** and writes JSON-RPC to **stdout**.
- **stderr** is logs only. Never protocol.
- `--version` / `--help` print and exit (no RPC).
- There is **no default store**. A store path must be provided via `--store` and/or `initialize.store` (see §5.1).
- One process, one store, one WhatsApp session. Two processes on the same store must fail on the store lock (whatsmeow `StreamReplaced` if they both connect).

### 2.2 Lifetime

```text
spawn → initialize [connect:true ⇒ implicit session.connect]
      → (else session.connect [+ implicit pair]) → events
stdin EOF → Disconnect WhatsApp → exit
```

- Pairing **keys** remain on disk across process restarts until `session.logout`.
- Live messages missed while the process is down are **gone** (at-most-once). WhatsApp may still deliver a short offline catch-up on the next `connect`; that is applied only to **current** subscriptions.
- “Warm daemon with zero clients” is **not** v1 (that needs a socket). The parent *is* the process.

### 2.3 Store layout

Chosen directory (absolute path), created if missing, mode `0700`:

| Path | Owner | Contents |
|---|---|---|
| `<store>/LOCK` | whatsbox | Exclusive lock. Fail fast if held. |
| `<store>/session.db` | whatsmeow | Device identity, Signal keys, app-state, LID map (whatsmeow’s SQL store). |
| `<store>/whatsbox.db` | whatsbox | Directory only (users, chats, LID↔PN labels). **No messages.** |
| Store files in general | whatsmeow / whatsbox | Sidecars WAL/SHM as SQLite requires. Mode `0600`. |

`files` (blob exchange) is **not** inside the store unless the client points it there. It is a client-owned directory passed at `initialize`.

### 2.4 `session.logout`

1. Unlink the device from WhatsApp if connected.
2. Delete **all whatsmeow session state** under the store (`session.db` and sidecars).
3. Delete **`whatsbox.db`** (directory is account-scoped).
4. Clear subscriptions.
5. Status becomes `new`.

The store **directory** may remain; it is empty of identity.

---

## 3. Transport

### 3.1 Framing

- JSON-RPC 2.0.
- UTF-8.
- **One JSON object per line** (NDJSON). Messages **must not** contain raw newlines (compact `json.Marshal`).
- stdout: only valid JSON-RPC request/response/notification objects.
- stderr: logs. Clients must not treat stderr as protocol or as “the command failed.”

### 3.2 Envelope

**Request**

```json
{"jsonrpc":"2.0","id":"1","method":"initialize","params":{}}
```

**Result**

```json
{"jsonrpc":"2.0","id":"1","result":{}}
```

**Error**

```json
{"jsonrpc":"2.0","id":"1","error":{"code":-32001,"message":"store_required"}}
```

**Notification** (server → client only in v1, except pairing is still notifications not server-requests):

```json
{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@..."}}
```

- All live traffic uses **one method name: `event`**.
- `params.topic` is always present.
- `params.kind` discriminates the payload.
- Client → server notifications are not used in v1. Cancellation of in-flight RPCs is not required in v1 (keep requests short).

### 3.3 Protocol version

`initialize.params.version` is the client’s protocol version. v1 value: **`"0.1"`**.

If the daemon does not support it, return an error with the versions it does support. Do not speak events before a successful `initialize`.

### 3.4 Verbosity

`initialize.params.verbosity` controls stderr only. Recommended values: `error` | `warn` | `info` (default) | `debug`. Until `initialize`, the process may log at `warn`.

---

## 4. Identity

### 4.1 Canonical topics

| Entity | Canonical topic | Notes |
|---|---|---|
| 1:1 user | LID JID, e.g. `123456789012345@lid` | **Primary key.** |
| Group | `120363…@g.us` | Unchanged. |
| System | `$session`, `$directory` | `$` prefix is reserved. Reject any other `$…` subscribe. |

**Phone-number JIDs** (`15551234567@s.whatsapp.net`) are a **mutable label** (`pn`), like a display name. They are not topics once a LID is known.

### 4.2 Input acceptance

These fields accept **LID, PN JID, or a phone number** (`+15551234567` or digits):

- `subscribe` / `unsubscribe` topic entries that are not `$directory`
- `messages.send.to`
- `messages.read.to`
- `directory.get` id

The daemon **normalizes** phones and resolves through (in order): local LID map, then WhatsApp `IsOnWhatsApp` when a live connection exists.

- Result / `topic` on the wire is always **canonical** (LID or group JID).
- Unknown phone → error. **Do not** create a ghost topic.
- Groups cannot be addressed by phone.

### 4.3 Remap

When a LID↔PN mapping appears later (HistorySync, group participants, usync):

1. Upsert `$directory` with the LID key and updated `pn`.
2. If a subscription was held on the old PN JID, **move** it to the LID.
3. Emit `$session` event `{kind:"remap", from, to}`.
4. Further chat `event`s use the LID `topic`.

### 4.4 `by` and `me`

`by` is the **author of the original message** (not the logged-in user sending the RPC).

| Value | Meaning |
|---|---|
| a JID | That user (normalized to LID when known). |
| the string `"me"` | The paired account’s LID. |

- **Reply and react:** `by` is **required** (1:1 and groups). Clients copy it from the inbound event. Use `"me"` when targeting their own message.
- **`messages.read`:** `by` is **required for groups** (and all `ids` in that call must share that author — whatsmeow `MarkRead` constraint). **Omit in 1:1.**
- **Inbound events:** `by` is `"me"` or a LID. There is no separate `self` field.
- **Status snapshot:** the paired LID is `me` only. Do not also emit `self`.

Why `id` alone is not enough is specified in §11 (trade-offs) and follows WhatsApp’s key `(chat, id, fromMe, participant)`.

---

## 5. RPC methods

Only these methods exist in v1.

### 5.1 `initialize`

**Must be the first RPC.** Second `initialize` → error.

```json
{
  "version": "0.1",
  "store": "D:\\data\\whatsbox",
  "files": "D:\\data\\wa-files",
  "subscribe": ["$directory", "123…@lid"],
  "verbosity": "info",
  "connect": true
}
```

| Field | Required | Description |
|---|---|---|
| `version` | yes | Protocol version (`"0.1"`). |
| `store` | if `--store` omitted | Absolute store path. |
| `files` | no | Absolute blob directory. Missing → text-only (no inbound media download, no icons, `path` on send errors). |
| `subscribe` | no | Initial topics; applied **before** any event is eligible for dispatch. `$session` is implicit and need not be listed. |
| `verbosity` | no | stderr level. |
| `connect` | no | If `true`, implicit `session.connect` after subscriptions are installed (same rules: `new` ⇒ pair). Default `false`: do not open WhatsApp. |

**Store resolution**

| `--store` | `initialize.store` | Result |
|---|---|---|
| set | omitted | use `--store` |
| omitted | set | use `initialize.store` |
| set | set, same absolute path | ok |
| set | set, different | error `store_mismatch` |
| omitted | omitted | error `store_required` |

Create the store directory if missing (`0700`).

Apply `subscribe` (plus implicit `$session`) **before** any event is eligible for dispatch, and **before** `connect:true` runs, so pairing / offline catch-up hits those topics.

- `connect: false` or omitted: do **not** open WhatsApp. Result is a status snapshot (`new` or `offline`).
- `connect: true`: then run **`session.connect`** (§5.2) as part of this RPC. The result is that connect result (`online`, or pairing via `$session` `qr` if `new`). The call lasts as long as `session.connect` would (including waiting for a QR scan when unpaired).

**Result** (example, `connect` omitted / false, already paired)

```json
{"version":"0.1","status":"offline","me":"111@lid","topics":["$session","$directory"]}
```

If never paired and `connect` is not true: `"status":"new"`, `me` omitted.

Subscriptions from `initialize.subscribe` plus implicit `$session` are reflected in `topics` (canonicalized).

### 5.2 `session.connect`

Bring up the WhatsApp socket. `initialize` with `connect: true` is this method after a successful init (same semantics, one round-trip).

- If status is `new`: **implicit `session.pair`** (QR events on `$session`), then connect.
- If `offline`: connect with existing keys.
- If `online`: no-op success.
- On success, start auto-reconnect with backoff until disconnect, logout, `logged_out`, or stdin EOF.
- Subscriptions persist across reconnects (they are client intent, not connection state).
- After `online`, start **async directory populate** (§7.3). Do not block the connect result on populate.

**Result:** same shape as `session.status`.

### 5.3 `session.pair`

Show QR and wait for link.

- Already paired (`offline` or `online`): **no-op**, return current status. To re-pair the client must `session.logout` first.
- QR codes are `$session` events `{kind:"qr", code}` as WhatsApp rotates them. Client renders the latest. No per-QR reply.
- Success: `{kind:"paired"}` then the session is linked. Pairing **ends connected** (socket up) when invoked standalone; when invoked from `connect`, `connect` finishes `online`.
- No pair-code / phone path in v1.
- If WhatsApp demands passkey/WebAuthn: `{kind:"pair_error", ...}` and the RPC fails. v1 does not implement passkey.

### 5.4 `session.disconnect`

Drop the WhatsApp socket. Process stays. Status `offline` if still paired, else `new`. Further RPCs are valid (`connect` again, `logout`, directory reads from cache).

### 5.5 `session.logout`

See §2.4. Result is `session.status` with `status: "new"`.

### 5.6 `session.status`

```json
{
  "me": "111@lid",
  "status": "online",
  "topics": ["$session", "$directory", "123@lid", "120363…@g.us"]
}
```

| `status` | Meaning | `me` |
|---|---|---|
| `new` | No session in the store | omitted |
| `offline` | Keys exist, socket down | your LID |
| `online` | Socket up | your LID |

`topics` is the current subscription set (always includes `$session`).

### 5.7 `subscribe` / `unsubscribe`

```json
{"topics":["$directory","+15551234567","120363…@g.us"]}
```

**Result**

```json
{"topics":["$directory","999@lid","120363…@g.us"]}
```

- Result lists **canonical** topics actually applied (resolved).
- Display name / `pn` / roster are **not** on this result. Call `directory.get` with the canonical topic if needed.
- Unknown / unresolvable entries fail the whole call with `error` naming the bad topic (no partial apply).
- `$session` cannot be unsubscribed.
- Subscribing an already-subscribed topic is a no-op.
- After `initialize`, these may be called at any time. Newly subscribed chats get **no replay**; next live event onward.

### 5.8 `directory.list`

```json
{"query":"ada","kind":"user","limit":50,"cursor":""}
```

| Field | Description |
|---|---|
| `query` | Optional. Matches name, `pn`, `handle`, and LID/group JID string. |
| `kind` | Optional. `user` \| `group`. |
| `limit` | Optional. Implementation default (e.g. 50), max 100. |
| `cursor` | Opaque; omit or `""` for the first page. |

**Result:** `{items: [DirectoryRow], cursor?}`. No `cursor` (or empty) means last page.

There is **no** sort-by-last-message (we do not store it). Order: implementation-defined but stable (recommend name, then jid).

### 5.9 `directory.get`

```json
{"id":"+15551234567","icon":false}
```

| Field | Description |
|---|---|
| `id` | LID, PN JID, or phone number. Required. |
| `icon` | Optional. When omitted, defaults to **whether `initialize.files` was set**. |

**Result:** one `DirectoryRow`. Groups include `participants` (canonical JIDs + names / `pn` / `handle` if known).

| `icon` | `files` set | Behavior |
|---|---|---|
| omitted | yes | Fetch preview icon (write under `files`, set `icon` on the result). |
| omitted | no | Do not fetch; omit `icon`. |
| `true` | yes | Fetch. |
| `true` | no | Error `files_required`. |
| `false` | either | Never fetch; omit `icon` even if a previous get stored an icon path. |

`list` / `$directory` upsert never carry `icon`.

Missing entity → error `not_found`.

### 5.10 `messages.send`

```json
{
  "to": "+15551234567",
  "text": "hello",
  "path": "out/photo.jpg",
  "reply": {"id": "3EB0…", "by": "999@lid"},
  "react": {"id": "3EB0…", "by": "me", "emoji": "👍"}
}
```

| Field | Description |
|---|---|
| `to` | Chat (LID / PN / phone / group JID). Required. |
| `text` | Body. Optional if `path` or `react` is set. |
| `path` | Relative path under `files`. Requires `files`. Optional. |
| `reply` | Quote. `id` + `by` required. |
| `react` | Reaction. `id` + `by` required. `emoji` empty string = remove reaction. May be sent with no `text`/`path`. |

Rules:

- At least one of `text`, `path`, `react`.
- `path` without `files` → error `files_required`.
- `path` must resolve under `files` (no `..` escape).
- **Result:** `{id, topic}` where `topic` is the **canonical** chat JID after normalization. No timestamp.
- If the client is subscribed to that topic, a normal inbound-shaped `event` is also emitted with `by: "me"`. If not subscribed, the RPC result is the only acknowledgement.

### 5.11 `messages.read`

```json
{"to":"120363…@g.us","ids":["3EB0…","3EB1…"],"by":"999@lid"}
```

- Sends WhatsApp **read** receipts (blue ticks). Not delivery acks (those are protocol-level and always happen).
- `to` = chat. Required.
- `ids` = message ids. Required, non-empty.
- `by` = author of those messages. **Required when `to` is a group.** Omit for 1:1. All `ids` in one call must be from that same author.
- **Result:** `{topic}` (canonical).
- Never automatic.

---

## 6. Events (`method: "event"`)

Every notification:

```json
{"jsonrpc":"2.0","method":"event","params":{"topic":"…","kind":"…", "...":"..."}}
```

### 6.1 `$session` (always on)

| `kind` | Payload | When |
|---|---|---|
| `qr` | `code` (string to render as QR) | Pairing |
| `paired` | `me` (LID) | Pair success |
| `pair_error` | `message` | Pair failed / passkey required |
| `online` | `me` | Socket up (including after reconnect) |
| `offline` | `reason?` | Socket down; will reconnect unless disconnect/logout/EOF |
| `logged_out` | `reason?` | Session revoked remotely; status becomes `new` after local cleanup |
| `remap` | `from`, `to` | Subscription/topic moved PN → LID |
| `overflow` | `topic`, `dropped` | Per-topic queue overflow (see §6.5) |

### 6.2 `$directory`

| `kind` | Payload |
|---|---|
| `upsert` | `DirectoryRow` (no `participants` array — keep catalog small; use `directory.get` for members) |
| `remove` | `topic` (canonical jid) |
| `ready` | `{generated: n}` after the first populate wave. Later upserts may still arrive. |

**When to emit `upsert` vs chat `meta`**

| Change | `$directory` | Chat topic `kind: meta` |
|---|---|---|
| You were added / new group / new 1:1 discovered | `upsert` | `meta` only if already subscribed |
| You left / removed / group gone | `remove` | `meta` then no further events |
| Rename, topic/description, icon | `upsert` (name/icon/flags) | `meta` (`rename` / `topic` / `icon`) |
| Announce / locked / ephemeral | optional flags on upsert | `meta` |
| Member join/leave/promote/demote | **no** (optional `participantCount` only) | `meta` |
| Mute / pin / archive (app-state) | `upsert` flags | no |

### 6.3 Chat topics (bare JID)

Inbound `kind` values:

| `kind` | Meaning |
|---|---|
| `text` | Body in `text` |
| `image` `video` `audio` `document` `sticker` | Media; `path` if `files` set and download succeeded; else `error` on the media object |
| `location` | `lat`, `lng` (and optional `name` / `address`). No file. |
| `reaction` | `emoji`, `target` (id of the reacted-to message), `by` |
| `ack` | Receipts: `ids`, `ack` = `delivered` \| `read` \| `played` |
| `meta` | Group/room notice: `action` + fields (`join`/`leave`/`promote`/`demote`/`rename`/`topic`/`icon`/…) |
| `unknown` | Anything else (polls, view-once, buttons, …). **No blob.** May include a short `label`. |

Common fields on message-like events:

```json
{
  "topic": "120363…@g.us",
  "kind": "text",
  "id": "3EB0…",
  "by": "999@lid",
  "handle": "@ada",
  "topicName": "Family",
  "byName": "Ada",
  "text": "hi"
}
```

- `by` is `"me"` or a LID.
- `handle` is the author’s WhatsApp username **with a leading `@`**, when known. Omit otherwise. Stored and emitted with `@` so `handle ?? byName ?? by` is unambiguous.
- `topicName` is the chat’s display name (group subject, or the 1:1 peer’s name). Omit if unknown.
- `byName` is the author’s display name (contact / push / real in-group name). When `by` is `"me"`, this is the paired account’s directory name if known. Omit if unknown.
- There is no `pn` on chat events. The phone JID lives on the directory row; look up `by` (or the 1:1 `topic`) via `directory.get` when needed.
- `path` is relative to `files` when media was written.
- Unsubscribed chats: **protocol-ack, then drop**. No event, no download.

`ack` and `meta` still get `topicName` when known. `handle` / `byName` only when `by` is present. Location `name` is the place name, not `topicName`. `unknown` `label` is the kind hint (`view_once`, `poll`), not a person name.

### 6.4 Delivery semantics

| Situation | Guarantee |
|---|---|
| Live event on a subscribed topic | At-most-once, arrival order per topic |
| Late subscribe | No replay |
| Process restart | No replay; client re-`initialize` + `subscribe` / `connect` |
| Offline catch-up from WhatsApp | Treated as live; only current subscriptions |
| HistorySync message bodies | Never persisted, never emitted |

### 6.5 Backpressure

Per-topic in-memory queue (recommend 256). On overflow: drop **oldest**, emit `$session` `{kind:"overflow", topic, dropped}`. Never block the WhatsApp handler loop.

---

## 7. Directory data model

### 7.1 `DirectoryRow`

```json
{
  "topic": "999@lid",
  "kind": "user",
  "name": "Ada",
  "handle": "@ada",
  "pn": "15551234567@s.whatsapp.net",
  "icon": "in/_dir/999_at_lid.jpg",
  "muted": false,
  "pinned": false,
  "archived": false,
  "participantCount": 0
}
```

| Field | Applies to | Notes |
|---|---|---|
| `topic` | all | Canonical JID. |
| `kind` | all | `user` \| `group`. |
| `name` | all | Best display name (contact full/push, group subject). |
| `handle` | user | WhatsApp username with a leading `@`. May be missing. Groups omit. |
| `pn` | user | Phone-number JID label; may be missing or change. Not on chat events. |
| `icon` | all | Relative path under `files`. Only after `directory.get` when `icon` is true (or omitted and `files` was set). Not on list/upsert by default. |
| `muted` `pinned` `archived` | all | From app-state. |
| `participantCount` | group | Optional; **not** the full roster. |
| `participants` | group, **`get` only** | `[{topic, name?, pn?, handle?}]`. |

No `lastMessage`, no preview text, no unread counts derived from bodies.

### 7.2 SQLite (`whatsbox.db`)

Normative intent, not a frozen migration:

- `chats(topic PK, kind, name, handle, pn, muted, pinned, archived, participant_count, icon_id, updated_at)`
- `contacts` may be folded into `chats` where `kind=user`, or a sibling table keyed by LID.
- `lid_map(lid PK, pn UNIQUE)` — labels + resolution cache.
- `participants(group_topic, user_topic, role, name, handle)` — for `directory.get`, not for `$directory` upserts.

Never: `messages`, FTS, media keys-as-history.

### 7.3 Populate (async, after `online`)

Do **not** block `session.connect` on this. Stream `$directory` upserts; finish with `ready`.

Sources (all metadata-only):

1. `FetchAppState` — contacts, mute, pin, archive.
2. `GetJoinedGroups` — groups, participants, LID pairs.
3. `Store.Contacts.GetAllContacts` — names from the session store.
4. HistorySync **headers only** — conversation id/name/flags, `pushnames`, `phoneNumberToLidMappings`, `inlineContacts`. **Never** persist `Conversation.messages`.

There is **no** WhatsApp-side contact/chat search. `directory.list` is local SQL.

Keep the directory warm from live events (new group, push name, contact app-state) even when the chat is not subscribed — that is how a client discovers a JID to subscribe to. Still **no message bodies**.

---

## 8. Files

Set only if `initialize.files` is an absolute directory the daemon may read/write.

| Direction | Path | Who writes |
|---|---|---|
| Inbound media | `{files}/in/{safeTopic}/{id}[.ext]` | daemon, after download, then `event` |
| Inbound icon from `directory.get` | `{files}/in/_dir/{safeTopic}[.ext]` | daemon, then return `icon` |
| Outbound | any file **under** `{files}` | client; RPC carries a **relative** path |

- `safeTopic` = filesystem-safe encoding of the canonical JID (replace `@` / path separators).
- Daemon **never deletes**. TTL/GC is the client’s.
- No `files` → no downloads, no icons, send-with-`path` errors.
- Unsubscribed inbound media is **never** downloaded.
- View-once and other `unknown` kinds are **never** written.
- Write then notify (no truncated-file race).
- Reject paths that escape `files`.

---

## 9. Session state machine

```text
                  initialize
                      │
                      ▼
                   (idle)
                      │
         initialize.connect:true
         or session.connect
                      │
         ┌────────────┴────────────┐
         │ status=new              │ status=offline
         ▼                         ▼
   implicit pair                 Connect()
   $session qr…                      │
         │                           │
         ▼                           ▼
      paired ──────────────────► online
                                      │
                    disconnect / socket drop
                                      │
                                      ▼
                                   offline ◄── auto-reconnect ──► online
                                      │
                                 session.logout
                                      │
                                      ▼
                                     new
```

- `logged_out` from WhatsApp: treat as logout cleanup (wipe local identity) so the store cannot pretend to be linked.
- Presence: **quiet**. Do not `SendPresence(available)`. Typing will not arrive; that is intended.

---

## 10. Errors

JSON-RPC application errors use codes in the `-32000`…`-32099` range (or a stable `message` token). Minimum tokens:

| Token | Meaning |
|---|---|
| `not_initialized` | RPC before `initialize` |
| `already_initialized` | Second `initialize` |
| `store_required` | No `--store` and no `initialize.store` |
| `store_mismatch` | Both set and not the same path |
| `store_locked` | Another process holds the store |
| `unsupported_version` | `version` not `"0.1"` |
| `not_paired` | Action needs keys (should be rare; `connect` pairs) |
| `pair_error` | QR pairing failed / passkey required |
| `not_found` | directory.get / unknown topic resolution |
| `invalid_topic` | `$` reserved, bad JID, unsubscribe `$session` |
| `files_required` | Blob op without `files` |
| `path_escape` | `path` outside `files` |
| `invalid_params` | Missing `by` on group read / reply / react, etc. |
| `disconnected` | Needs `online` (send, read, pair-time usync) |

---

## 11. Key trade-offs

Decisions from the design session, with the alternative we rejected.

| Choice | Rejected | Why |
|---|---|---|
| Live pub/sub, **no message history** | wacli-style SQLite transcript + FTS | Product is a bus, not a mailbox. History is WhatsApp’s problem and a different binary. |
| Discard unsubscribed (after protocol-ack) | Persist everything, filter in the client | Disk/privacy stay bounded. Agents watch three chats. Decrypt cost is still account-wide (WhatsApp cannot filter). |
| JSON-RPC NDJSON on stdio | Native MCP, ACP, A2A, protobuf-on-the-wire | Same envelope agents already speak. MCP spawn-per-host fights exclusive device keys. ACP/A2A have the wrong nouns (diffs, Tasks). |
| MCP as a **future adapter**, not the session owner | MCP-native server | Tools are pull; WhatsApp is push. Adapter can wrap this spec later. |
| Stdio only in v1 | signal-cli daemon + unix socket from day one | One subscriber, one process. Socket is the day two agents attach. Implies EOF tears down WA. |
| Greenfield repo | Mode of wacli / shared `internal/` | Archive semantics leak. Steal lessons, not packages. `external/` is documentation. |
| Pairing **in** the RPC (`connect` ⇒ implicit pair) | Separate `whatsbox pair` CLI | One process, always JSON-RPC. QR is a `$session` event the client renders. |
| QR only | Pair-code / passkey | Pair-code is headless-nice but out of v1. Passkey is a different UX; fail clearly. |
| `session.pair` no-op if already linked | Error or force re-pair | Re-pair is destructive. Logout is the reset. |
| No default store | `~/.whatsbox` | Client (or `--store`) must choose. Avoids hidden state. |
| `--store` **or** `initialize.store` | Flag-only or init-only | Either is enough; conflict fails loud. |
| LID as topic / directory PK; PN is a label | Canonical phone JID | Agents will see LIDs. PN can change or be missing. `remap` is cheaper than dual topics forever. |
| Accept phone / PN / LID on input | JID-only | Agents will pass numbers. `IsOnWhatsApp` + lid map resolve. No ghost topics. |
| `$` system topics, bare JIDs, no `chats/` prefix | `chats/{jid}/ack` subtopics | MQTT-shaped. After `$session`/`$directory`, everything else is a chat. |
| Fold ack + meta into the chat topic | Separate `/ack` `/meta` subscriptions | Volume is low (no typing). Client discards `kind`. |
| Split directory vs chat meta by **list-row vs room notice** | Dual-publish every member join, or meta-only-on-directory | Catalog stays small. One-group bots are not flooded. Rename/icon/you-were-added are both a list fact and a room event. |
| Client-owned `files` dir; daemon never GC | Daemon TTL/claim protocol | Simplest daemon. No `files` ⇒ no blobs at all. |
| Icons only on `directory.get` when `files` is set | Background-download every avatar | Icons are media. Don’t fetch a roster of pictures on populate. |
| Always require `by` on reply/react; `"me"` special | Infer from `to`+`id`, or in-memory id cache | WhatsApp keys `(chat, id, fromMe, participant)`. No history ⇒ cannot look up author. `to` is the **chat**, not the writer. In 1:1, your messages and theirs share `to`. Groups need `participant`. Client already has `by` on the event. |
| `messages.read` never automatic; `by` required in groups | Auto-read subscribed chats | Quiet linked device. `MarkRead` is per-author in groups. |
| No revoke/edit in v1 | Kitchen-sink send | No use case yet. React + reply are enough. |
| HistorySync harvested for **headers only** | Skip HistorySync entirely, or store bodies | Only conversation headers give the 1:1 thread list at pair time. `GetJoinedGroups` covers groups; app-state covers the address book. Download cost is real; persistence of bodies is not. |
| Quiet presence | Mark available so typing works | A linked device that looks online steals phone notifications. Typing was explicitly out. |
| Auto-reconnect, subs persist | Client re-`connect`s / wipe subs on drop | Process *is* the session. WhatsApp sockets die; client intent does not. |
| One `event` method + `topic` field | Method = topic, or MCP `notifications/wa/…` | JIDs as method names are ugly. One client handler. |
| `unknown` kind instead of dropping or full proto | Expose `waE2E.Message` oneof | Stream stays honest without shipping WhatsApp’s union to agents. |
| Bounded queue + visible overflow | Block WA loop, or silent drop | Session stays up; loss is observable. |
| Logout wipes whatsmeow **and** `whatsbox.db` | Keep directory across logout | Directory is the previous account’s address book. |
| Status `new` \| `offline` \| `online` | `paired`/`disconnected` or booleans | Three exclusive states. `new` = no `me`. |
| Send result `{id, topic}` only | Also timestamp; or echo without subscribe | `topic` is the post-normalization LID/group JID. Subscribe if you want the tape. |

---

## 12. Notes for a future implementation plan

Facts gathered from whatsmeow and wacli that an implementer should not rediscover. Not part of the client-visible contract.

### 12.1 whatsmeow session and events

- `Client.Connect` / `ConnectContext` own the websocket. A second process with the same `session.db` emits `events.StreamReplaced`.
- Event types live in `types/events`: `Message`, `Receipt`, `HistorySync`, `ChatPresence`, `Presence`, `QR`, `PairSuccess`, `LoggedOut`, `Connected`, `Disconnected`, `OfflineSyncPreview` / `OfflineSyncCompleted`, group/app-state/call variants (~70 structs).
- `ChatPresence` (typing) is **not sent by WhatsApp** unless `SendPresence(PresenceAvailable)`. v1 never does that.
- `Presence` (online/last-seen) requires `SubscribePresence(jid)` per user. Out of v1.
- Protocol receipts for incoming messages must still be sent or the phone retries (`UndecryptableMessage` storms). “Discard” is application-layer after whatsmeow has handled the frame.
- `MarkRead(ctx, ids, ts, chat, sender)`: `sender` becomes `participant` only when `chat` is **not** a DM (`DefaultUserServer` / `HiddenUserServer` / Messenger). Multiple ids in one call must share the same author. Group read ⇒ our `by`.
- Delivery receipts while not marked available use type `inactive` (not shown as ticks). `SetForceActiveDeliveryReceipts` exists; v1 stays quiet — do not force active receipts unless a later spec says so.

### 12.2 Send / reply / react

- `SendMessage(ctx, to, *waE2E.Message)`.
- `BuildReaction(chat, sender, id, emoji)` and `BuildMessageKey(chat, sender, id)`: `sender` is the **original author**. Empty/`own` ⇒ `FromMe=true`. In groups, non-self author sets `MessageKey.Participant`.
- Empty reaction text removes the reaction (whatsmeow / WhatsApp convention).
- Reply is a `ContextInfo` on the outgoing proto (`stanzaId` + `participant` in groups + quoted stub). wacli uses `--reply-to` + `--reply-to-sender` for the same reason as `by`.
- `RevokeMessage` / `BuildRevoke` is “delete for everyone.” Explicitly out of v1.
- Newsletter reactions use `NewsletterSendReaction`, not `BuildReaction`. Channels are out of v1.

### 12.3 HistorySync

- Pushed by the phone; you do not request `INITIAL_BOOTSTRAP`.
- Types: `INITIAL_BOOTSTRAP`, `INITIAL_STATUS_V3`, `FULL`, `RECENT`, `PUSH_NAME`, `NON_BLOCKING_DATA`, `ON_DEMAND`.
- Default whatsmeow **downloads** blobs and emits `events.HistorySync`. Set `ManualHistorySyncDownload` to choose: ingest bootstrap/recent/push-name/non-blocking; skip `ON_DEMAND` / do not request backfill.
- `DownloadHistorySync` already writes LID maps, push names, NCT salt, and **message secrets** into the **session** store (`storeHistoricalMessageSecrets`). That is `session.db`, not `whatsbox.db`. Harmless and useful for later reactions on live messages.
- After download, whatsmeow `DeleteMedia`s the history blob on the server — keep that path so the phone does not retry forever.
- `Conversation` header fields useful for directory: `ID`, `name` / `displayName`, `archived`, `pinned`, `muteEndTime`, `participant[]`, `lidJID` / `pnJID`, `parentGroupID`, `description`, `createdAt`. **Ignore `messages`.**
- Root extras: `pushnames`, `phoneNumberToLidMappings`, `inlineContacts`.
- wacli `history backfill` / `ON_DEMAND` is the opposite product. Do not call `BuildHistorySyncRequest`.

### 12.4 Directory sources (no server search)

- **No** `SearchContacts` / `GetAllChats` IQ.
- Contacts: `FetchAppState` (`critical_unblock_low` / `regular*` — `IndexContact`, mute, pin, archive) then `Store.Contacts.GetAllContacts()`.
- Groups: `GetJoinedGroups()` (live, complete). Also fills LID pairs + redacted phones. `GetGroupInfo(jid)` for one group.
- Channels: `GetSubscribedNewsletters()` — out of v1.
- Phones: `IsOnWhatsApp(phones)` (usync). Returns `IsIn`, JID (often LID), `PhoneNumber` (PN), and stores LID mappings.
- Hydrate known JIDs: `GetUserInfo(jids)` (avatar id, status, devices, LID).
- 1:1 **thread list** at pair time ≈ HistorySync conversation IDs. Without it, directory is “groups + address book” until someone messages.

### 12.5 LID vs PN in whatsmeow

- Hidden user server is `lid`. Default user server is `s.whatsapp.net`.
- `BuildMessageKey` treats both DM servers as “no participant.”
- wacli spends a lot of code **canonicalizing LID → PN** for a phone-number-shaped store. whatsbox does the **opposite** (LID canonical, PN label). Do not copy wacli’s `canonicalJID` direction blindly; reuse the **mapping tables**, invert the preference.
- Keep `lid_map` in `whatsbox.db` even though whatsmeow also stores mappings — so `directory` and topic match stay consistent if session internals change.

### 12.6 Media

- Incoming `events.Message` carries encrypted media **metadata**; bytes come from `Client.Download` / `DownloadToFile`.
- Download **only** if subscribed **and** `files` is set.
- wacli caps ~100 MiB; reuse a similar cap.
- View-once is a wrapper (`IsViewOnce*`). Map to `kind: unknown`, do not write files.
- Stickers/voice have format constraints on **send** (WebP 512, OGG/Opus). v1 can require the client to pre-encode; document errors rather than shelling out to ffmpeg in v1 unless cheap.

### 12.7 App-state and wacli lock/delegate

- `FetchAppState(name, fullSync, onlyIfNotSynced)` after connect (and when app-state keys arrive). wacli fetches `regular_high` / `regular_low` so mute/pin/archive/star catch up.
- LTHash mismatch: whatsmeow can request a recovery snapshot. Log and continue; do not store messages.
- wacli store lock + “delegate send to the follow process” exists because **two CLIs cannot share a session**. whatsbox has one process: no delegate IPC. Still take the **lock file** so a leftover wacli/whatsbox cannot double-connect.
- wacli `--events` NDJSON on **stderr** and webhooks are a one-way log, not this protocol. Do not mix.

### 12.8 Pairing

- `GetQRChannel` / `events.QR` with rotating `Codes`. WhatsApp Web shows the first ~60s, then ~20s.
- After scan: `PairSuccess`, then typically a reconnect; wait for `Connected` / our `online` before send.
- `PairPhone` is the pair-code path — **out of v1**.
- `PairPasskeyRequest` / confirmation — **out of v1**; surface `pair_error`.

### 12.9 Suggested implementation slices (not normative)

1. Binary + NDJSON JSON-RPC loop + `initialize` / store lock / `session.status`.
2. `session.pair` / `connect` / reconnect / `$session` qr·online·offline.
3. Directory DB + populate (app-state + groups + HistorySync headers) + `$directory` + list/get.
4. LID-first resolution + `remap` + subscribe match.
5. Chat `event`s (text/unknown/ack/meta) + discard policy + overflow.
6. `messages.send` text + reply/react (`by` / `me`).
7. `files` + inbound download + send path + `directory.get` icon.
8. `messages.read` + logout wipe.

Test with a fake `Client` (wacli’s `fake_wa` pattern) for protocol tests; live whatsmeow only for a thin pairing/connect smoke.

### 12.10 Module / repo

- New repository (not under `external/`).
- Go module path is the publisher’s choice (`github.com/<org>/whatsbox`).
- Depend on `go.mau.fi/whatsmeow` as a module. A `replace` to a local checkout is a dev convenience, not a product requirement.

---

## 13. v1 method index

| Method | In | Out |
|---|---|---|
| `initialize` | `version`, `store?`, `files?`, `subscribe?`, `verbosity?`, `connect?` | status snapshot (`connect:true` ⇒ `session.connect`) |
| `session.connect` | — | status (`new` ⇒ implicit pair) |
| `session.pair` | — | status (no-op if already linked) |
| `session.disconnect` | — | status |
| `session.logout` | — | status `new` |
| `session.status` | — | `{me?, status, topics}` |
| `subscribe` | `{topics}` | `{topics}` canonical |
| `unsubscribe` | `{topics}` | `{topics}` remaining |
| `directory.list` | `{query?, kind?, limit?, cursor?}` | `{items, cursor?}` |
| `directory.get` | `{id, icon?}` | `DirectoryRow` (+ `participants`, + `icon` per §5.9) |
| `messages.send` | `{to, text?, path?, reply?, react?}` | `{id, topic}` |
| `messages.read` | `{to, ids, by?}` | `{topic}` |

Notifications: `event` only.
