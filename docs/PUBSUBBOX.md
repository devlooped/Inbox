# PubSubBox

PubSubBox is the **Azure Web PubSub Chat adapter** for Inbox Client Protocol (ICP): binary `pubsubbox`.

**Status:** v0.1 (not shipped)  
**License (product):** MIT  
**Chat API:** [Web PubSub chat](https://learn.microsoft.com/azure/azure-web-pubsub/chat-overview) (`2026-02-01-preview`)

`pubsubbox` owns one Chat hub session (one claimed `userId`) and exposes it as JSON-RPC 2.0 NDJSON on stdio. It is **not** ICP, **not** the GrokBox appliance, and it does **not** bind HTTP or mDNS.

Wire, methods, events, files, errors, client-once rules, and capabilities are specified in **INBOX.md** §20. This document is the Chat mapping: Entra/`az-cli`, hub selection, room ids, the blob overlay, and invoke names that are **not** implemented. It does **not** restate the method table.

Hosted ingress in INBOX.md §2.5 (Azure Web PubSub as a **bus** for Graph/Slack webhooks) is a different use of the same Azure product. This adapter is a **Chat hub participant**, not that bus.

---

## 1. Product

### 1.1 What it is

A locked Chat hub companion that is:

1. An **address book** (rooms this `me` belongs to; find/join/create when advertised).
2. A **live pub/sub** of chats the client asked for.
3. A **same-machine blob channel** (paths on disk; Chat stores a URL, not bytes).

One binary. One process. One store. One Chat `userId`. One hub.

### 1.2 Who it is for

The same ICP **client** as WhatsBox (`InboxClient`, a REPL, an appliance). Pairing UX (device code, claimed name, hub file) is the client’s job. The box does not host `grokbox.local`.

### 1.3 v1 does

- Pair via Entra device code (`az-cli`) and/or a token already in the store.
- Claim `me` (Chat `userId`) on `initialize` / `session.pair`.
- Connect, auto-reconnect, disconnect, logout.
- Directory populate + list/get + live `$directory`.
- `directory.find` / `join` / `leave` / `create` (`membership: "create"`).
- Subscribe by canonical room topic (roster row required).
- Send text; send one blob + optional caption via the storage overlay.
- Receive live `kind: message` / `meta` (membership).

### 1.4 v1 does not

- Quotes, reactions, mark-read, acks (`chat.createMessage` / `markRead` / `react` are `UnsupportedOperation` on the service).
- Message history as live events (Chat history exists; ICP v1 does not emit it except short reconnect catch-up on **current** subscriptions).
- Base-hub groups/connections (`profile: "hub"` — later; omit `profile` ≡ this Chat surface).
- HTTP operator UI, Bonjour, ARM picker RPCs.
- SAS blob URLs (public blobs in v1).
- More than one blob part per send.

---

## 2. Chat profile (on the wire)

| Field | Value |
|---|---|
| `product` | `webpubsub` |
| `identity` | `user` |
| `profile` | **omit** (≡ Chat hub) |
| `capabilities.auth` | `["device_code", "token"]` |
| `me` | `"claimed"` |
| `membership` | `"create"` |
| `reply` | `"none"` |
| `react` | `false` |
| `read` | `"none"` |
| `ack` | `false` |
| `files` | `true` |
| `attachments` | `"single"` |

Advertise this object on `initialize` / `session.status`. Never emit `context`. Outbound `context` / `reply` / `reaction` parts → `unsupported` as INBOX.md requires.

---

## 3. Process and store

Invocation is INBOX.md §2.1 (`pubsubbox [--store ABSOLUTE_PATH]`).

| Path | Contents |
|---|---|
| `<store>/LOCK` | Exclusive lock |
| `<store>/az/` | `az-cli` token cache (Entra). **Not** `~/.azure`. Logout wipes it. |
| `<store>/credentials.json` | Hub name, optional client access URL, optional connection string / Entra app settings. Never on JSON-RPC. |
| `<store>/directory.db` | Roster only. No messages. |

`session.logout` deletes Entra cache, credentials identity, directory, subscriptions. The Chat service is **not** told to delete rooms.

---

## 4. Pairing

### 4.1 Claimed `me`

Required on `initialize` and/or `session.pair`. Omit → `me_required` immediately (no `$session` event). Issued products passing `me` is N/A here. `me` is the Chat `userId` (opaque string the token will stamp). Not the Entra UPN, not `deviceName`.

Initialize `{me:"alice", connect:false}` remembers `alice` for the later pair. Both set and different → `invalid_params`.

### 4.2 Entra device code

Inside `pair({me})` after `me` is known:

1. `az-cli` `StartDeviceCode` (implementation PackageReference; not an ICP adapter).
2. Emit `$session` `device_code` `{user_code, verification_uri, expires_in, interval}`.
3. Poll until token or `pair_error`.

Scopes: ARM as needed to list Web PubSub / Chat hubs; Chat data plane as required to mint a client access URL.

If `credentials.json` already has a usable **client access URL** for this `me` + hub, skip device code (`auth` includes `"token"`).

### 4.3 Hub / resource

Store-only. After Entra:

- Exactly one Chat hub on the reachable resource → write it and continue.
- Zero → `pair_error`.
- Many, and no `hub` in the store → `$session` `token_required` `{path:"credentials.json", hint:"hub"}` and wait (existing file-watch). The client (appliance, REPL) writes `{"hub":"…"}`. No `hub` param on initialize.

Mint the Chat client access URL with `userId = me`. Then Chat WebSocket login → `paired` `{me}` → `online`.

---

## 5. Topics

| Entity | Canonical topic | `kind` |
|---|---|---|
| Room | Chat `roomId` (1–64: letters, digits, `_`, `-`) | `group` |
| User (find only) | Chat `userId` | `user` |
| 1:1 after join | The DM **room** id | `user` or `group` per Chat’s shape; roster key is the **room** |

`directory.find` `{kind:"user"}` may return `{topic: userId, kind: "user"}`. `directory.join` of that topic opens or reuses the 1:1 room, then `$session` `remap` `{from: userId, to: roomId}`. Send/subscribe after that use `roomId`.

`directory.create` `{name, topic?}` → Chat `createRoom`. Same `me` + same `topic` → no-op. Other occupant → `topic_taken`. Always a group.

`directory.join` / `leave` `{id}`: canonical only. Already a member → no-op. Leave of a non-member → `not_found`. Leave drops a held subscription.

Subscribe without a roster row → `not_found`. Join does not subscribe.

---

## 6. Live path

Chat client WebSocket after login. Filter by the current subscribe set. Unsubscribed rooms: protocol-ack if required, then drop; no download.

First login / history pages are **not** live (INBOX.md §7.4). Persist whatever cursor Chat gives; emit only subsequent events. Reliable reconnect may fill **current** subscriptions (catch-up), not a history API.

Membership events → chat `kind: meta` (`join` / `leave`) when already subscribed; `$directory` upsert/remove always as INBOX.md §7.2.

No `ack` events. No `kind: reaction`.

---

## 7. Files overlay

Chat has **no** upload API (`chat.upload`, `chat.getUploadUrl`, `CreateMessage` External → `UnsupportedOperation`). `content.text` and `content.binary` **cannot coexist** (one table `Body` + `BodyType`). Message content cap is 64 KB.

**Convention (adapter-internal; not on JSON-RPC):**

1. Upload the file to the **same storage account** bound to the Chat hub (v1: public container, `publicAccess: blob`).
2. Set blob `Content-Type` from the file; `Content-Disposition` filename when known; **one** metadata key `x-ms-meta-text` = the ICP caption (UTF-8). Azure allows many `x-ms-meta-*` keys; this profile uses **only** `text` so caption round-trips in one header.
3. Chat message: `content.binary = base64(utf8(blobUrl))`. No `content.text`. A raw URL in `binary` is **400**.
4. Text-only send: `content.text`, `BodyType` text. No blob.

**Send (ICP → Chat):**

| ICP `contents` | Chat |
|---|---|
| `[{type:text}]` | `content.text` |
| one blob part, no text | upload; `binary` = base64(url); no `x-ms-meta-text` |
| text + one blob | upload; `x-ms-meta-text` = text; `binary` = base64(url) |
| two blob parts | `unsupported` (`attachments`) |
| blob without `initialize.files` | `files_required` |

Download inbound **immediately** into `{files}/in/{safeTopic}/{id}[.ext]`, then emit the event (SAS later must not race). Public URL in v1; the client still uses the local path after notify.

**Receive (Chat → ICP):**

1. If `content.text` set and no usable `binary` → `[{type:text}]`.
2. If `binary` decodes as UTF-8 `https://…` URL:
   - HEAD (need `x-ms-version` for metadata).
   - Map `Content-Type` → part `type`: `image/*` → `image` (optional: `image/webp` → `sticker`), `video/*` → `video`, `audio/*` → `audio`, else `document`.
   - GET into `files`; `path` on the part.
   - If `x-ms-meta-text` present → also a `text` part (caption).
3. Else `binary` that is not a URL → `unknown` (no blob), or write raw bytes as `document` if it is clearly a small in-table payload. Prefer not to pretend in-table bytes are files.
4. Failed download → part with `error`, no `path`.

Unsubscribed inbound: never download (INBOX.md §9).

v1 does **not** use SAS. A later profile may. Do not put the Azure URL on the JSON-RPC event.

---

## 8. Chat invoke whitelist (live `grokbox` hub)

Implemented: `chat.login`, `chat.createRoom`, `chat.getRoom`, `chat.sendTextMessage`, `chat.queryMessageHistory`, member add/remove as the SDK exposes.

**Not implemented** (`UnsupportedOperation` — same as a made-up `chat.*` name): `chat.createMessage`, `chat.sendMessage`, `chat.markRead`, `chat.react`, `chat.upload`, `chat.getUploadUrl`, and the rest of the upload/attachment family.

Do not advertise capabilities that require those invokes. REST PATCH can set `content.binary`; that is an adapter implementation detail for the overlay, not a client RPC.

---

## 9. Errors (adapter)

All INBOX.md tokens. Additionally:

| Situation | Token |
|---|---|
| Claimed pair, no `me` | `me_required` |
| Create topic held by someone else | `topic_taken` |
| Find/join/leave/create while not `online` | `disconnected` |
| Reply / react / read | `unsupported` + `error.data.capability` |

`az-cli` failures during pair → `pair_error`.

---

## 10. What stays off the wire

Azure resource ids, connection strings, `content.binary`, blob URLs, `x-ms-meta-text`, ARM, `az-cli`, hub names (except via `token_required` hint + store file). The JSON-RPC client copies opaque topics and `me`, renders `device_code` / `token_required`, and calls find/join/create as INBOX.md.

---

## 11. Difficulty

**2 / 5.** Second-product proof for claimed `me` and membership verbs. Pairing is Entra + hub file, not QR. Files are a storage convention, not a Chat feature.
