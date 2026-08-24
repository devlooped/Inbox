# Inbox Client Protocol (ICP)

**Status:** v0.1 (draft, session-locked with WhatsBox)  
**Wire version:** `"0.1"`  
**Transport:** JSON-RPC 2.0, NDJSON over stdio  
**Reference profile:** WhatsApp / [`docs/WHATSBOX.md`](WHATSBOX.md)

Inbox Client Protocol (**ICP**) is a **single client-facing JSON-RPC 2.0 pub/sub bus** for local companion processes. One binary owns one messaging-product session and exposes it over stdin/stdout. Clients subscribe to chats and two system topics, send a small set of actions, and receive live events. It is **not** an archive, a search engine, or a product CLI.

A client implements the methods, events, store/files rules, and error tokens in this document **once**. Product differences appear only as:

1. Opaque `topic` / `by` / `context` strings the client copies and never type-parses.
2. `$session` auth event payloads (`qr`, `oauth`, `device_code`, `token_required`).
3. `product` + `identity` + `capabilities` advertised on `initialize` / `session.status`.
4. Directory `kind` values in `user` / `group` (optional additive row fields, never a new RPC).

There are **no** per-product method names (`discord.send`, `slack.postMessage`, `graph.chats`, …). A WhatsBox `0.1` client codec MUST be able to speak this envelope without a second framing dialect: extra JSON fields are additive and MUST be ignored by clients that do not understand them.

Suggested binaries (not normative): `whatsbox`, `pubsubbox`, `discordbox`, `slackbox`, `teamsbox`, `telegrambox`, `matrixbox`.

---

## 1. Product

### 1.1 What it is

A locked, same-machine companion that is:

1. An **address book** (directory of users and chats, no transcripts).
2. A **live pub/sub** of chats the client asked for.
3. A **same-machine blob channel** (paths on disk, never bytes on the RPC).

One binary. One process. One store. One product session. One files directory.

### 1.2 Who it is for

Agents and local apps that can spawn a process, speak newline-delimited JSON-RPC, and optionally share a directory for files. Not humans typing commands (QR / OAuth / device-code / token-file rendering is the client’s job).

### 1.3 v1 does

- Authenticate via the product’s official path, surfaced as `$session` events a single client can render.
- Connect, auto-reconnect, disconnect, logout.
- Directory populate + list/get + live `$directory` updates.
- When advertised (`capabilities.membership`), find / join / leave / create chats — product membership, **not** subscribe.
- Subscribe/unsubscribe to chats by **canonical topic** only.
- Receive live messages and in-chat `meta` on the chat topic; receipts (`ack`) when the product provides them.
- Send `contents[]` (text, file if `files` is set, reply, react) — as far as advertised capabilities allow.
- Explicit mark-read (`messages.read`). Never automatic.

### 1.4 v1 does not

- Message history, message search, backfill, export, FTS (`directory.find` is live product lookup of chats, not a transcript search).
- Store message bodies or last-message previews.
- Typing indicators or “available” presence (quiet companion).
- Edit, revoke (“delete for everyone”).
- MCP / ACP / A2A as the native protocol.
- Unix/TCP socket mux (stdio only).
- Named multi-account in one process.
- Topic wildcards (`#`, `+`, `$all`).
- Default store path.
- Per-product RPC methods to paper over gaps. Missing features are **capabilities** (and documented degraded behavior or a stable error token), not extra verbs. `directory.find` / `join` / `leave` / `create` exist on the method table; products that cannot honor them advertise `membership: "none"` and return `unsupported`.

---

## 2. Process and store

### 2.1 Invocation

```text
{bin} [--store ABSOLUTE_PATH] [--version] [--help]
```

- The process reads JSON-RPC from **stdin** and writes JSON-RPC to **stdout**.
- **stderr** is logs only. Never protocol. Clients MUST NOT treat stderr as protocol or as “the command failed.”
- `--version` / `--help` print and exit (no RPC).
- There is **no default store**. A store path MUST be provided via `--store` and/or `initialize.store` (see §6.1).
- One process, one store, one product session. Two processes on the same store MUST fail on the store lock (`store_locked`).

### 2.2 Lifetime

```text
spawn → initialize [connect:true ⇒ implicit session.connect]
      → (else session.connect [+ implicit pair]) → events
stdin EOF → Disconnect product → exit
```

- Pairing **keys / tokens** remain on disk across process restarts until `session.logout`.
- Live messages missed while the process is down are **gone** (at-most-once), unless a product socket or a hosted ingress (§2.5) delivers a short **offline catch-up** on the next `connect`. Catch-up is applied only to **current** subscriptions. It is not a history API.
- “Warm daemon with zero clients” is **not** v1. The parent *is* the process. A hosted webhook subscription MAY outlive the process; that does not make the daemon a background service.

### 2.3 Store layout

Chosen directory (absolute path), created if missing, mode `0700`:

| Path | Owner | Contents |
|---|---|---|
| `<store>/LOCK` | process | Exclusive lock. Fail fast if held. |
| `<store>/credentials` or product session files | process | Tokens, device keys, refresh tokens, homeserver URL. Product-defined filenames; see profiles. Optional **ingress** fields (hub URL, connection string, hosted `notificationUrl`) — §2.5. |
| `<store>/directory.db` (name may vary) | process | Directory only (users, chats, labels). **No messages.** |
| Store files in general | process | Sidecars WAL/SHM as SQLite requires. Mode `0600`. |

`files` (blob exchange) is **not** inside the store unless the client points it there. It is a **client-owned** directory passed at `initialize`.

### 2.4 `session.logout`

1. Unlink / revoke remotely if the product supports it and a live connection exists.
2. Delete **all session identity** under the store (tokens, keys, device records).
3. Delete the **directory** database (it is account-scoped).
4. Clear subscriptions.
5. Status becomes `new`.

The store **directory** may remain; it is empty of identity.

### 2.5 Hosted ingress (optional; not a client RPC)

Some products cannot push into a purely local stdio process. Microsoft Graph change notifications require a public HTTPS `notificationUrl`; Slack’s HTTP Events API and Telegram `setWebhook` likewise. A bridge MAY pair with a **hosted webhook** that:

1. Receives native callbacks (URL validation and signing-secret checks happen **on the host**).
2. Forwards **native** payloads onto a same-operator bus the local process consumes (Azure Web PubSub, Service Bus, a SignalR hub, or equivalent).
3. The local process maps those payloads to Box `event`s, filters by the current subscribe set, and writes blobs under `files`.

From the JSON-RPC client this is indistinguishable from WhatsApp: `event` notifications on stdout. There is **no** `webhook.register` method, no Azure / SignalR / Service Bus nouns on the wire, and no second framing dialect.

| Concern | Rule |
|---|---|
| Config | Store-only (`credentials.json` / `ingress.json`): hub URL, connection string, subscription name, hosted `notificationUrl`. Missing file → use the product’s local live path, or Graph delta-poll as an implementer fallback. |
| Mapping | **Stays in the bridge.** The host MUST NOT emit Box `event` objects (that would be a second codec). Envelope on the bus: routing key (store/session id) + raw native JSON. |
| Send vs receive | Send remains local REST/SDK. Receive MAY be a different hop (Slack already is: Socket Mode in, Web API out). |
| `online` | Able to **send and receive**. Ingress drop while REST send still works → `$session` `offline` and reconnect the bus. |
| stdin EOF | Tears down the local process (and its bus connection). The hosted Graph/Slack/Telegram subscription MAY remain. |
| Catch-up | Bus backlog while the process was down is **offline catch-up** (§7.4): current subscriptions only; at-most-once after the bridge acks the bus message. Prefer pub/sub (drop if no consumer) for WhatsApp-identical loss. |
| Files | Ingress carries metadata. The **local** process downloads into `files`. Bytes do not ride JSON-RPC. |
| Not universal | WhatsApp and Discord **keep** their local sockets. Ingress is the missing hop for **Teams**, and an optional alternative for Slack HTTP Events API and Telegram `setWebhook`. Matrix has no native webhook; a host MAY `/sync` and fan-in — optional, not required. |

Clients MUST NOT branch on how the daemon obtained an event.

---

## 3. Transport

### 3.1 Framing (MUST)

- JSON-RPC 2.0.
- UTF-8.
- **One JSON object per line** (NDJSON). Messages **MUST NOT** contain raw newlines (compact `json.Marshal` / equivalent).
- JSON-RPC **batch arrays MUST NOT** be used. A line that is a JSON array is a parse error.
- stdout: only valid JSON-RPC request/response/notification objects.
- stderr: logs. Never protocol.

A WhatsBox `0.1` codec already speaks this dialect. Other products MUST NOT invent a second framing (length-prefix, LSP headers, MCP, protobuf-on-the-wire).

### 3.2 Envelope

**Request** (named `params` object; omit `params` when empty)

```json
{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"version":"0.1"}}
```

**Result**

```json
{"jsonrpc":"2.0","id":"1","result":{}}
```

**Error** — application errors in `-32000`…`-32099` with a stable `message` token

```json
{"jsonrpc":"2.0","id":"1","error":{"code":-32001,"message":"store_required"}}
```

**Notification** (server → client only in v1):

```json
{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@..."}}
```

- All live traffic uses **one method name: `event`**.
- `params.topic` is always present.
- `params.kind` discriminates the payload.
- Client → server notifications are not used in v1. Cancellation of in-flight RPCs is not required in v1.

### 3.3 Protocol version

`initialize.params.version` is the client’s protocol version. v1 value: **`"0.1"`**.

If the daemon does not support it, return `unsupported_version` with the versions it does support in `error.data`. Do not speak events before a successful `initialize`.

### 3.4 Verbosity

`initialize.params.verbosity` controls stderr only. Recommended values: `error` | `warn` | `info` (default) | `debug`. Until `initialize`, the process MAY log at `warn`.

---

## 4. Identity

### 4.1 Canonical topics

Topics, `by`, and `context` are **opaque strings**. The client copies them from directory rows and events. It MUST NOT parse product type suffixes to decide protocol behavior (use directory `kind` and advertised capabilities instead).

| Entity | Canonical topic | Notes |
|---|---|---|
| 1:1 / DM | product-defined opaque id | Directory `kind: "user"`. |
| Group / channel / room | product-defined opaque id | Directory `kind: "group"`. |
| System | `$session`, `$directory` | `$` prefix is reserved. Reject any other `$…` subscribe. |

WhatsApp (reference): 1:1 is a LID JID (`123@lid`); groups are `120363…@g.us`; phone-number JIDs are a **mutable label** (`pn`), not a topic once a LID is known.

### 4.2 Input acceptance

`subscribe` / `unsubscribe` (and `initialize.subscribe`) accept **canonical topics only**: a directory row’s `topic`, or `$directory`. Names, handles, phones, Slack `#channel` aliases, Discord snowflake-as-mention, Matrix aliases (`#room:server`) are **not** resolved here — the client looks those up with `directory.list` (roster) or `directory.find` (live product lookup when `membership` is `join` or `create`) and passes the row’s `topic`. Unknown / unresolvable entries fail the **whole** call (`invalid_topic`; no partial apply).

When `capabilities.membership` is `"join"` or `"create"`, subscribe of a chat topic that is **not** on the roster is `not_found` (whole call; no partial apply). `membership: "none"` keeps today’s rule: a canonical topic may be subscribed without a roster row (WhatsApp JID). `$directory` is never a membership check.

These fields MAY accept a product-defined **alias** that the daemon normalizes to a canonical topic (WhatsApp: LID, PN JID, or phone; others: see profiles):

- `messages.send.to`
- `messages.read.to`
- `directory.get` id

Rules:

- Result / `topic` on the wire is always **canonical** once known.
- Unknown alias on send/read/get → `not_found`. **Do not** create a ghost topic.
- Groups cannot be addressed by a 1:1-only alias (WhatsApp: groups cannot be addressed by phone).

### 4.3 Remap

When a product later replaces a temporary topic with a stable one (WhatsApp LID↔PN; Matrix alias → room id; Slack IM opened by user id → `D…` channel):

1. Upsert `$directory` with the new key.
2. If a subscription was held on the old topic, **move** it to the new one.
3. Emit `$session` event `{kind:"remap", from, to}`.
4. Further chat `event`s use the new `topic`.

Products that never remap MAY omit `remap`. Clients MUST still handle it.

### 4.4 `by` and `me`

`by` is the **author of the original message** (not the logged-in user sending the RPC).

| Value | Meaning |
|---|---|
| an opaque topic-shaped id | That user. |
| the string `"me"` | The paired / logged-in account. |

- **Reply, react, and `messages.read`:** `by` is **required** (1:1 and groups). Clients copy it from the inbound event. Use `"me"` when targeting their own message. The daemon normalizes `"me"` to the account’s canonical id before talking to the product. Products that do not need an author id (WhatsApp 1:1 mark-read, Slack/Teams cursor read, …) still receive `by` and MUST ignore it rather than invent a second RPC shape. All `ids` in one `messages.read` MUST share that author when the product’s read API is per-author (WhatsApp groups).
- **Inbound events:** `by` is `"me"` or an opaque author id. There is no separate `self` field.
- **Status snapshot:** the paired id is `me` only. Do not also emit `self`.

v1 stores **no** message bodies. The client that displayed a line is the only place that has quote text; see §6.14 `reply.text`.

### 4.5 `context` (optional grouping key)

`context` is an optional opaque string on chat **events** and on `messages.send`. It is **not** a topic. Subscribe remains the chat/channel/room. Quotes stay on `reply` (`id` + `by` + `text?`) — a pointer, not a bucket.

| Who | Rule |
|---|---|
| Inbound | If `context` is present, events that share that value are one **group**. The root is the message whose `id` equals `context` (when that event is in the stream). Omit `context` → main stream (flat). |
| Outbound | Optional on `messages.send`. When `capabilities.reply` is `"context"` and the user replies to event `e`, send `context: e.context ?? e.id` **and** the usual `reply: {id, by, text?}`. |
| Display | Group by `context` if any event on the topic has it. Otherwise a flat list (WhatsApp). Do not parse the string. |
| `reply: "quote"` daemons | MUST ignore outbound `context` if sent (additive; WhatsBox `0.1` codecs may omit it). MUST NOT emit `context` unless the product actually has a grouping key. |
| Discord threads | Separate **topics** (`kind: group`), never `context` on the parent channel. |

One Reply action. There is **no** “start a new context” verb in v1: Slack cannot fork a thread from a child. Replying to a child **copies** `e.context` (the root), which is how the client avoids using the child id as `thread_ts` without a daemon history cache.

Products MAY emit `context` even when `capabilities.reply` is `"quote"` (Matrix `m.thread`, Teams channel reply lists). Grouping is data-driven. The `"context"` value only tells the UI that the **first** reply on a message with no `context` yet will **create** a group, not a quote. A stricter always-send variant (zero send-path branch) is in §22.7.

### 4.6 Me binding

`SessionSnapshot.me` / `$session` `paired.me` is the paired **product** identity. How it is bound is `capabilities.me`:

| Value | Who chooses `me` | `me` on `initialize` / `session.pair` |
|---|---|---|
| `"issued"` | The product, after auth (WhatsApp LID, Discord bot snowflake, Graph oid) | **Must omit.** Present → `invalid_params`. |
| `"claimed"` | The client | **Required** on the pairing call. Omit → error token `me_required` (not a `$session` kind). |

`me` on `initialize` is remembered even when `connect` is false and applied to the following `session.pair` / `session.connect`. If initialize and pair both set `me` and they differ → `invalid_params`. Claimed `me` is pair **input**, not pair progress: there is no `me_required` event and no file-watch inject. Device-code / QR wait still happens **inside** `pair({me})` after the name is known.

`deviceName` stays the companion label. It is not `me`.

---

## 5. Product advertisement and capabilities

### 5.1 Where it lives

`initialize` and `session.status` results MUST include:

| Field | Type | Description |
|---|---|---|
| `product` | string | `whatsapp` \| `webpubsub` \| `discord` \| `slack` \| `teams` \| `telegram` \| `matrix` |
| `identity` | string | `user` \| `bot` |
| `profile` | string? | Product sub-profile when one binary could be either (Telegram: `bot` \| `user`). Omit when identical to `identity`, **or** when the product’s default surface is implied (Web PubSub: omit ≡ Chat hub; a later `profile: "hub"` would be the base hub). |
| `capabilities` | object | See §5.2. |

A WhatsBox `0.1` codec that does not bind these fields still works: they are additive. A unifying client MUST read them to disable UI / skip RPCs that the product cannot honor.

### 5.2 `capabilities` object

Every key below is required on the wire so a client never has to guess.

| Key | Type | Meaning |
|---|---|---|
| `auth` | string[] | How `session.pair` / implicit pair authenticates. Subset of `qr`, `oauth`, `device_code`, `token`. |
| `me` | `"issued"` \| `"claimed"` | How session `me` is bound. See §4.6. |
| `membership` | `"none"` \| `"join"` \| `"create"` | Product-side add/remove of this `me` from a chat. Total order: `create` ⊃ `join` ⊃ roster. See below. |
| `reply` | `"quote"` \| `"context"` \| `"none"` | String enum. JSON `true` / `false` are **not** members and **not** aliases of `"quote"` / `"none"`. `"quote"` = in-chat quote (WhatsApp / Discord / Telegram / Matrix). `"context"` = grouping key (Slack threads): send still uses `reply` plus optional `context` (§4.5); not a quote bubble. `"none"` = `messages.send` with `reply` → `unsupported`. |
| `react` | boolean | Two-state. A `{type:"reaction"}` content part on `messages.send` works. Off is JSON `false`, not `"none"`. |
| `read` | `"message"` \| `"cursor"` \| `"conversation"` \| `"none"` | String enum. JSON `true` / `false` are **not** members and **not** aliases of `"none"`. See §6.15. `"none"` = `unsupported`. |
| `ack` | boolean | Chat `kind: ack` events (`delivered` / `read` / `played`) will be emitted when the product has them. |
| `files` | boolean | Product can move blobs through `initialize.files`. Still requires the client to pass `files`. |
| `attachments` | `"none"` \| `"single"` \| `"many"` | How many **blob parts** (`image` `video` `audio` `document` `sticker`) one `kind: message` may carry. String enum; JSON `false` is **not** `"none"`. |

There is **no** `live` capability (and therefore no `"poll"` value). JSON-RPC inbound is always `event` notifications. How the daemon obtains them (product WS, long-poll, hosted ingress §2.5, or local poll fallback) is **not** client-visible. Implementers MUST NOT advertise a poll mode, invent webhook RPCs, or put `"poll"` on the wire.

There is **no** `e2ee` capability. WhatsApp Signal is inside the library and is not a client branch. Matrix Olm/Megolm and Telegram secret chats are out of v1: inbound ciphertext is `kind: message` with a content part `{type:"unknown", label:"encrypted"}` / `"secret"` (no blob); send/read/react targeting that topic → `unsupported` **without** `error.data.capability` (that field names advertised keys only). Directory still lists the room. A process-wide boolean would be the wrong grain — Matrix has both encrypted and plaintext rooms — and would not let a client hide send correctly. A future spec may add `e2ee` when a process can actually decrypt.

`attachments` values:

| Value | `messages.send` blob parts |
|---|---|
| `"none"` | Any blob part → `unsupported` (`capability: "attachments"`). Text-only transports (plain SMS). |
| `"single"` | At most one blob part. A second → `unsupported` (whole call; no silent drop). Optional `text` part is the caption. |
| `"many"` | N blob parts on one Box message. Result is still one `{id, topic}`. If the product would fan out to N native ids, advertise `"single"` instead of splitting. |

`files` is blob plumbing (media + icons). `attachments` is cardinality of media **parts**. Missing `initialize.files` is still `files_required` even when `attachments` is `"many"`. Profiles with `attachments: "none"` set `files: false`.

`me` values: see §4.6.

`membership` values:

| Value | RPCs |
|---|---|
| `"none"` | `directory.find` / `join` / `leave` / `create` → `unsupported` (`capability: "membership"`). Join happens in the product’s own UI (WhatsApp phone, Discord invite). |
| `"join"` | `directory.find`, `directory.join`, `directory.leave`. `directory.create` → `unsupported`. |
| `"create"` | `"join"` plus `directory.create`. |

Find / join / leave / create are **online-only** (`disconnected` when not `online`). Roster `list` / `get` remain valid offline. Join writes the roster and does **not** subscribe. Leave ends product membership, `$directory` `remove`s the row, and drops a held subscription. `unsubscribe` never leaves the product.

`read` values:

| Value | `messages.read` behavior |
|---|---|
| `"message"` | WhatsApp-style per-id receipts (blue ticks). |
| `"cursor"` | Slack `conversations.mark`: conversation read cursor to the latest of `ids`. Not a per-message blue tick. RPC **succeeds**. No `ack` events from this call. |
| `"conversation"` | Teams `markChatReadForUser`: whole chat marked read. RPC **succeeds**. Extra `ids` / `by` ignored. |
| `"none"` | RPC **fails** with `unsupported` and `error.data.capability = "read"`. Never a silent no-op. |

`reply` values:

| Value | `messages.send` `reply` behavior |
|---|---|
| `"quote"` | In-chat quote. Outbound `context` ignored. |
| `"context"` | RPC **succeeds**. The daemon posts into the grouping key `context` if provided, else `reply.id` (start a group rooted at that message). `reply.text` MAY be ignored. Not a quote bubble — the client SHOULD group by `context` and MUST NOT draw a WhatsApp-style citation. Documented lossy mapping vs quotes, not `unsupported`. |
| `"none"` | `messages.send` with `reply` → `unsupported` with `error.data.capability = "reply"`. |

A daemon MUST advertise only the members in the tables above. In particular it MUST NOT put JSON `true` or `false` on `reply`, `read`, `attachments`, `me`, or `membership` — those are not synonyms of `"none"`. Two-state keys (`react`, `ack`, `files`) stay JSON booleans.

A client that receives a boolean or unknown string on `reply` / `read` / `attachments` / `me` / `membership` MAY hide the action (same UI as `"none"` / issued) so a non-conformant daemon does not crash it. That is fail-closed parsing, not a second legal value: the client MUST NOT echo the boolean and MUST NOT document it as valid.

When the advertised capability does not allow a send/read field:

| Advertised | Client set | Result |
|---|---|---|
| `react: false` | a `reaction` content part | `unsupported` (`capability: "react"`). Whole call fails. |
| `reply: "none"` | `reply` | `unsupported` (`capability: "reply"`). Whole call fails. |
| `read: "none"` | `messages.read` | `unsupported` (`capability: "read"`). |
| `attachments: "none"` | any blob part | `unsupported` (`capability: "attachments"`). |
| `attachments: "single"` | two or more blob parts | `unsupported` (`capability: "attachments"`). |
| `membership: "none"` | `directory.find` / `join` / `leave` / `create` | `unsupported` (`capability: "membership"`). |
| `membership: "join"` | `directory.create` | `unsupported` (`capability: "membership"`). |

Do not silently drop a content part the client set.

### 5.3 Example status snapshot

```json
{"version":"0.1","status":"online","me":"123456789012345678","product":"discord","identity":"bot","topics":["$session","$directory"],"capabilities":{"auth":["token"],"me":"issued","membership":"none","reply":"quote","react":true,"read":"none","ack":false,"files":true,"attachments":"many"}}
```

---

## 6. RPC methods

Only these methods exist in v1. This is the WhatsBox v1 noun set. Products MUST NOT replace them with MCP/REST/product-native names.

### 6.1 `initialize`

**MUST be the first RPC.** Second `initialize` → `already_initialized`.

```json
{
  "version": "0.1",
  "store": "D:\\data\\box",
  "files": "D:\\data\\box-files",
  "subscribe": ["$directory"],
  "verbosity": "info",
  "connect": true,
  "deviceName": "box on DESKTOP-ADA",
  "me": "alice"
}
```

| Field | Required | Description |
|---|---|---|
| `version` | yes | Protocol version (`"0.1"`). |
| `store` | if `--store` omitted | Absolute store path. |
| `files` | no | Absolute blob directory. Missing → text-only (no inbound media download, no icons, `path` on send errors). |
| `subscribe` | no | Initial topics; applied **before** any event is eligible for dispatch. `$session` is implicit and need not be listed. |
| `verbosity` | no | stderr level. |
| `connect` | no | If `true`, implicit `session.connect` after subscriptions are installed. Default `false`. |
| `deviceName` | no | Display name for this companion (WhatsApp linked-device name; Matrix `device_display_name`; others MAY ignore). Omitted or blank → `{bin} on {hostname}`. |
| `me` | if `capabilities.me` is `"claimed"` and this call (or a later pair) will pair | Claimed product identity. See §4.6. Issued products MUST omit. |

**Store resolution**

| `--store` | `initialize.store` | Result |
|---|---|---|
| set | omitted | use `--store` |
| omitted | set | use `initialize.store` |
| set | set, same absolute path | ok |
| set | set, different | `store_mismatch` |
| omitted | omitted | `store_required` |

Create the store directory if missing (`0700`).

Apply `subscribe` (plus implicit `$session`) **before** any event is eligible for dispatch, and **before** `connect:true` runs.

- `connect: false` or omitted: do **not** open the product socket. Result is a status snapshot (`new` or `offline`).
- `connect: true`: then run **`session.connect`** as part of this RPC. The call lasts as long as `session.connect` would (including waiting for QR / OAuth / device code / token file).

**Result** (already paired, `connect` omitted)

```json
{"version":"0.1","status":"offline","me":"111@lid","product":"whatsapp","identity":"user","topics":["$session","$directory"],"capabilities":{"auth":["qr"],"me":"issued","membership":"none","reply":"quote","react":true,"read":"message","ack":true,"files":true,"attachments":"single"}}
```

If never paired and `connect` is not true: `"status":"new"`, snapshot `me` omitted (claimed `params.me` is stored for the later pair, not echoed as paired identity). `product` / `capabilities` are still present so the client can render the right auth UI and, when `capabilities.me` is `"claimed"`, collect `me` before `connect` / `pair`.

### 6.2 `session.connect`

Bring up the product connection. `initialize` with `connect: true` is this method after a successful init.

- If status is `new`: **implicit `session.pair`**, then connect.
- If `offline`: connect with existing keys/tokens.
- If `online`: no-op success.
- Connect **send** (product REST/SDK or native socket) **and receive** (product socket and/or hosted ingress §2.5). `online` means both.
- On success, start auto-reconnect with backoff until disconnect, logout, `logged_out`, or stdin EOF. Ingress drop is a receive-path drop: emit `offline` and reconnect the bus even if REST send would still work.
- Subscriptions persist across reconnects (they are client intent, not connection state).
- After `online`, start **async directory populate** (§8.3). Do not block the connect result on populate.

**Result:** same shape as `session.status`.

### 6.3 `session.pair`

Start the product’s auth flow and wait until linked (or fail).

```json
{"me": "alice"}
```

- `me`: see §4.6. Claimed: required here unless `initialize` already supplied it. Issued: omit.
- Already paired (`offline` or `online`): **no-op**, return current status. To re-pair the client MUST `session.logout` first.
- Auth progress is `$session` events (see §7.1). Client renders the latest. No per-event reply.
- Success: `{kind:"paired"}` then the session is linked. Pairing **ends connected** when invoked standalone; when invoked from `connect`, `connect` finishes `online`.
- Failure: `{kind:"pair_error", message}` and the RPC fails with `pair_error`. Claimed pair without `me` fails immediately with `me_required` (no `$session` event, no device-code wait).

### 6.4 `session.disconnect`

Drop the product connection. Process stays. Status `offline` if still paired, else `new`. Further RPCs are valid (`connect` again, `logout`, directory reads from cache).

### 6.5 `session.logout`

See §2.4. Result is `session.status` with `status: "new"`.

### 6.6 `session.status`

```json
{
  "me": "111@lid",
  "status": "online",
  "product": "whatsapp",
  "identity": "user",
  "topics": ["$session", "$directory"],
  "capabilities": {"auth":["qr"],"me":"issued","membership":"none","reply":"quote","react":true,"read":"message","ack":true,"files":true,"attachments":"single"}
}
```

| `status` | Meaning | `me` |
|---|---|---|
| `new` | No session in the store | omitted |
| `offline` | Keys/tokens exist, connection down | your id |
| `online` | Connection up | your id |

`topics` is the current subscription set (always includes `$session`).

### 6.7 `subscribe` / `unsubscribe`

```json
{"topics":["$directory","opaque-topic-1"]}
```

**Result:** `{topics:[…]}` canonical topics actually applied.

- Display name / roster are **not** on this result. Call `directory.get` if needed.
- Names, handles, phones, aliases are `invalid_topic`. Resolve with `directory.list` (or `directory.find` when membership allows).
- Unknown entries fail the whole call (no partial apply).
- When `membership` is `"join"` or `"create"`, a chat topic with no roster row is `not_found` (whole call).
- `$session` cannot be unsubscribed.
- Subscribing an already-subscribed topic is a no-op.
- Newly subscribed chats get **no replay**; next live event onward. `directory.join` does **not** subscribe.

### 6.8 `directory.list`

```json
{"query":"ada","kind":"user","limit":50,"cursor":""}
```

| Field | Description |
|---|---|
| `query` | Optional. Matches name, `pn`, `handle`, and topic string. |
| `kind` | Optional. `user` \| `group`. |
| `limit` | Optional. Implementation default (e.g. 50), max 100. |
| `cursor` | Opaque; omit or `""` for the first page. |

**Result:** `{items: [DirectoryRow], cursor?}`. No `cursor` (or empty) means last page.

There is **no** sort-by-last-message (we do not store it). Order: implementation-defined but stable (recommend name, then topic). This RPC searches the **roster**. Live product lookup of chats this `me` could join is `directory.find`. Never search on `subscribe`.

### 6.9 `directory.get`

```json
{"id":"opaque-or-alias","icon":false}
```

| Field | Description |
|---|---|
| `id` | Canonical topic or product alias. Required. |
| `icon` | Optional. When omitted, defaults to **whether `initialize.files` was set**. |

**Result:** one `DirectoryRow`. Groups include `participants` (canonical topics + names / `pn` / `handle` if known).

| `icon` | `files` set | Behavior |
|---|---|---|
| omitted | yes | Fetch preview icon (write under `files`, set `icon` on the result). |
| omitted | no | Do not fetch; omit `icon`. |
| `true` | yes | Fetch. |
| `true` | no | `files_required`. |
| `false` | either | Never fetch; omit `icon`. |

`list` / `$directory` upsert never carry `icon`. Missing entity → `not_found`.

### 6.10 `directory.find`

Live product lookup of chats this `me` **could** join. Does **not** write the roster. `membership: "none"` → `unsupported`. Not `online` → `disconnected`.

Params: same object as `directory.list` (`query?`, `kind?`, `limit?`, `cursor?`). Empty `query` = first page of what the product will show.

**Result:** `{items: [DirectoryRow], cursor?}` — same page shape as list. Rows include canonical `topic` and `kind`. A `kind: "user"` row is a person this `me` may open a 1:1 with; it is **not** yet a roster chat. Subscribe of a find hit that was not joined → `not_found`.

### 6.11 `directory.join`

```json
{"id":"opaque-canonical-topic"}
```

Add this `me` to an existing chat (or open a 1:1 from a `kind: "user"` find topic). Canonical topic only (from find, create, or the roster). Names / aliases → `invalid_topic`.

- `membership: "none"` → `unsupported`. Not `online` → `disconnected`.
- Already a member → no-op `{topic}`.
- A `kind: "user"` topic MAY remap to a group/1:1 chat topic: upsert the new row, `$session` `{kind:"remap", from, to}`, result `{topic}` is the **canonical chat** after remap. Further send/subscribe use `to`.
- Writes the roster. Does **not** subscribe.
- **Result:** `{topic}`.

### 6.12 `directory.leave`

```json
{"id":"opaque-canonical-topic"}
```

End product membership. Canonical topic only.

- `membership: "none"` → `unsupported`. Not `online` → `disconnected`.
- Not a member → `not_found`.
- On success: product leave, `$directory` `remove`, drop a held subscription if any. `unsubscribe` never does this.
- **Result:** `{topic}` of the chat left (canonical).

### 6.13 `directory.create`

```json
{"name":"Project Falcon","topic":"falcon"}
```

Create a **group**. `name` required. `topic` optional (claimed id); omit → the product assigns one.

- `membership` other than `"create"` → `unsupported`. Not `online` → `disconnected`.
- Always `kind: "group"`. 1:1 is `directory.join` of a user topic from find, not create.
- `$` prefix / garbage `topic` → `invalid_topic`.
- Same `me` already has this topic → no-op `{topic}`.
- Another occupant holds that topic → `topic_taken`.
- Writes the roster. Does **not** subscribe. No participant list (invitees join themselves).
- **Result:** `{topic}`.

### 6.14 `messages.send`

```json
{
  "to": "opaque-or-alias",
  "reply": {"id": "3EB0…", "by": "999@lid", "text": "original body"},
  "context": "1710000000.000001",
  "contents": [
    {"type": "text", "text": "hello"},
    {"type": "image", "path": "out/photo.jpg"}
  ]
}
```

React-only (no body, no `reply` / `context`):

```json
{
  "to": "opaque-or-alias",
  "contents": [{"type": "reaction", "target": "3EB0…", "by": "me", "emoji": "👍"}]
}
```

| Field | Description |
|---|---|
| `to` | Chat. Required. |
| `reply` | Quote target (and, when `capabilities.reply` is `"context"`, the message the user clicked). `id` + `by` required. Optional `text` is the quoted body. Without it, clients that do not have the original message show no quote bubble (products that support quotes). Illegal on a reaction-only send. |
| `context` | Optional opaque grouping key (§4.5). When `capabilities.reply` is `"context"`: `e.context ?? e.id` from the event being replied to. Illegal on a reaction-only send. |
| `contents` | Non-empty array of parts (§7.3). |

Rules:

- `contents` is required and MUST be non-empty.
- **Message send:** one or more of `text` / `image` / `video` / `audio` / `document` / `sticker` / `location` / `unknown`. No `reaction` part. Blob-part count follows `capabilities.attachments`. A `path` on a blob part without `files` → `files_required`. `path` must resolve under `files` → else `path_escape`.
- **React send:** exactly one `{type:"reaction", target, by, emoji}`. `emoji` empty string = remove. `target` + `by` required (`by` is the author of the reacted-to message). No mix with body parts. No `reply` / `context`.
- Mix of reaction + body → `invalid_params`.
- Capability failures: see §5.2 (`unsupported`, never a silent no-op; never drop extra blob parts).
- **Result:** `{id, topic}` where `topic` is the **canonical** chat id after normalization. No timestamp. One Box send, one id — never fan-out to N native messages.
- If the client is subscribed to that topic, a normal inbound-shaped `event` is also emitted with `by: "me"`. React echo is `kind: reaction`. If not subscribed, the RPC result is the only acknowledgement.

**Reply mapping (lossy cases are advertised, not hidden):**

| `capabilities.reply` | Native action |
|---|---|
| `"quote"` | In-chat quote / `message_reference` / `reply_parameters` / `m.in_reply_to`. Outbound `context` ignored. |
| `"context"` | Grouping key: `thread_ts` / channel-reply root / `m.thread` = `params.context` if set, else `reply.id`. Still require `reply.{id,by}`. Not a quote bubble. `reply.text` MAY be ignored. |
| `"none"` | `unsupported`. |

Daemon **never** looks up quote bodies. `reply.text` is optional but required for a visible quote on clients that do not have history (every Box consumer).

### 6.15 `messages.read`

```json
{"to":"opaque-group","ids":["3EB0…","3EB1…"],"by":"999@lid"}
```

- `to` = chat. Required.
- `ids` = message ids. Required, non-empty.
- `by` = author. **Required.** Copy from the inbound event. WhatsApp 1:1 ignores it (MarkRead has no participant on DMs). Groups pass it through (per-author receipts). Cursor / conversation products ignore it. `"none"` still errors `unsupported` before `by` matters.
- **Result:** `{topic}` (canonical).
- Never automatic.
- Behavior follows `capabilities.read` (§5.2). `"none"` → `unsupported`. `"cursor"` / `"conversation"` succeed as degraded mark-read, not WhatsApp blue ticks.

---

## 7. Events (`method: "event"`)

Every notification:

```json
{"jsonrpc":"2.0","method":"event","params":{"topic":"…","kind":"…"}}
```

### 7.1 `$session` (always on)

| `kind` | Payload | When | Client renders |
|---|---|---|---|
| `qr` | `code` (string) | Pairing (WhatsApp; optional Telegram QR login) | QR image of `code` |
| `oauth` | `url`, `state?` | OAuth authorization | Open / show `url` |
| `device_code` | `user_code`, `verification_uri`, `expires_in?`, `interval?` | OAuth device grant | Show `user_code` + `verification_uri` |
| `token_required` | `path`, `hint` | Token-in-store | Write credentials to `{store}/{path}`; `hint` is a short instruction |
| `paired` | `me` | Auth success | Linked |
| `pair_error` | `message` | Auth failed | Error |
| `online` | `me` | Connection up (including after reconnect) | |
| `offline` | `reason?` | Connection down; will reconnect unless disconnect/logout/EOF | |
| `logged_out` | `reason?` | Session revoked remotely; status becomes `new` after local cleanup | |
| `remap` | `from`, `to` | Subscription/topic moved | Retarget UI |
| `overflow` | `topic`, `dropped` | Per-topic queue overflow (§7.5) | |

A unifying client MUST render `qr`, `oauth`, `device_code`, and `token_required`. Unknown future `$session` kinds MUST be shown as `kind` plus remaining payload fields (forward compatible), never dropped on the floor.

**Token-in-store wait (like QR):** `session.pair` / implicit pair emits `token_required` and **watches** `{store}/{path}` until the file exists and parses, or the RPC fails (`pair_error`). The client writes the file (it owns prompting the human). Re-calling `connect` after writing also works.

**OAuth localhost:** the daemon MAY bind `127.0.0.1` for the redirect and put that URL in `oauth.url`. A purely local stdio process MUST NOT require a public HTTPS callback.

**Device code:** the daemon polls the token endpoint at `interval` (or the provider default) until success, expiry, or disconnect.

### 7.2 `$directory`

| `kind` | Payload |
|---|---|
| `upsert` | Row fields. Event envelope already uses `topic`/`kind`, so the row id is `jid` and the entity type is `entityKind` (`user` \| `group`). No `participants` — use `directory.get`. |
| `remove` | `jid` (canonical topic) |
| `ready` | `{generated: n}` after the first populate wave. Later upserts may still arrive. |

**When to emit `upsert` vs chat `meta`**

| Change | `$directory` | Chat topic `kind: meta` |
|---|---|---|
| You were added / new group / new 1:1 discovered | `upsert` | `meta` only if already subscribed |
| You left / removed / group gone | `remove` | `meta` then no further events |
| Rename, topic/description, icon | `upsert` | `meta` (`rename` / `topic` / `icon`) |
| Member join/leave/promote/demote | **no** (optional `participantCount` only) | `meta` |
| Mute / pin / archive | `upsert` flags | no |

### 7.3 Chat topics

Every chat-topic event has `contents[]`. Top-level `kind` is the **class**. `$session` / `$directory` have **no** `contents`.

| `kind` | `contents` | Rule |
|---|---|---|
| `message` | `text` \| `image` \| `video` \| `audio` \| `document` \| `sticker` \| `location` \| `unknown` | Heterogeneous. Spec allows N parts. Blob-part count on **send** follows `attachments`. |
| `reaction` | exactly one `{type:"reaction", emoji, target}` | `contents[0].type === kind`. Envelope `by` is who reacted. |
| `ack` | exactly one `{type:"ack", ids, ack}` | `ack` = `delivered` \| `read` \| `played`. Envelope `id` omitted; `ids` on the part. Only when `capabilities.ack` is true. |
| `meta` | exactly one `{type:"meta", action, …}` | `action` + fields (`join`/`leave`/`promote`/`demote`/`rename`/`topic`/`icon`/…). Envelope `by` when known. |

**`message` parts**

| `type` | Props | Notes |
|---|---|---|
| `text` | `text` | Body or caption. |
| `image` `video` `audio` `document` `sticker` | `path?`, `error?` | `path` if `files` set and download succeeded; else `error`. Relative to `files`. |
| `location` | `lat`, `lng`, `name?`, `address?` | No file. Place `name` is not `topicName`. |
| `unknown` | `label?` | Polls, view-once, buttons, encrypted ciphertext, slash-command envelopes, … **No blob.** |

View-once and ciphertext are `kind: message` with a single `unknown` part (`label: "view_once"` / `"encrypted"`), not a top-level `kind: unknown`.

Unknown future part `type` values: show `type` plus remaining fields (forward compatible). Do not drop the event.

Envelope (all chat kinds):

```json
{
  "topic": "opaque-chat",
  "kind": "message",
  "id": "3EB0…",
  "by": "opaque-user",
  "handle": "@ada",
  "topicName": "Family",
  "byName": "Ada",
  "context": "1710000000.000001",
  "contents": [
    {"type": "image", "path": "in/opaque-chat/3EB0.jpg"},
    {"type": "text", "text": "hello"}
  ]
}
```

- `by` is `"me"` or an opaque author id.
- `handle` is the author’s username **with a leading `@`**, when known. Omit otherwise.
- `topicName` is the chat’s display name. Omit if unknown.
- `byName` is the author’s display name. Omit if unknown.
- `context` is the optional grouping key (§4.5). Omit on the main stream. Same value → one group; root is the event whose `id` equals `context`.
- There is no `pn` on chat events. Look up `by` (or the 1:1 `topic`) via `directory.get`.
- `path` lives on a **content part**, relative to `files`.
- Unsubscribed chats: **protocol-ack at the product layer if required, then drop**. No event, no download.
- `ack` / `meta` still get `topicName` when known. `handle` / `byName` only when `by` is present.

**Inbound vs send cardinality:** if the product delivered **one** native message with N attachments, emit one `kind: message` with N parts. If it delivered N native stanzas (WhatsApp album, Matrix images), emit **N events**. Coalescing albums is extra daemon state and is not v1.

### 7.4 Delivery semantics

| Situation | Guarantee |
|---|---|
| Live event on a subscribed topic | At-most-once, arrival order per topic |
| Late subscribe | No replay |
| Process restart | No replay of a local transcript; client re-`initialize` + `subscribe` / `connect` |
| Offline catch-up from the product **or hosted ingress** | Treated as live; only current subscriptions; at-most-once after the bridge acks the native socket / bus message |
| Initial snapshot / history / delta full-sync | **Never emitted as live.** Persist the cursor (`next_batch`, delta token, `update_id`) and only emit **subsequent** incremental events. |

### 7.5 Backpressure

Per-topic in-memory queue (recommend 256). On overflow: drop **oldest**, emit `$session` `{kind:"overflow", topic, dropped}`. Never block the product handler loop.

---

## 8. Directory data model

### 8.1 `DirectoryRow`

```json
{
  "topic": "opaque-id",
  "kind": "user",
  "name": "Ada",
  "handle": "@ada",
  "pn": "optional-label",
  "icon": "in/_dir/opaque.jpg",
  "muted": false,
  "pinned": false,
  "archived": false,
  "participantCount": 0,
  "parent": "optional-guild-or-team"
}
```

| Field | Applies to | Notes |
|---|---|---|
| `topic` | all | Canonical opaque id. |
| `kind` | all | `user` \| `group`. **No new RPC.** Channels, rooms, Slack channels, Teams channels, Telegram groups/supergroups/channels, Matrix rooms → `group`. DMs / 1:1 → `user`. |
| `name` | all | Best display name. Discord/Teams MAY use `"Guild / #general"` so a flat list stays readable. |
| `handle` | user | Username with a leading `@`. Groups omit. |
| `pn` | user | Optional mutable label (WhatsApp phone JID; elsewhere email, phone, or omit). Opaque to the client. |
| `icon` | all | Relative path under `files`. Only after `directory.get` when fetched. |
| `muted` `pinned` `archived` | all | When the product has them. |
| `participantCount` | group | Optional; **not** the full roster. |
| `participants` | group, **`get` only** | `[{topic, name?, pn?, handle?}]`. |
| `parent` | optional | Additive. Opaque id of containing guild / team / space. Clients that do not bind it ignore it. Not a new RPC. |

No `lastMessage`, no preview text, no unread counts derived from bodies.

### 8.2 Persistence

Normative intent, not a frozen migration: a local DB of chats/contacts keyed by canonical topic. Never: `messages`, FTS, media keys-as-history.

### 8.3 Populate (async, after `online`)

Do **not** block `session.connect` on this. Stream `$directory` upserts; finish with `ready`.

Keep the directory warm from live events (new group, rename, join) even when the chat is not subscribed — that is how a client discovers a topic to subscribe to. Still **no message bodies**.

Live-message upserts MUST NOT corrupt identity (WhatsApp: never write sender push name onto a group subject; see WHATSBOX.md §7.3). Other products: never overwrite a group/channel title with the last author’s display name.

---

## 9. Files

`initialize.files`, when set, MUST be an absolute client-owned directory the daemon may read/write. Missing `files` ⇒ text-only. RPC payloads MUST carry **relative paths** only (never absolute paths, never media bytes). One process, one store directory, one files directory.

| Direction | Path | Who writes |
|---|---|---|
| Inbound media | `{files}/in/{safeTopic}/{id}[.ext]` | daemon, after download, then `event` |
| Inbound icon from `directory.get` | `{files}/in/_dir/{safeTopic}[.ext]` | daemon, then return `icon` |
| Outbound | any file **under** `{files}` | client; RPC carries a **relative** path |

- `safeTopic` = filesystem-safe encoding of the canonical topic (replace `@` / path separators / `!` / `:`).
- Daemon **never deletes**. TTL/GC is the client’s.
- No `files` → no downloads, no icons, send with a blob part `path` → `files_required`.
- Unsubscribed inbound media is **never** downloaded.
- `unknown` **parts** are **never** written.
- Write then notify (no truncated-file race).
- Reject paths that escape `files` → `path_escape`.
- Missing `files` ⇒ text-only.

---

## 10. Session state machine

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
   $session qr / oauth /             │
   device_code / token_required      │
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

- `logged_out` from the product: treat as logout cleanup (wipe local identity) so the store cannot pretend to be linked.
- Presence: **quiet**. Do not advertise “available” / typing unless a later spec says so.
- `online` means send **and** receive are up (product socket and/or ingress §2.5). Ingress-only drop → `offline`.

States are exactly `new` | `offline` | `online`. `new` = no `me`.

---

## 11. Errors

JSON-RPC application errors use codes in the `-32000`…`-32099` range. `error.message` is a **stable token**. Minimum tokens:

| Token | Code | Meaning |
|---|---|---|
| `not_initialized` | -32001 | RPC before `initialize` |
| `already_initialized` | -32002 | Second `initialize` |
| `store_required` | -32003 | No `--store` and no `initialize.store` |
| `store_mismatch` | -32004 | Both set and not the same path |
| `store_locked` | -32005 | Another process holds the store |
| `unsupported_version` | -32006 | `version` not `"0.1"` |
| `not_paired` | -32007 | Action needs keys/tokens |
| `pair_error` | -32008 | Auth failed |
| `not_found` | -32009 | `directory.get` / unknown topic resolution / subscribe of a non-roster chat when `membership` is `join` or `create` / `directory.leave` when not a member |
| `invalid_topic` | -32010 | `$` reserved, bad topic, alias on subscribe or `directory.join` / `leave`, unsubscribe `$session` |
| `files_required` | -32011 | Blob op without `files` |
| `path_escape` | -32012 | `path` outside `files` |
| `invalid_params` | -32013 | Missing `by` on read / reply / react, `me` on an issued product, etc. |
| `disconnected` | -32014 | Needs `online` (send, read, find/join/leave/create, live resolve) |
| `unsupported` | -32015 | Advertised capability does not allow the action (`react` `false`, `reply`/`read`/`attachments` `"none"`, `membership: "none"` on find/join/leave/create, `membership: "join"` on create, or `attachments: "single"` with two blob parts), **or** the topic cannot honor the RPC (ciphertext-only room). When it is a capability gap, `error.data.capability` names the key (`read`, `reply`, `react`, `attachments`, `membership`, …). |
| `me_required` | -32016 | `capabilities.me` is `"claimed"` and `me` was omitted on pair / connecting initialize |
| `topic_taken` | -32017 | `directory.create` `{topic}` is held by another occupant |

Standard JSON-RPC: `parse_error` -32700, `invalid_request` -32600, `method_not_found` -32601.

`unsupported` is how gaps become **protocol-visible**. Silent no-ops are forbidden.

---

## 12. Client-once rules

A single client (the WhatsBox managed codec, a REPL, an agent) implements the method table in §13 **once**.

| Concern | Client does | Client does not |
|---|---|---|
| Framing | NDJSON JSON-RPC 2.0, named params, `event` notifications | Product REST, Gateway opcodes, Graph payloads, MCP |
| Find a chat | `directory.list` (roster) or `directory.find` then `directory.join` (when advertised) then `subscribe` with `row.topic` | Subscribe by name / phone / `#channel`; join via `subscribe` |
| Auth UX | Render `$session` `qr` / `oauth` / `device_code` / `token_required`; if `capabilities.me` is `"claimed"`, pass `me` on pair | Product-specific pair RPCs; a `me_required` **event** |
| Capabilities | Read `product` + `capabilities`; hide or skip impossible actions | Call `discord.read` because WhatsApp has blue ticks |
| `by` / topics / `context` | Copy opaque strings | Parse `@lid`, snowflakes, `thread_ts`, `!room:server` |
| Grouping | If any event on the topic has `context`, bucket by it; else flat | Per-product thread APIs; `context` as a subscribe topic |
| Reply | Always `reply: {id, by, text?}`. If `capabilities.reply` is `"context"`, also `context: e.context ?? e.id` | `thread_ts` / `message_reference` on the wire |
| Read | Always `messages.read` with `by` copied from the event | Parse `@g.us` / directory `kind` to omit `by` in 1:1 |
| Chat events | `kind` ∈ `message` \| `reaction` \| `ack` \| `meta`; always `contents[]` | Per-product MIME APIs; `kind: text` / `kind: image` envelopes |
| Send | `{to, contents, reply?, context?}` | Envelope `text` / `path` / `react` |
| Files | Pass absolute `files`; blob `path` on a **part** | Base64 on the RPC |
| Live events | Consume `event` notifications | Poll, Azure, SignalR, webhooks, or a `live` capability |
| Gaps | Honor `unsupported` and degraded `read`/`reply` values | Invent methods |

If a section of a profile required per-product client method names, that profile would be non-conformant.

---

## 13. v1 method index

| Method | In | Out |
|---|---|---|
| `initialize` | `version`, `store?`, `files?`, `subscribe?`, `verbosity?`, `connect?`, `deviceName?`, `me?` | status snapshot (`connect:true` ⇒ `session.connect`) including `product` + `capabilities` |
| `session.connect` | — | status (`new` ⇒ implicit pair) |
| `session.pair` | `me?` | status (no-op if already linked) |
| `session.disconnect` | — | status |
| `session.logout` | — | status `new` |
| `session.status` | — | `{me?, status, topics, product, identity, capabilities}` |
| `subscribe` | `{topics}` | `{topics}` canonical |
| `unsubscribe` | `{topics}` | `{topics}` remaining |
| `directory.list` | `{query?, kind?, limit?, cursor?}` | `{items, cursor?}` |
| `directory.get` | `{id, icon?}` | `DirectoryRow` |
| `directory.find` | `{query?, kind?, limit?, cursor?}` | `{items, cursor?}` (not roster) |
| `directory.join` | `{id}` | `{topic}` |
| `directory.leave` | `{id}` | `{topic}` |
| `directory.create` | `{name, topic?}` | `{topic}` |
| `messages.send` | `{to, contents, reply?, context?}` | `{id, topic}` |
| `messages.read` | `{to, ids, by}` | `{topic}` |

Notifications: **`event` only**.

---

## 14. WhatsApp reference profile

Canon: [`docs/WHATSBOX.md`](WHATSBOX.md), [`docs/RFC-1.md`](RFC-1.md). Library: [whatsmeow](https://github.com/tulir/whatsmeow). Official identity: **linked device of a real user** (WhatsApp Web companion). User self-automation of the phone app is out of scope; this is the multi-device protocol.

| Field | Value |
|---|---|
| `product` | `whatsapp` |
| `identity` | `user` |
| `capabilities.auth` | `["qr"]` |
| `me` | `"issued"` |
| `membership` | `"none"` |
| `reply` | `"quote"` (ContextInfo quote; `reply.text` stub; no `remoteJid` on same-chat quotes). Never emit `context`. |
| `react` | `true` |
| `read` | `"message"` |
| `ack` | `true` (`delivered` / `read` / `played`) |
| `files` | `true` |
| `attachments` | `"single"` (one media part + optional text caption). Extra blob parts → `unsupported`. Inbound album = N events (no coalescing). |

Auth: `$session` `qr` rotating `code`. No pair-code / passkey in v1 (`pair_error`). Store: `session.db` + directory DB. Subscribe: LID / `@g.us` / PN JID / `$directory` only.

Implementation difficulty: **3 / 5** (done in this repo). Hard parts are LID canonicalization, HistorySync headers-only, and quiet presence.

---

## 15. Discord mapping

**Official / legal identity: bot only.** Automating a user account (“self-bot”) is forbidden by Discord’s [Developer Terms](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots) and can result in account termination. There is **no** WhatsApp-style “linked user companion” on Discord. `identity` is always `bot`. `me` is the bot user snowflake.

| Field | Value |
|---|---|
| `product` | `discord` |
| `identity` | `bot` |
| `capabilities.auth` | `["token"]` |
| `me` | `"issued"` |
| `membership` | `"none"` |
| `reply` | `"quote"` (`message_reference`) |
| `react` | `true` (`PUT …/reactions/{emoji}/@me`) |
| `read` | `"none"` — Discord [does not have read receipts](https://paul.koeck.dev/writeups/discord-read-receipts); bot ACK of a channel is not a user-visible tick |
| `ack` | `false` |
| `files` | `true` (REST multipart; Gateway does not carry bytes) |
| `attachments` | `"many"` |

### 15.1 Auth

Token-in-store. `session.pair` emits:

```json
{"topic":"$session","kind":"token_required","path":"credentials.json","hint":"bot_token"}
```

Client writes `{store}/credentials.json`:

```json
{"bot_token":"MTk4NjIy…"}
```

Daemon watches the file, validates with `GET /users/@me` (`Authorization: Bot …`), then `paired`. Bot invite URL MAY additionally be emitted as `oauth` (`url` = OAuth2 bot invite with `bot` + `applications.commands` scopes) so the human can add the bot to a guild; that is **guild install**, not user login. Pairing still requires the bot token in the store.

### 15.2 Topics and directory

| Entity | Topic | `kind` |
|---|---|---|
| DM channel | channel snowflake | `user` |
| Guild text / thread parent | channel snowflake | `group` |
| Group DM | channel snowflake | `group` |

`name`: `"GuildName / #channel"` for guild channels; peer username for DMs. Optional `parent` = guild id. `handle` = `@username` of the peer (DMs) or omitted (channels). `by` = author user snowflake.

Populate after `online`: `GET /users/@me/guilds`, then per-guild channels the bot can see; DM channels from Gateway `CHANNEL_CREATE` / REST. Members as `user` rows (lazy; full member list is privileged and huge — list what the bot has already seen plus `directory.get` of a snowflake).

Subscribe: channel snowflake only. Send `to` MAY accept a user snowflake: daemon `POST /users/@me/channels` to open a DM, then remap.

### 15.3 Live path

Discord Gateway (websocket) with bot token. Intents: `GUILD_MESSAGES`, `DIRECT_MESSAGES`, `GUILD_MESSAGE_REACTIONS`, `MESSAGE_CONTENT` (privileged — required to see message text). Filter `MESSAGE_CREATE` to **subscribed** channel ids; protocol-ack is not a Discord concept beyond Gateway heartbeat. Do not emit the READY guild snapshot as live chat.

Reconnect: Gateway resume; missed events during downtime are gone (at-most-once). Hosted ingress (§2.5) does **not** replace Gateway for `MESSAGE_CREATE` (Discord Interactions HTTP is a different animal). Stdout is `event` notifications; do not advertise a poll mode.

Discord **threads are channels** (their own snowflake → `kind: group` topic). Do **not** put a thread id in `context` on the parent channel. `message_reference` is `reply`, not `context`.

### 15.4 Send / reply / react / read

| RPC | Native |
|---|---|
| send | `POST /channels/{id}/messages`: text parts → `content`; blob parts → multipart `files[n]`. `attachments: "many"`. Result is one message id. |
| reply | `message_reference: {message_id: reply.id}`; `by` ignored |
| react | `PUT /channels/{id}/messages/{id}/reactions/{emoji}/@me` from a `{type:"reaction"}` part; empty emoji → `DELETE` |
| read | **`unsupported`** (`capability: "read"`). Not a silent no-op. |

Inbound reactions: `MESSAGE_REACTION_ADD` / `_REMOVE` → chat `kind: reaction`. No `ack` events.

### 15.5 Files

Download attachments from `attachment.url` / `proxy_url` only if subscribed and `files` is set. Stickers: `sticker` if a PNG/APNG/Lottie file can be fetched; else `unknown` `label: "sticker"`.

### 15.6 Difficulty

**2 / 5.** Well-documented Gateway + REST, one bot token, local websocket. Privileged intents and bot-invite UX are the main friction. The product gap is **identity** (bot, not user companion) and **no read receipts**.

Degraded client-visible behavior: `read: "none"` → `messages.read` errors `unsupported`; `ack: false` → no ticks; directory is guilds/channels the **bot** is in, not the human’s private DMs.

---

## 16. Slack mapping

**Official identity:** a Slack app. Recommended: **bot token** (`xoxb-`) plus **app-level token** (`xapp-`, `connections:write`) for [Socket Mode](https://docs.slack.dev/tools/python-slack-sdk/socket-mode). Optional user token (`xoxp-`) lets the app act as a member (DMs the bot cannot see, `conversations.mark` as that user). v1 default profile is **bot**.

| Field | Bot profile | User-token profile |
|---|---|---|
| `product` | `slack` | `slack` |
| `identity` | `bot` | `user` |
| `profile` | `bot` | `user` |
| `auth` | `["token"]` (both tokens in store) or `["oauth"]` | `["oauth"]` |
| `me` | `"issued"` | `"issued"` |
| `membership` | `"none"` | `"none"` |
| `reply` | `"context"` | `"context"` |
| `react` | `true` (`reactions.add` / `remove`) | `true` |
| `read` | `"cursor"` | `"cursor"` |
| `ack` | `false` | `false` |
| `files` | `true` (`files.uploadV2`) | `true` |
| `attachments` | `"many"` | `"many"` |

### 16.1 Auth

Token-in-store:

```json
{"topic":"$session","kind":"token_required","path":"credentials.json","hint":"bot_token_and_app_token"}
```

```json
{"bot_token":"xoxb-…","app_token":"xapp-…","user_token":"xoxp-…"}
```

`user_token` optional. OAuth install (`oauth.url` = Slack OAuth v2 authorize) MAY be used instead; daemon uses localhost redirect. Socket Mode needs the app-level token either way.

### 16.2 Topics and directory

| Entity | Topic | `kind` |
|---|---|---|
| IM | `D…` conversation id | `user` |
| Channel / private channel / MPIM | `C…` / `G…` | `group` |

`handle` = `@name` of the IM peer. `name` = channel name or peer real name. Populate: `users.list` (users) + `conversations.list` (public/private/im/mpim the token can see). Bot only sees channels it was invited to.

Subscribe: conversation id only. Send `to` MAY accept `@user` / `#channel` **only on send/get**, not subscribe; daemon resolves via `conversations.list` / `users.lookupByEmail` and returns canonical `C…`/`D…`.

### 16.3 Live path

**Local default:** Socket Mode websocket (app token) delivers Events API envelopes. Ack every Socket Mode envelope (`envelope_id`) at the Slack layer, then drop if the conversation is not subscribed.

**Optional ingress:** HTTP Events API (`request_url` = hosted webhook) via §2.5. The host verifies Slack’s signing secret and `url_verification`; the bridge consumes native event payloads from the bus and maps them. Socket Mode and HTTP Events API are alternatives, not both at once.

RTM is legacy; do not use it. Do not replay `conversations.history` on subscribe. Stdout is `event` notifications either way; do not advertise a poll mode.

### 16.4 Send / reply / react / read

| RPC | Native | Degraded behavior |
|---|---|---|
| send | `chat.postMessage` / `files.uploadV2` from `contents[]` | Bot posts as the app, not the human (`identity: bot`). `attachments: "many"`. Still one Box `{id}`. |
| reply | `chat.postMessage` with `thread_ts` = outbound `context` if set, else `reply.id` | **Not a quote.** `capabilities.reply` is `"context"`. Prefer outbound `context` (the root). `e.context ?? e.id` on the client means a child reply continues the thread; a root with no `context` **starts** one. `reply.text` MAY be ignored. |
| react | `reactions.add` `{name, channel, timestamp}` from a `{type:"reaction"}` part; empty emoji → `reactions.remove` | Slack names (`thumbsup`) not Unicode; daemon maps common emoji → Slack name and MAY pass Unicode when the API accepts it. Failure → `invalid_params`. |
| read | `conversations.mark` `{channel, ts: max(ids)}` | **Not per-message blue ticks.** Cursor for the token owner. RPC succeeds. No `ack` events. Slack’s own docs: “We don't know why bot users would want to move their read cursor but it can be done.” |

Inbound `reaction_added` / `reaction_removed` → `kind: reaction`. No `ack`.

### 16.5 Difficulty

**3 / 5.** Two tokens, Socket Mode, Events API ack, bot-must-be-in-channel, thread vs quote mismatch. Easier than Teams; harder than Discord bot because of threading and token types.

Degraded: `reply: "context"` (grouping, not quotes); `read: "cursor"` (not ticks); `ack: false`; bot cannot see channels it was not invited to.

**Inbound `context`:** thread replies MUST set `context` to Slack `thread_ts`. Channel roots omit it until a thread exists; once Slack sets `thread_ts == ts` on the root, the daemon MAY echo `context` equal to `id`. The client groups without knowing timestamps.

---

## 17. Microsoft Teams mapping

**Official identity:** Microsoft Graph. Two modes:

- **Delegated** (recommended for a companion): the process acts as a signed-in work/school user. Closest to WhatsApp’s “this is me.”
- **Application**: tenant-wide `Chat.ReadWrite.All` / `ChannelMessage.Read.All` — admin consent, not a personal companion.

Personal Microsoft accounts are **not** supported by Teams chat Graph APIs. `identity` is `user` (delegated) or `bot` (application).

| Field | Delegated (v1 default) | Application |
|---|---|---|
| `product` | `teams` | `teams` |
| `identity` | `user` | `bot` |
| `auth` | `["device_code"]` or `["oauth"]` | `["token"]` (client credentials in store) + admin consent |
| `me` | `"issued"` | `"issued"` |
| `membership` | `"none"` | `"none"` |
| `reply` | `"quote"` (`chatMessage` `replyToId` / replies collection) | `"quote"` |
| `react` | `true` (`setReaction` / `unsetReaction`) | `true` |
| `read` | `"conversation"` (`markChatReadForUser`, **beta**, delegated only) | `"none"` (application cannot mark a user’s chat read) |
| `ack` | `false` | `false` |
| `files` | `true` (hosted contents / `sharePoint` attachments) | `true` |
| `attachments` | `"many"` | `"many"` |

### 17.1 Auth

Device code (best for a local CLI):

```json
{"topic":"$session","kind":"device_code","user_code":"B2AB8TQB","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"interval":5}
```

OAuth authorization code with **localhost redirect** is also allowed (`kind: oauth`). Client credentials (application identity) use `token_required` with `client_id` / `client_secret` / `tenant_id` in `credentials.json`.

Scopes (delegated): `Chat.ReadWrite`, `ChannelMessage.Send`, `Channel.ReadBasic.All`, `Team.ReadBasic.All`, `User.Read`, `Files.ReadWrite`. Application scopes need **admin consent**.

### 17.2 Topics and directory

| Entity | Topic | `kind` |
|---|---|---|
| 1:1 chat | Graph `chat.id` (`19:…@unq.gbl.spaces`) | `user` |
| Group chat | Graph `chat.id` | `group` |
| Channel | `{teamId}/{channelId}` (opaque, joined with `/`) | `group` |

`parent` = team id for channels. `name` = `"Team / Channel"` or chat topic. `by` = Azure AD user id (or `application` id for bots).

Populate: `GET /me/chats` + `GET /me/joinedTeams` + per-team `channels`. Do not treat meeting chats as special beyond `kind: group`.

Subscribe: canonical chat id or `{teamId}/{channelId}`. Send `to` MAY accept a UPN on 1:1; daemon creates/finds the chat (`POST /chats`).

### 17.3 Live path — hosted ingress (v1)

Graph [change notifications](https://learn.microsoft.com/en-us/graph/teams-change-notification-in-microsoft-teams-overview) require a public HTTPS `notificationUrl` (validation handshake, often `lifecycleNotificationUrl`, and encryption certs when `includeResourceData` is true). A purely local stdio process cannot be that URL.

**v1 path:** hosted webhook + bus (§2.5). The host answers Graph’s validation POST and decrypts resource data if used. The local process is a bus consumer. Stdout is `event` notifications. The JSON-RPC client MUST NOT see Graph payloads, Azure nouns, or a poll loop.

The local process (not the host) still:

- `POST /subscriptions` with `notificationUrl` pointing at the host (or the host does this with application permissions).
- Maps native `chatMessage` notifications to Box `event`s.
- Filters by the current subscribe set (application-permission `getAllMessages` may enqueue a tenant-wide firehose).
- Downloads attachments into `files`.

**First Graph snapshot / subscription creation MUST NOT become live `event`s** (§7.4).

**Implementer fallback (no ingress configured):** [delta query](https://learn.microsoft.com/en-us/graph/api/chatmessage-delta?view=graph-rest-1.0) on a timer (recommend 5–15 s, honor `Retry-After`):

- Chats: `GET /me/chats/{id}/messages/delta` or `GET /users/{id}/chats/getAllMessages/delta`
- Channels: `GET /teams/{id}/channels/{id}/messages/delta`

First delta page is a full snapshot — persist `@odata.deltaLink`; only subsequent incremental pages become `event`s. Stdout is still `event` notifications; MUST NOT be advertised as `"poll"`. Clients MUST NOT branch on poll vs ingress. Prefer configuring ingress; poll is for air-gapped / bring-up only.

### 17.4 Send / reply / react / read

| RPC | Native | Degraded behavior |
|---|---|---|
| send | `POST /chats/{id}/messages` or `POST /teams/{t}/channels/{c}/messages` from `contents[]` | Text → `{body:{contentType:"text", content}}`. Blob parts → hosted content / `sharePoint` attachments. `attachments: "many"`. Still one Box `{id}`. |
| reply | chat: `replyToId`; channel: `POST …/messages/{id}/replies` | Chats are quote-like (`reply: "quote"`). Channel nested replies MUST set inbound `context` to the **root post id** so the client groups them. Outbound: if `context` is set, post as a reply under that root; else `reply.id`. Still `reply: "quote"` (Graph has a reply object). |
| react | `POST …/setReaction` `{reactionType}` from a `{type:"reaction"}` part; empty emoji → `unsetReaction` | |
| read | delegated: `POST /chats/{id}/markChatReadForUser` (beta) | **Whole chat**, not per `ids`. `read: "conversation"`. Channel read is not the same API — for `{teamId}/{channelId}` v1 returns `unsupported` (`capability: "read"`) unless a v1.0 equivalent exists. Application identity: `read: "none"` → always `unsupported`. |

No `ack` events (no delivered/read/played to the sender comparable to WhatsApp).

### 17.5 Difficulty

**4 / 5.** Still the hardest profile, but the live-path hole is the hosted ingress, not a protocol lie. Remaining cost: Entra app registration, admin consent for application permissions, chat vs channel dual API, encrypted resource-data certs on the host, beta mark-read, hosted attachments, throttling. Implement last.

Degraded: `read` conversation-level or `unsupported`; `ack: false`. Without ingress, implementer delta-poll (seconds of latency) still looks like `event` notifications on stdout.

---

## 18. Telegram mapping

Telegram has **two official stacks**. They MUST NOT be collapsed into one profile.

### 18.1 Bot API (recommended default)

HTTP Bot API, token from [@BotFather](https://core.telegram.org/bots). `identity: bot`. `profile: bot`. Easy, local (`getUpdates` long-poll). The process is **not** the human’s user account: it cannot see the user’s private chats with other people; groups only if the bot is a member (privacy mode may hide ordinary messages).

| Field | Value |
|---|---|
| `product` | `telegram` |
| `identity` | `bot` |
| `profile` | `bot` |
| `auth` | `["token"]` |
| `me` | `"issued"` |
| `membership` | `"none"` |
| `reply` | `"quote"` (`reply_parameters` / `reply_to_message_id`) |
| `react` | `true` (`setMessageReaction`) |
| `read` | `"none"` (bots do not send user-visible read receipts) |
| `ack` | `false` |
| `files` | `true` (`getFile` + `sendDocument`; cloud Bot API caps ~20 MB down / 50 MB up) |
| `attachments` | `"single"` (v1: one media per send; `sendMediaGroup` would return N ids) |

Auth: `token_required` `{path:"credentials.json", hint:"bot_token"}` → `{"bot_token":"123:ABC"}`. Validate `getMe`.

Topics: chat id as decimal string (`-100…` groups, positive users). `kind: user` for private chats, `group` for groups/supergroups/channels. `handle` = `@username`. `by` = user id string.

**Local default:** `getUpdates` with `offset`; `allowed_updates` includes `message`, `message_reaction`, `edited_message`. Confirm updates via `offset` (Telegram-layer ack) then drop unsubscribed chats.

**Optional ingress:** `setWebhook` at the hosted URL (§2.5). `getUpdates` and webhooks are mutually exclusive; pick one. The host verifies the secret token; the bridge maps native `Update` objects.

Privacy mode: groups may not deliver ordinary member messages — that is not a protocol bug; directory still lists chats the bot knows. Stdout is `event` notifications either way; do not advertise a poll mode.

Send: map `contents[]` to `sendMessage` / `sendPhoto` / `sendDocument` / … (one native call, one id). Extra blob parts → `unsupported` (`attachments: "single"`). `sendMediaGroup` is out of v1 because it returns N ids. Reply: `reply_parameters.message_id`. React: `setMessageReaction` from a `{type:"reaction"}` part. Read: `unsupported`.

Inbound `message_reaction`: **not delivered for reactions on bot-sent messages in 1:1 DMs** (Bot API rule). Degraded: react **send** works; inbound `kind: reaction` in private chats MAY be missing. Still advertise `react: true` (the RPC works). Document the inbound hole.

Difficulty: **1 / 5**. First implementer target after (or before) Discord.

### 18.2 MTProto user client

Native client protocol (TDLib / Telethon / GramJS / MadelineProto). `identity: user`. `profile: user`. This is the WhatsBox-closest Telegram: real dialogs, contacts, user-visible receipts in some clients.

| Field | Value |
|---|---|
| `product` | `telegram` |
| `identity` | `user` |
| `profile` | `user` |
| `auth` | `["qr"]` and/or `["token"]` (phone code is rendered as `qr` **or** `device_code` — see below) |
| `me` | `"issued"` |
| `membership` | `"none"` |
| `reply` | `"quote"` |
| `react` | `true` |
| `read` | `"cursor"` (`messages.readHistory` / `channels.readHistory` — up-to id, not WhatsApp per-message blue ticks in groups) |
| `ack` | `false` (outgoing `UpdateReadHistoryInbox` is not WhatsApp `delivered/read/played` to the sender in this spec; do not fake `ack`) |
| `files` | `true` (no Bot API size cap; still store-based) |
| `attachments` | `"single"` (albums are N native messages; do not coalesce. `sendMediaGroup` would return N ids) |

**Auth:** `API_ID` + `API_HASH` from my.telegram.org go in the store (`credentials.json`) **plus** user login:

1. Emit `token_required` for `api_id` / `api_hash` if missing (`hint: "telegram_api_id_hash"`).
2. Then `auth.sendCode`: emit `device_code` with `user_code` = the SMS/Telegram code prompt and `verification_uri` a `tg://` or `https://telegram.org` login hint — **or** `qr` with `code` = the `auth.exportLoginToken` QR payload when using QR login.
3. 2FA password: further `token_required` `{path:"credentials.json", hint:"2fa_password"}` (watch for a `password` field). Do not add a `session.2fa` RPC.

Phone number pairing without QR is **not** a new method; it is `device_code` + token file.

**Legal / ToS:** user-account automation is closer to a custom client (allowed with API_ID) than Discord self-bots, but bulk/unattended userbots are against Telegram’s rules. A companion REPL that the human is sitting at is the intended use. Document this in the binary’s README.

Topics: dialog peer (`user_id`, `-group_id`, `-100channel_id`) as strings. Directory from `messages.getDialogs` + `contacts.getContacts`. Subscribe canonical peer only. Initial dialog list is directory populate, **not** live history — do not emit `messages.getHistory`.

Read: `messages.readHistory` to max(`ids`) → `read: "cursor"`. Secret chats: inbound `kind: message` with `[{type:"unknown", label:"secret"}]`; send/read/react → `unsupported` (no `error.data.capability`).

Difficulty: **4 / 5.** API_ID registration, DC migration, 2FA, auth key persistence, library choice, updates gap/pts, ToS. Much closer to WhatsApp as a product; much harder than Bot API.

A binary MAY support only `profile: bot`. A binary that supports both MUST advertise `profile` and keep one identity per store (logout to switch).

---

## 19. Matrix mapping

**Official identity: a real user client** of a homeserver (Client-Server API `/sync`). Not a bot (Application Service) in v1 — AS would be a different identity and registration. `identity: user`.

| Field | Value |
|---|---|
| `product` | `matrix` |
| `identity` | `user` |
| `auth` | `["token"]` (access_token in store) and/or `["oauth"]` / password via `token_required` |
| `me` | `"issued"` |
| `membership` | `"none"` |
| `reply` | `"quote"` (`m.in_reply_to` / `m.relates_to`) |
| `react` | `true` (`m.reaction` + `m.annotation`) |
| `read` | `"cursor"` (`POST …/receipt/m.read/{eventId}` — up-to event, threaded receipts exist but v1 sends unthreaded) |
| `ack` | `true` — inbound `m.receipt` mapped to `ack: "read"` (no `delivered` / `played`) |
| `files` | `true` (`mxc://` download/upload) |
| `attachments` | `"single"` (one `m.room.message` per send; extra blob parts → `unsupported`. Inbound images are already one event each — do not coalesce) |

### 19.1 Auth

```json
{"homeserver":"https://matrix.example.org","access_token":"syt_…","user_id":"@ada:example.org","device_id":"BOXDEV"}
```

If only homeserver + username: `token_required` `{hint:"password"}` then `POST /_matrix/client/v3/login`. SSO: `oauth` with the SSO redirect (localhost callback). `deviceName` maps to `initial_device_display_name`.

Logout: `POST /logout` then wipe store.

### 19.2 Topics and directory

| Entity | Topic | `kind` |
|---|---|---|
| Room (including DM) | `!opaque:server` room id | `user` if 1:1 DM (`m.direct` account data), else `group` |
| User (for `by` / participants) | `@user:server` | — |

`handle` = `@user:server`. Aliases (`#room:server`) are **not** subscribe topics; resolve on `directory.get` / send `to`, then `remap` if a subscription was held on a temporary alias (prefer: reject aliases on subscribe as `invalid_topic`, allow on send/get).

Populate: joined rooms from `/sync` `rooms.join` **state** (name, topic, membership counts) plus `m.direct`. Do not emit timeline events from the first `/sync`.

### 19.3 Live path

**Local default:** `GET /_matrix/client/v3/sync?timeout=30000&since={token}`. Persist `next_batch` in the store. Long-poll is local; stdout is still `event` notifications. Do not advertise a poll mode.

**First `/sync` (no `since`):** use it for directory + `next_batch` only. Timeline chunks are history — **MUST NOT** be emitted as live (§7.4). Incremental `/sync` after that: emit subscribed rooms’ timeline, ephemeral receipts, and `m.reaction`.

Filter: lazy-load members; omit presence (quiet). Matrix has no native webhook; a host MAY `/sync` and fan-in over §2.5 — optional, not required.

### 19.4 Send / reply / react / read

| RPC | Native |
|---|---|
| send | `PUT /rooms/{roomId}/send/m.room.message/{txnId}` from `contents[]`: text → `{msgtype:"m.text", body}`; one blob → upload `/_matrix/media/v3/upload` then `m.image` / `m.file` / … Extra blob parts → `unsupported`. |
| reply | `m.in_reply_to.event_id` = `reply.id`; include `reply.text` in the fallback body if present. If outbound `context` is set, also `m.relates_to` `{rel_type: "m.thread", event_id: context}`. Inbound `m.thread` events MUST emit `context` (the thread root id). `capabilities.reply` stays `"quote"` (quotes work). |
| react | `m.reaction` with `m.relates_to` `{rel_type:"m.annotation", event_id, key: emoji}` from a `{type:"reaction"}` part; empty key → redaction of our prior annotation if known, else `not_found` |
| read | `POST /rooms/{roomId}/receipt/m.read/{eventId}` for the latest id |

### 19.5 E2EE rooms (v1 MUST NOT require Olm/Megolm)

There is no `e2ee` capability. Rooms with `m.room.encryption` state degrade per topic:

- Inbound `m.room.encrypted`: chat `kind: message` with `[{type:"unknown", label:"encrypted"}]`, **no blob**, no attempt to decrypt.
- `messages.send` / `messages.read` / react targeting that room: `unsupported` **without** `error.data.capability`.
- Directory still lists the room (so the client can see it exists). Unencrypted rooms in the same session work normally.

v1 implementers MUST NOT ship a half-broken crypto stack. A future spec may add `e2ee` when Olm/Megolm is real.

### 19.6 Difficulty

**3 / 5** without crypto (homeserver URL, login types, `/sync` filters, `mxc://` media). **5 / 5** if E2EE were in scope — it is not.

Degraded: encrypted rooms are visible in the directory but live as `kind: message` + `unknown` / send as `unsupported`; `read: "cursor"`; `ack` only `read`.

---

## 20. Azure Web PubSub mapping

**Official identity:** a Chat hub **user** (`userId` in a client access URL). There is no external bot portal: the client **claims** `me`. Adapter canon: [`docs/PUBSUBBOX.md`](PUBSUBBOX.md). Omit `profile` (implied Chat hub; a later `profile: "hub"` would be the base hub).

| Field | Value |
|---|---|
| `product` | `webpubsub` |
| `identity` | `user` |
| `profile` | omit |
| `auth` | `["device_code", "token"]` |
| `me` | `"claimed"` |
| `membership` | `"create"` |
| `reply` | `"none"` |
| `react` | `false` |
| `read` | `"none"` |
| `ack` | `false` |
| `files` | `true` |
| `attachments` | `"single"` |

### 20.1 Auth

`session.pair({me})` (or `initialize` with `me` + `connect: true`). Missing `me` → `me_required`.

Device code (`az-cli` inside the adapter) is `$session` `device_code`. A pre-minted client access URL in the store is `token` (skip Entra). Hub / resource are store-only: after Entra, one Chat hub → use it; otherwise `$session` `token_required` `{path:"credentials.json", hint:"hub"}`. No `hub` field on the RPC.

`me` on the wire is the Chat `userId`, never the Entra UPN.

### 20.2 Topics and directory

Groups: Chat `roomId` → `kind: "group"`. 1:1: `directory.find` may return `{topic: userId, kind: "user"}`; `directory.join` opens/reuses the DM room, `$session` `remap` to the room topic. Roster key is the room. Subscribe requires a roster row (`not_found` otherwise).

`directory.create` `{name, topic?}` is a group. `directory.find` / `join` / `leave` are live and online-only.

Populate after `online`: rooms this `me` already belongs to. History is **not** live (§7.4).

### 20.3 Live path

Chat client WebSocket (reliable reconnect is catch-up on **current** subscriptions only). Local; no public webhook. Stdout is `event` notifications.

### 20.4 Send / reply / react / read

| RPC | Native | Degraded |
|---|---|---|
| send | Chat text, or one blob via the adapter’s storage overlay (PUBSUBBOX.md) | `attachments: "single"`. Caption = optional `text` part. Extra blob parts → `unsupported`. |
| reply | — | `unsupported` (`capability: "reply"`). Chat `CreateMessage` / `refMessageId` is not implemented. |
| react | — | `unsupported` (`capability: "react"`). |
| read | — | `unsupported` (`capability: "read"`). `chat.markRead` is not implemented. |

### 20.5 Files

`files: true`. Bytes never on JSON-RPC. The adapter uploads to the hub’s storage account and points the Chat message at the URL; inbound downloads into `files` then notifies. How that is stored on Chat (`content.binary`, blob metadata) is **not** client-visible — [`docs/PUBSUBBOX.md`](PUBSUBBOX.md).

### 20.6 Difficulty

**2 / 5.** Chat SDK + claimed `me` + membership verbs. Hard parts are Entra device-code pairing, hub selection, and the blob overlay. No hosted ingress.

Degraded: no quotes, reactions, or receipts; one blob + optional caption; public blob URLs in v1 (SAS later).

---

## 21. Capability and difficulty matrix

Legend: **Diff** = implementation difficulty 1 (easiest) … 5 (hardest) for a gateway that already speaks this protocol. WhatsApp is the reference (already shipped).

| | WhatsApp | Discord | Slack | Teams | Telegram Bot | Telegram user | Matrix | Web PubSub |
|---|---|---|---|---|---|---|---|---|
| **Official identity** | Linked device (user) | **Bot only** (self-bots forbidden) | Bot app (optional user token) | Graph delegated user (or app + admin consent) | BotFather bot | MTProto user client | CS API user | Chat hub user (`userId` claimed) |
| `product` | `whatsapp` | `discord` | `slack` | `teams` | `telegram` | `telegram` | `matrix` | `webpubsub` |
| `identity` | `user` | `bot` | `bot` (default) | `user` (default) | `bot` | `user` | `user` | `user` |
| `me` cap. | `"issued"` | `"issued"` | `"issued"` | `"issued"` | `"issued"` | `"issued"` | `"issued"` | **`"claimed"`** |
| `membership` | `"none"` | `"none"` | `"none"` | `"none"` | `"none"` | `"none"` | `"none"` | **`"create"`** |
| **Auth** | QR (`qr`) | Token in store (`token_required`) | Token(s) or OAuth | Device code / OAuth / client secret | Bot token | API_ID + QR/code + 2FA | access_token / password / SSO | Device code + token |
| **Live path** | Local WS (whatsmeow) | Local Gateway WS (ingress does not replace it) | Socket Mode **or** HTTP Events API via hosted ingress | Hosted HTTPS `notificationUrl` → bus → bridge. Delta poll = air-gapped fallback only | `getUpdates` **or** `setWebhook` via ingress | Local MTProto updates | Local `/sync` long-poll (optional host fan-in) | Local Chat WebSocket |
| **Reply** | Quote (ContextInfo) | `message_reference` | **`thread_ts` via `context` (not a quote)** | `replyToId` / channel replies + inbound `context` | `reply_parameters` | MTProto reply | `m.in_reply_to` + optional `m.thread` `context` | **none** |
| `reply` cap. | `"quote"` | `"quote"` | **`"context"`** | `"quote"` | `"quote"` | `"quote"` | `"quote"` | **`"none"`** |
| **Reactions** | yes | yes | yes (Slack names) | `setReaction` | `setMessageReaction` (inbound 1:1 hole) | yes | `m.reaction` | **none** |
| `react` | `true` | `true` | `true` | `true` | `true` | `true` | `true` | **`false`** |
| **Mark-read** | Per-message blue ticks | **None** | `conversations.mark` cursor | Whole-chat `markChatReadForUser` (beta, delegated); channels `unsupported` | **None** | History up-to id | `m.read` up-to event | **None** |
| `read` | `message` | **`none` → `unsupported`** | **`cursor` (not ticks)** | **`conversation` or `none`** | **`none` → `unsupported`** | `cursor` | `cursor` | **`none` → `unsupported`** |
| **Acks to sender** | delivered/read/played | **none** | **none** | **none** | **none** | not mapped | `m.receipt` → `ack:read` | **none** |
| `ack` | `true` | `false` | `false` | `false` | `false` | `false` | `true` | `false` |
| **Files** | download then path | attachment URL → path | `files.uploadV2` | hosted contents | `getFile` (size cap) | MTProto download | `mxc://` | storage overlay (PUBSUBBOX.md) |
| `files` | `true` | `true` | `true` | `true` | `true` | `true` | `true` | `true` |
| `attachments` | `"single"` | `"many"` | `"many"` | `"many"` | `"single"` | `"single"` | `"single"` | `"single"` |
| **E2EE** | Signal (library; not client-visible) | n/a | n/a | n/a | n/a | secret chats → `message`+`unknown` / `unsupported` | **Olm/Megolm out of v1** (per-room `message`+`unknown` / `unsupported`) | n/a |
| **Directory** | contacts + groups + HistorySync headers | guilds/channels bot is in | conversations token can see | `/me/chats` + joined teams | chats the bot has seen | dialogs + contacts | joined rooms + `m.direct` | rooms this `me` is in; find/join/create |
| **Diff** | 3 (shipped) | **2** | **3** | **4** | **1** | **4** | **3** (5 with crypto) | **2** |

### 21.1 Gaps — degraded behavior (not “unsupported, ignore”)

| Gap | Advertisement | Client-visible behavior |
|---|---|---|
| WhatsApp: one media per send | `attachments: "single"` | Extra blob parts → `unsupported` (`capability: "attachments"`). Caption = optional `text` part. Inbound album = N `kind: message` events (no coalescing). |
| Discord: no user companion | `identity: "bot"` | `me` is the bot. Directory is guilds/channels the bot joined, not the human’s DMs. |
| Discord: no read receipts | `read: "none"`, `ack: false` | `messages.read` → **`unsupported`** (`capability: "read"`). No `ack` events. Client hides “mark read” / ticks. |
| Slack: reply is a thread | `reply: "context"` | Send `reply` **and** `context: e.context ?? e.id`. Inbound thread replies carry `context`. UI groups by `context`; MUST NOT draw a quote bubble. |
| Slack: no per-message ticks | `read: "cursor"`, `ack: false` | `messages.read` **succeeds** via `conversations.mark`. No `ack` events. |
| Teams: Graph `notificationUrl` is public HTTPS | hosted ingress §2.5 (not a capability) | Client sees ordinary `event`s. Host answers Graph validation; bridge maps native payloads. No ingress → implementer delta-poll, **still** `event` notifications on stdout. First snapshot is **not** live. |
| Teams: admin consent | `identity` + auth events | Application profile uses `token_required` and will `pair_error` until an admin consents. Delegated device-code is the default. |
| Teams: mark-read | `read: "conversation"` or `"none"` | Chat: whole-chat mark (ignores extra ids). Channel or app identity: `unsupported`. |
| Telegram Bot vs user | `profile: "bot"` \| `"user"` | Bot cannot see the human’s other chats. User profile is a different store/identity. |
| Telegram Bot: no read receipts | `read: "none"` | `messages.read` → `unsupported`. |
| Telegram Bot: 1:1 inbound reactions | `react: true` (send works) | Inbound `kind: reaction` may never arrive in private chats. Client must tolerate silence. |
| Web PubSub: claimed `me` | `me: "claimed"` | Pass `me` on `initialize` / `session.pair`. Omit → `me_required`. |
| Web PubSub: no registrar | `membership: "create"` | `directory.find` / `join` / `leave` / `create`. Subscribe is not join. |
| Web PubSub: no quotes/reacts/receipts | `reply: "none"`, `react: false`, `read: "none"`, `ack: false` | Those RPCs → `unsupported`. |
| Web PubSub: one blob + caption | `attachments: "single"`, `files: true` | Extra blob parts → `unsupported`. Caption = optional `text` part (adapter stores it on the blob). |
| Matrix E2EE rooms | (no capability; per-room) | Inbound ciphertext → `kind: message` + `unknown` `label: "encrypted"`. Send/read/react → `unsupported` (no `error.data.capability`). Room still listed. Plaintext rooms in the same session work. |
| All: no history API | (v1 scope) | Late subscribe / restart: no replay. Initial `/sync` / delta / HistorySync bodies are not events. |

### 21.2 Suggested implementation order

1. **Telegram Bot** (diff 1) — prove the common envelope on a second product with `getUpdates`.
2. **Web PubSub Chat** (diff 2) — claimed `me`, `membership: "create"`, `read: "none"` / `reply: "none"`. Canon: PUBSUBBOX.md.
3. **Discord Bot** (diff 2) — Gateway push, bot identity, `read: "none"` error path.
4. **Matrix unencrypted** (diff 3) — `/sync` cursor discipline, `ack: read`, E2EE degradation.
5. **Slack Bot + Socket Mode** (diff 3) — `reply: "context"`, inbound `context`, `read: "cursor"`.
6. **Telegram MTProto user** (diff 4) — user-companion parity with WhatsApp.
7. **Teams Graph** (diff 4) — hosted ingress + Entra + dual chat/channel. Delta-poll only as bring-up fallback.

---

## 22. Implementer notes (not the client contract)

Facts an implementer should not rediscover. Not visible to the JSON-RPC client except as capabilities / errors already specified.

### 22.1 Hosted ingress

- One hosted function MAY serve many local bridges: route on a store/session id in the bus topic / SignalR group / Service Bus session.
- Verify Slack signing secret, Graph clientState, Telegram secret token **on the host**. Do not forward unverified bodies.
- Graph `includeResourceData` needs an encryption certificate on the host; the bridge then receives plaintext `chatMessage` JSON, not CMS blobs.
- Subscribe-set filtering belongs in the **bridge** (the host often cannot: Graph application `getAllMessages` is tenant-wide). Updating a host-side filter when `subscribe` changes is optional implementer IPC, not a Box RPC.
- Dedupe by native message id: Graph, Slack, and Service Bus are at-least-once.
- Do not write client `files` from the host. Download in the process that owns `initialize.files`.

### 22.2 Discord

- `Authorization: Bot {token}`. Never a user token.
- `MESSAGE_CONTENT` is privileged; without it, `MESSAGE_CREATE` has empty `content` (map to an `unknown` part or empty `text` — prefer empty `text` only when blob parts still classify the message).
- Threads are channels; v1 treats a thread id as its own `group` topic. Do not invent a thread RPC. Do not put that id in `context` on the parent.
- No official read-receipt API for bots. Userdoccers `POST /channels/{id}/messages/{id}/ack` is a **user** read-state endpoint; using it with a bot token is not an official companion feature and MUST NOT be mapped to `messages.read`.

### 22.3 Slack

- Socket Mode: ack `envelope_id` even when dropping for subscribe filters, or Slack retries.
- `chat.postMessage` `thread_ts` starts or continues a thread; it does not attach a quote. Map `thread_ts` ↔ `context`. `reply_broadcast` is out of v1. Prefer client-supplied `context` over `reply.id` so a reply to a child does not become a new root.
- `conversations.mark` requires membership; bot marks the **bot’s** cursor, which users do not see as ticks.

### 22.4 Teams

- Resource data in change notifications is encrypted with a certificate you provision on the **host**. The local process should see decrypted `chatMessage` JSON (or notification-without-data + a Graph GET).
- `markChatReadForUser` is **beta** and delegated. Treat 4xx as `unsupported` rather than looping.
- Throttling is aggressive; honor `Retry-After`. Overflow still applies to the in-memory per-topic queue after an event is mapped.
- Delta poll is an implementer fallback when `ingress` is missing from the store — not a client-visible mode.

### 22.5 Telegram

- Bot API `getUpdates` and webhooks are mutually exclusive; with ingress, call `setWebhook`, otherwise `getUpdates`.
- Bot privacy mode in groups is a product setting, not a protocol switch.
- MTProto: persist auth key; a second process with the same key is a second session (`store_locked` still applies locally). Ingress is irrelevant (updates are already a local socket).

### 22.6 Matrix

- Store `next_batch` even when offline so a crash does not re-emit a timeline.
- `m.relates_to` for replies/reactions is plaintext even in encrypted rooms (server aggregations); v1 still does not decrypt bodies.
- Media: authenticated media (`/_matrix/client/v1/media/download/…`) on modern homeservers; fall back to `/_matrix/media/v3/download/…`.

### 22.7 Stricter agnostic Reply (always send `context`)

§4.5 / §12 say: send `context: e.context ?? e.id` only when `capabilities.reply` is `"context"`. That is one branch on the send path.

**Even stricter variant:** on every Reply, always send

```json
{"to":"…","contents":[{"type":"text","text":"…"}],"reply":{"id":"e.id","by":"e.by","text":"e.text?"},"context":"e.context ?? e.id"}
```

and let `reply: "quote"` daemons **ignore** `context` (§4.5 already requires that). Then the JSON-RPC send path has **zero** capability branch — same shape as copying `by`. A WhatsBox `0.1` codec that omits `context` still works (field is optional).

Keep advertising `"context"` anyway: the **UI** still needs to know that the first reply on a message with no inbound `context` will **create a group**, not a quote. Display grouping stays data-driven (bucket if any event has `context`). Do not drop the capability just because send no longer reads it.

Recommended for new unifying clients. Gateways MUST still ignore unknown `context` when `reply` is `"quote"`, and MUST prefer outbound `context` over `reply.id` when `reply` is `"context"`.

### 22.8 Web PubSub Chat

Blob overlay, Entra/`az-cli` cache, hub `token_required`, and Chat invoke whitelist: [`docs/PUBSUBBOX.md`](PUBSUBBOX.md). Do not put Azure URLs or `content.binary` on JSON-RPC.

---

## 23. Key trade-offs

| Choice | Rejected | Why |
|---|---|---|
| One method table for every product | Per-product RPCs / MCP tools named after REST | A WhatsBox 0.1 codec must work. Gaps are capabilities + tokens. |
| Opaque topics / `by` | Typed JIDs, snowflakes, MXIDs on the client | Client copies strings. Directory `kind` is the only enum. |
| Subscribe canonical-only | Alias / name subscribe | Roster search is `directory.list`. Live join lookup is `directory.find`. Ambiguous names need a picker. |
| Claimed `me` is pair input | `$session` `me_required` event / file watch | QR completes off-RPC; a nickname cannot. Error token `me_required`. |
| `membership` `none` \| `join` \| `create` | Subscribe-as-join, or a `create` boolean | Join is product membership. Create ⊃ join. WhatsApp stays `"none"`. |
| Web PubSub omits `profile` | `profile: "chat"` | Omitted ≡ Chat hub. A later base hub would set `profile: "hub"`. |
| Store-based blobs | Base64 / multipart on JSON-RPC | Same-machine paths. No `files` ⇒ text-only. |
| NDJSON, no batches, stderr = logs | LSP headers, JSON-RPC arrays, logs on stdout | WhatsBox 0.1 dialect. |
| No `live` capability | `live: "push"` (constant) or `"poll"` | Inbound is always `event` notifications. A one-value capability that clients must ignore is not a capability. Fetch (WS / long-poll / ingress / delta) stays behind the daemon. |
| Hosted ingress behind the daemon | New webhook RPCs, or advertise `"poll"` | Graph/Slack/Telegram HTTP need a public URL. The hop is implementer-only (store config). Mapping stays in the bridge. Delta poll remains an air-gapped fallback. |
| Discord bot only | User self-bot “companion” | Forbidden. Advertise `identity: bot`. |
| Slack `reply: "context"` + opaque `context` | Pretend quotes, or `unsupported`, or `thread_ts` only on send | Client can group without product types. Copy `e.context ?? e.id` on Reply. Quotes stay `reply`; buckets stay `context`. |
| No `e2ee` capability | Process-wide `e2ee: true`/`false` | WhatsApp Signal is inside the library. Matrix encryption is **per-room**; a session flag would hide send on plaintext rooms too. Ciphertext degrades as `unknown` + `unsupported`. |
| Telegram bot **and** user profiles | One “telegram” that pretends to be both | Bot API cannot see user dialogs; MTProto cannot use a BotFather token as a user. |
| `unsupported` instead of silent no-op | Return success and do nothing | Client would show fake ticks / fake reads. |
| `reply` / `read` string enums; `false` is not `"none"` | Accept JSON `false` as a synonym | Mixing booleans with strings is what the enums removed. Two-state keys stay boolean (`react`, `ack`, `files`). |
| No history / search / backfill RPCs | `messages.history` to “fix” poll/sync snapshots | Product remains a bus. Cursors are an implementation detail. |

---

## 24. Relationship to WhatsBox

This document is **Inbox Client Protocol (ICP)** (also the managed `Inbox` client). [`docs/WHATSBOX.md`](WHATSBOX.md) is the **WhatsApp profile** for the native `whatsbox` adapter. [`docs/PUBSUBBOX.md`](PUBSUBBOX.md) is the **Azure Web PubSub Chat profile** for `pubsubbox`. Neither restates the method table, event envelope, or error tokens.

A `whatsbox` binary is a conformant ICP implementation when it speaks this envelope (including `product`, `identity`, and `capabilities` on `initialize` / `session.status`) and maps WhatsApp as in WHATSBOX.md + RFC-1. A `pubsubbox` binary is conformant when it maps Chat as in PUBSUBBOX.md (claimed `me`, `membership: "create"`, files overlay).

Wire version stays **`"0.1"`** so one codec spans the family.

