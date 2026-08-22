# WhatsBox

WhatsBox is the **WhatsApp adapter** for Inbox Client Protocol (ICP): native binary `whatsbox`, managed NuGet `WhatsBox`.

**Status:** v0.1 (session-locked)  
**License (product):** MIT  
**Language:** Go  
**Library:** [whatsmeow](https://github.com/tulir/whatsmeow)

`whatsbox` is the native WhatsApp adapter in this Inbox repository: a local linked-device companion that owns one WhatsApp session and exposes it as a JSON-RPC 2.0 pub/sub bus over stdio. The `WhatsBox` NuGet is the managed host on top of that adapter.

Wire, methods, events, files, errors, client-once rules, and capabilities are specified in **INBOX.md**. This document is the WhatsApp profile: how that envelope maps onto WhatsApp Web (LIDs, QR pairing, ContextInfo, HistorySync headers, store layout). It does **not** restate the method table.

`external/whatsmeow` and `external/wacli` in this workspace are **reference only**; the adapter does not share databases, packages, or command surface with wacli.

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
- Receive live `kind: message` / `reaction` / `ack` / `meta` on the chat topic.
- Send `contents[]` (text, one media part + optional caption, react, quote).
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
- Album coalescing; more than one blob part on a send.

---

## 2. WhatsApp profile

| Field | Value |
|---|---|
| `product` | `whatsapp` |
| `identity` | `user` |
| `profile` | omit (same as `identity`) |
| `capabilities.auth` | `["qr"]` |
| `reply` | `"quote"` |
| `react` | `true` |
| `read` | `"message"` |
| `ack` | `true` |
| `files` | `true` |
| `attachments` | `"single"` |

Advertise this object on `initialize` / `session.status` as INBOX.md requires. Never emit `context` on chat events (`reply` is `"quote"`). Outbound `context` is accepted and ignored.

---

## 3. Process and store

### 3.1 Invocation

```text
whatsbox [--store ABSOLUTE_PATH] [--version] [--help]
```

- The process reads JSON-RPC from **stdin** and writes JSON-RPC to **stdout**.
- **stderr** is logs only. Never protocol.
- `--version` / `--help` print and exit (no RPC).
- There is **no default store**. A store path must be provided via `--store` and/or `initialize.store`.
- One process, one store, one WhatsApp session. Two processes on the same store must fail on the store lock (whatsmeow `StreamReplaced` if they both connect).

Framing, protocol version (`"0.1"`), verbosity, and `initialize` store resolution are INBOX.md.

### 3.2 Lifetime

```text
spawn → initialize [connect:true ⇒ implicit session.connect]
      → (else session.connect [+ implicit pair]) → events
stdin EOF → Disconnect WhatsApp → exit
```

- Pairing **keys** remain on disk across process restarts until `session.logout`.
- Live messages missed while the process is down are **gone** (at-most-once). WhatsApp may still deliver a short offline catch-up on the next `connect`; that is applied only to **current** subscriptions.
- “Warm daemon with zero clients” is **not** v1 (that needs a socket). The parent *is* the process.

### 3.3 Store layout

Chosen directory (absolute path), created if missing, mode `0700`:

| Path | Owner | Contents |
|---|---|---|
| `<store>/LOCK` | whatsbox | Exclusive lock. Fail fast if held. |
| `<store>/session.db` | whatsmeow | Device identity, Signal keys, app-state, LID map (whatsmeow’s SQL store). |
| `<store>/whatsbox.db` | whatsbox | Directory only (users, chats, LID↔PN labels). **No messages.** |
| Store files in general | whatsmeow / whatsbox | Sidecars WAL/SHM as SQLite requires. Mode `0600`. |

`files` (blob exchange) is **not** inside the store unless the client points it there. It is a client-owned directory passed at `initialize`.

### 3.4 `session.logout`

1. Unlink the device from WhatsApp if connected.
2. Delete **all whatsmeow session state** under the store (`session.db` and sidecars).
3. Delete **`whatsbox.db`** (directory is account-scoped).
4. Clear subscriptions.
5. Status becomes `new`.

The store **directory** may remain; it is empty of identity.

---

## 4. Identity (WhatsApp)

### 4.1 Canonical topics

| Entity | Canonical topic | Notes |
|---|---|---|
| 1:1 user | LID JID, e.g. `123456789012345@lid` | **Primary key.** |
| Group | `120363…@g.us` | Unchanged. |
| System | `$session`, `$directory` | `$` prefix is reserved. Reject any other `$…` subscribe. |

**Phone-number JIDs** (`15551234567@s.whatsapp.net`) are a **mutable label** (`pn`), like a display name. They are not topics once a LID is known.

### 4.2 Input acceptance

`subscribe` / `unsubscribe` (and `initialize.subscribe`) accept **canonical JIDs only**: LID, group JID (`@g.us`), PN JID (`@s.whatsapp.net`), or `$directory`. Names, handles, and phone numbers are **not** resolved here — the client looks those up with `directory.list` and passes the row’s `topic`. An unmapped PN JID is kept as a temporary topic until a LID mapping arrives (`remap`).

These fields accept **LID, PN JID, or a phone number** (`+15551234567` or digits):

- `messages.send.to`
- `messages.read.to`
- `directory.get` id

The daemon **normalizes** phones and resolves through (in order): local LID map, then WhatsApp `IsOnWhatsApp` when a live connection exists.

- Result / `topic` on the wire is always **canonical** (LID or group JID) once a LID is known.
- Unknown phone on send/read/get → error. **Do not** create a ghost topic.
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

- **Reply, react, and `messages.read`:** `by` is **required** (1:1 and groups). Clients copy it from the inbound event. Use `"me"` when targeting their own message. The daemon **normalizes** `"me"` to the paired LID before WhatsApp `ContextInfo.participant` / reaction keys / group `MarkRead`.
- **`messages.read`:** still requires `by`. In 1:1 the daemon **ignores** it (whatsmeow `MarkRead` has no participant on DMs). In groups it is passed as `participant`. All `ids` in one call MUST share that author in groups.
- **Inbound events:** `by` is `"me"` or a LID. There is no separate `self` field.
- **Status snapshot:** the paired LID is `me` only. Do not also emit `self`.

WhatsApp’s key is `(chat, id, fromMe, participant)`. WhatsApp does **not** resolve quoted bodies server-side; see §6.1 `reply.text`.

---

## 5. Pairing

- Auth is **QR only**. `$session` `{kind:"qr", code}` as WhatsApp rotates codes. Client renders the latest. No per-QR reply.
- Already paired (`offline` or `online`): `session.pair` is a **no-op**. To re-pair the client must `session.logout` first.
- Success: `{kind:"paired"}` then the session is linked. Pairing **ends connected** when invoked standalone; when invoked from `connect`, `connect` finishes `online`.
- Pair-code / phone path: **out of v1**. If WhatsApp demands passkey/WebAuthn: `{kind:"pair_error"}` and the RPC fails.

`deviceName` is the linked-device label shown in WhatsApp → Linked devices. Omitted or blank → `whatsbox on {hostname}`. Applied at pairing; changing it later does not rename an already-linked device.

Presence is **quiet**. Do not `SendPresence(available)`. Typing will not arrive; that is intended.

---

## 6. WhatsApp send / read / attachments

INBOX.md owns the RPC shapes. WhatsApp-specific mapping:

### 6.1 Quote (`reply: "quote"`)

`messages.send.reply` becomes `ContextInfo` on `ExtendedTextMessage` (or media):

- `stanzaID` = `reply.id`
- `participant` = author JID (`"me"` → paired LID) in **1:1 and groups**
- `quotedMessage.conversation` = `reply.text` when provided (always attach the stub, even if empty)
- **Do not** set `remoteJid` on a same-chat quote — WhatsApp then renders `Group • {name}` instead of the bubble

Daemon **never** looks up quote bodies. `reply.text` is optional but required for a visible quote on clients that do not have the original message.

Empty reaction `emoji` removes the reaction (WhatsApp / whatsmeow convention).

### 6.2 `messages.read`

Sends WhatsApp **read** receipts (blue ticks). Not delivery acks (those are protocol-level and always happen).

- `by` required. 1:1: ignored. Groups: `MarkRead` `participant`.
- Never automatic.

### 6.3 Attachments (`attachments: "single"`)

- **Send:** at most one blob part (`image` `video` `audio` `document` `sticker`) plus optional `text` part(s) joined as caption. A second blob part → `unsupported` (`capability: "attachments"`). Whole call fails; do not drop extra parts.
- **Inbound:** one WhatsApp stanza → one `kind: message`. An album is N stanzas → N events. Do not coalesce.
- Caption on inbound media is an extra `text` part on the same event.
- View-once: `kind: message` with `[{type:"unknown", label:"view_once"}]`. **No blob**, no download.
- Polls / buttons / other: `kind: message` with `[{type:"unknown", label}]`. No blob.
- `path` on a blob part without `initialize.files` → `files_required`. Path must resolve under `files` → else `path_escape`.

Unsubscribed chats: **protocol-ack at the WhatsApp layer, then drop**. No event, no download.

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

Chat events still get `topicName` / `byName` / `handle` when known. There is no `pn` on chat events — look up `by` (or the 1:1 `topic`) via `directory.get`.

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

Live-message upserts must not corrupt identity:

- **PushName is the sender’s.** Never write it onto a **group** row (that replaces the subject with the last author) or onto the 1:1 peer on a from-me event.
- **From-me 1:1:** `RecipientAlt` / event `pn` is the **peer**, not the sender. Do not map sender LID → that `pn` (it steals the peer’s phone onto `me`).
- Group rows keep existing `name` / `handle` / `pn` unless a real group source updates them (`GetJoinedGroups`, rename `meta`).

`$directory` vs chat `meta` split is INBOX.md §7.2.

---

## 8. Files

INBOX.md §8 is the contract. WhatsApp notes:

- Inbound media: `{files}/in/{safeTopic}/{id}[.ext]` after download, then the `event`.
- Inbound icon from `directory.get`: `{files}/in/_dir/{safeTopic}[.ext]`.
- View-once and other `unknown` parts are **never** written.
- Unsubscribed inbound media is **never** downloaded.
- Write then notify (no truncated-file race).

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
- Auto-reconnect with backoff until disconnect, logout, `logged_out`, or stdin EOF. Subscriptions persist across reconnects.

---

## 10. Key trade-offs (WhatsApp)

INBOX.md §22 covers protocol-wide choices. WhatsApp-local:

| Choice | Rejected | Why |
|---|---|---|
| Live pub/sub, **no message history** | wacli-style SQLite transcript + FTS | Product is a bus, not a mailbox. History is WhatsApp’s problem and a different binary. |
| Discard unsubscribed (after protocol-ack) | Persist everything, filter in the client | Disk/privacy stay bounded. Decrypt cost is still account-wide (WhatsApp cannot filter). |
| QR only | Pair-code / passkey | Pair-code is headless-nice but out of v1. Passkey is a different UX; fail clearly. |
| LID as topic / directory PK; PN is a label | Canonical phone JID | Agents will see LIDs. PN can change or be missing. `remap` is cheaper than dual topics forever. |
| `subscribe` canonical JIDs only | Phone / name / handle on subscribe | Directory search is `directory.list`. Ambiguous names need a client picker. |
| Phone / PN / LID on send, read, `directory.get` | Phones nowhere | Send-to-number without a directory row remains useful. `IsOnWhatsApp` + lid map. No ghost topics. |
| Optional `reply.text`; daemon never looks up quote bodies | Infer quote from store | v1 stores no message text. WhatsApp does not resolve quotes server-side. |
| No `remoteJid` on same-chat quotes; `participant` is the author LID | Set `remoteJid` to the chat; omit participant in 1:1 | `remoteJid` means “quoted from another chat” (`Group • {name}`). `"me"` is not a JID. |
| Always require `by` on reply/react/read; `"me"` special | Infer from `to`+`id`, omit `by` in 1:1, or parse `@g.us` | WhatsApp keys `(chat, id, fromMe, participant)`. Client already has `by` on the event. 1:1 `MarkRead` ignores `sender`; still require `by` so the client never branches on kind. |
| `attachments: "single"` | Coalesce albums / multi-file send | WhatsApp delivers albums as N stanzas and one send is one id. Extra blob parts `unsupported`. |
| HistorySync harvested for **headers only** | Skip HistorySync entirely, or store bodies | Only conversation headers give the 1:1 thread list at pair time. |
| Quiet presence | Mark available so typing works | A linked device that looks online steals phone notifications. |

---

## 11. Notes for a future implementation plan

Facts gathered from whatsmeow and wacli that an implementer should not rediscover. Not part of the client-visible contract.

### 11.1 whatsmeow session and events

- `Client.Connect` / `ConnectContext` own the websocket. A second process with the same `session.db` emits `events.StreamReplaced`.
- Event types live in `types/events`: `Message`, `Receipt`, `HistorySync`, `ChatPresence`, `Presence`, `QR`, `PairSuccess`, `LoggedOut`, `Connected`, `Disconnected`, `OfflineSyncPreview` / `OfflineSyncCompleted`, group/app-state/call variants (~70 structs).
- `ChatPresence` (typing) is **not sent by WhatsApp** unless `SendPresence(PresenceAvailable)`. v1 never does that.
- `Presence` (online/last-seen) requires `SubscribePresence(jid)` per user. Out of v1.
- Protocol receipts for incoming messages must still be sent or the phone retries (`UndecryptableMessage` storms). “Discard” is application-layer after whatsmeow has handled the frame.
- `MarkRead(ctx, ids, ts, chat, sender)`: `sender` becomes `participant` only when `chat` is **not** a DM (`DefaultUserServer` / `HiddenUserServer` / Messenger). Multiple ids in one call must share the same author. Group read ⇒ our `by`.
- Delivery receipts while not marked available use type `inactive` (not shown as ticks). `SetForceActiveDeliveryReceipts` exists; v1 stays quiet — do not force active receipts unless a later spec says so.

### 11.2 Send / reply / react

- `SendMessage(ctx, to, *waE2E.Message)`.
- `BuildReaction(chat, sender, id, emoji)` and `BuildMessageKey(chat, sender, id)`: `sender` is the **original author**. Empty/`own` ⇒ `FromMe=true`. In groups, non-self author sets `MessageKey.Participant`.
- Empty reaction text removes the reaction (whatsmeow / WhatsApp convention).
- Reply is a `ContextInfo` on `ExtendedTextMessage` (or media): `stanzaID`, `participant` (quoted author JID in **1:1 and groups**; resolve `"me"` to the paired LID), `quotedMessage` stub. **Do not** set `remoteJid` unless quoting a *different* chat. whatsmeow has no `Reply()` helper. wacli uses `--reply-to` + `--reply-to-sender` for the same reason as `by`.
- `RevokeMessage` / `BuildRevoke` is “delete for everyone.” Explicitly out of v1.
- Newsletter reactions use `NewsletterSendReaction`, not `BuildReaction`. Channels are out of v1.

### 11.3 HistorySync

- Pushed by the phone; you do not request `INITIAL_BOOTSTRAP`.
- Types: `INITIAL_BOOTSTRAP`, `INITIAL_STATUS_V3`, `FULL`, `RECENT`, `PUSH_NAME`, `NON_BLOCKING_DATA`, `ON_DEMAND`.
- Default whatsmeow **downloads** blobs and emits `events.HistorySync`. Set `ManualHistorySyncDownload` to choose: ingest bootstrap/recent/push-name/non-blocking; skip `ON_DEMAND` / do not request backfill.
- `DownloadHistorySync` already writes LID maps, push names, NCT salt, and **message secrets** into the **session** store (`storeHistoricalMessageSecrets`). That is `session.db`, not `whatsbox.db`. Harmless and useful for later reactions on live messages.
- After download, whatsmeow `DeleteMedia`s the history blob on the server — keep that path so the phone does not retry forever.
- `Conversation` header fields useful for directory: `ID`, `name` / `displayName`, `archived`, `pinned`, `muteEndTime`, `participant[]`, `lidJID` / `pnJID`, `parentGroupID`, `description`, `createdAt`. **Ignore `messages`.**
- Root extras: `pushnames`, `phoneNumberToLidMappings`, `inlineContacts`.
- wacli `history backfill` / `ON_DEMAND` is the opposite product. Do not call `BuildHistorySyncRequest`.

### 11.4 Directory sources (no server search)

- **No** `SearchContacts` / `GetAllChats` IQ.
- Contacts: `FetchAppState` (`critical_unblock_low` / `regular*` — `IndexContact`, mute, pin, archive) then `Store.Contacts.GetAllContacts()`.
- Groups: `GetJoinedGroups()` (live, complete). Also fills LID pairs + redacted phones. `GetGroupInfo(jid)` for one group.
- Channels: `GetSubscribedNewsletters()` — out of v1.
- Phones: `IsOnWhatsApp(phones)` (usync). Returns `IsIn`, JID (often LID), `PhoneNumber` (PN), and stores LID mappings.
- Hydrate known JIDs: `GetUserInfo(jids)` (avatar id, status, devices, LID).
- 1:1 **thread list** at pair time ≈ HistorySync conversation IDs. Without it, directory is “groups + address book” until someone messages.

### 11.5 LID vs PN in whatsmeow

- Hidden user server is `lid`. Default user server is `s.whatsapp.net`.
- `BuildMessageKey` treats both DM servers as “no participant.”
- wacli spends a lot of code **canonicalizing LID → PN** for a phone-number-shaped store. whatsbox does the **opposite** (LID canonical, PN label). Do not copy wacli’s `canonicalJID` direction blindly; reuse the **mapping tables**, invert the preference.
- Keep `lid_map` in `whatsbox.db` even though whatsmeow also stores mappings — so `directory` and topic match stay consistent if session internals change.

### 11.6 Media

- Incoming `events.Message` carries encrypted media **metadata**; bytes come from `Client.Download` / `DownloadToFile`.
- Download **only** if subscribed **and** `files` is set.
- wacli caps ~100 MiB; reuse a similar cap.
- View-once is a wrapper (`IsViewOnce*`). Map to `kind: message` + `unknown` `label: "view_once"`, do not write files.
- Stickers/voice have format constraints on **send** (WebP 512, OGG/Opus). v1 can require the client to pre-encode; document errors rather than shelling out to ffmpeg in v1 unless cheap.

### 11.7 App-state and wacli lock/delegate

- `FetchAppState(name, fullSync, onlyIfNotSynced)` after connect (and when app-state keys arrive). wacli fetches `regular_high` / `regular_low` so mute/pin/archive/star catch up.
- LTHash mismatch: whatsmeow can request a recovery snapshot. Log and continue; do not store messages.
- wacli store lock + “delegate send to the follow process” exists because **two CLIs cannot share a session**. whatsbox has one process: no delegate IPC. Still take the **lock file** so a leftover wacli/whatsbox cannot double-connect.
- wacli `--events` NDJSON on **stderr** and webhooks are a one-way log, not this protocol. Do not mix.

### 11.8 Pairing

- `GetQRChannel` / `events.QR` with rotating `Codes`. WhatsApp Web shows the first ~60s, then ~20s.
- After scan: `PairSuccess`, then typically a reconnect; wait for `Connected` / our `online` before send.
- `PairPhone` is the pair-code path — **out of v1**.
- `PairPasskeyRequest` / confirmation — **out of v1**; surface `pair_error`.

### 11.9 Suggested implementation slices (not normative)

1. Binary + NDJSON JSON-RPC loop + `initialize` / store lock / `session.status` (`product` + `capabilities`).
2. `session.pair` / `connect` / reconnect / `$session` qr·online·offline.
3. Directory DB + populate (app-state + groups + HistorySync headers) + `$directory` + list/get.
4. LID-first resolution + `remap` + subscribe match.
5. Chat `event`s (`kind: message` + `contents[]`, ack, meta) + discard policy + overflow.
6. `messages.send` `contents[]` + reply/react (`by` / `me` / optional `reply.text` stub).
7. `files` + inbound download + send path + `directory.get` icon + `attachments: "single"`.
8. `messages.read` (`by` always required) + logout wipe.

Test with a fake `Client` (wacli’s `fake_wa` pattern) for protocol tests; live whatsmeow only for a thin pairing/connect smoke.

### 11.10 Module / repo

- New repository (not under `external/`).
- Go module path is the publisher’s choice (`github.com/<org>/whatsbox`).
- Depend on `go.mau.fi/whatsmeow` as a module. A `replace` to a local checkout is a dev convenience, not a product requirement.

