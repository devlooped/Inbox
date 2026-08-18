# whatsbox

[![Release](https://img.shields.io/github/v/release/devlooped/whatsbox?include_prereleases&color=darkmagenta)](https://github.com/devlooped/whatsbox/releases)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/devlooped/oss/blob/main/license.txt)

<!-- #content -->
`whatsbox` is a local WhatsApp companion for agents and apps. One process
owns a linked-device session and speaks **JSON-RPC 2.0 over stdio**: you
subscribe to chats, send a small set of actions, and receive live events.

It is an **address book**, a **live pub/sub** of the chats you asked for,
and a **same-machine file channel** (paths on disk, never bytes on the
wire). It is not an archive, a search engine, or a WhatsApp CLI — there
is no transcript, no history search, and no one typing commands.

If you can spawn a process and read newline-delimited JSON, you can drive
WhatsApp. Pairing QR codes are events you render; the parent process *is*
the session.

## Install

With [Go](https://go.dev/dl/) 1.25+:

```bash
go install github.com/devlooped/whatsbox/cmd/whatsbox@latest
```

## Usage

```text
whatsbox [--store ABSOLUTE_PATH] [--version] [--help]
```

JSON-RPC on **stdin** / **stdout**. **stderr** is logs only — never treat
it as protocol or as “the command failed.” `--version` and `--help` print
and exit with no RPC.

There is **no default store**. Pass an absolute path with `--store` and/or
`initialize.store`. One process, one store, one WhatsApp socket. A second
process on the same store fails with `store_locked`.

```text
spawn → initialize [connect:true ⇒ implicit connect]
      → (else session.connect [+ implicit pair]) → events
stdin EOF → disconnect WhatsApp → exit
```

Pairing keys stay on disk until `session.logout`. Messages missed while
the process is down are gone (at-most-once). A short WhatsApp offline
catch-up on the next `connect` applies only to **current** subscriptions.

### Talk to it

One compact JSON object per line (NDJSON). Protocol version is `"0.1"`.

```json
{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"version":"0.1","store":"/data/whatsbox","connect":true}}
```

```json
{"jsonrpc":"2.0","id":"1","result":{"version":"0.1","status":"online","me":"111@lid","topics":["$session"]}}
```

```json
{"jsonrpc":"2.0","id":"1","error":{"code":-32003,"message":"store_required"}}
```

Live traffic is always a notification named `event`. `params.topic` is
always present; `params.kind` discriminates the payload.

```json
{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@..."}}
```

`initialize` must be the first RPC. Nothing is emitted before it succeeds.

### Pair and connect

`session.status` is exactly one of `new` | `offline` | `online`. `me` is
your LID and is omitted only when `new`.

| Method | What it does |
| --- | --- |
| `session.connect` | Bring up the socket. `new` ⇒ implicit QR pair. `online` is a no-op. Then auto-reconnect until disconnect, logout, remote `logged_out`, or stdin EOF. |
| `session.pair` | Show QR and wait. No-op if already linked — logout first to re-pair. Passkey/WebAuthn is `pair_error` (not implemented). |
| `session.disconnect` | Drop the socket. Process stays. Status `offline` if still paired. |
| `session.logout` | Unlink if connected, wipe local identity and directory, clear subscriptions, status `new`. |
| `session.status` | `{ me?, status, topics }` |

QR codes arrive on `$session` as `{kind:"qr", code}` while WhatsApp
rotates them. Render the latest. Success is `{kind:"paired"}` then
`online`. `initialize` with `connect: true` is `session.connect` after
subscriptions are installed (same rules, one round-trip). The call waits
as long as pairing would.

The linked device stays **quiet**: no “available” presence, no typing
indicators. That is intended — a linked device that looks online steals
phone notifications.

### Topics

| Entity | Canonical topic |
| --- | --- |
| 1:1 user | LID JID, e.g. `123456789012345@lid` |
| Group | `120363…@g.us` |
| System | `$session` (always on), `$directory` |

Phone-number JIDs (`1555…@s.whatsapp.net`) are a **label** (`pn`), like a
display name. They are not topics once a LID is known.

`subscribe` / `unsubscribe`, `messages.send.to`, `messages.read.to`, and
`directory.get` accept a LID, a PN JID, or a phone (`+15551234567` or
digits). Results and `topic` on the wire are always the canonical LID or
group JID. Unknown phone is an error — no ghost topic. Groups cannot be
addressed by phone.

`$session` cannot be unsubscribed. Any other `$…` topic is
`invalid_topic`. Subscribe is atomic: one bad entry fails the whole call.
Late subscribe has no replay.

When a LID↔PN mapping appears later, the subscription moves and `$session`
emits `{kind:"remap", from, to}`. Further chat events use the LID.

### Directory

`directory.list` is a local address book (name / `pn` / JID, optional
`kind` `user`|`group`, `limit`, opaque `cursor`). There is no
sort-by-last-message — bodies are not stored.

`directory.get` returns one row. Groups include `participants`. With
`files` set, a preview icon is written and `icon` is a relative path.

After `online`, the directory fills in the background (`$directory`
`upsert` / `remove`, then `ready`). That does not block `connect`.

```json
{"topic":"999@lid","kind":"user","name":"Ada","pn":"15551234567@s.whatsapp.net","muted":false,"pinned":false,"archived":false}
```

### Messages

```json
{"jsonrpc":"2.0","id":"2","method":"messages.send","params":{"to":"+15551234567","text":"hello"}}
```

```json
{"jsonrpc":"2.0","id":"2","result":{"id":"3EB0…","topic":"999@lid"}}
```

At least one of `text`, `path`, `react`. `reply` / `react` need `id` and
`by` (copy `by` from the inbound event; `"me"` for your own message).
Empty react emoji removes the reaction. If you are subscribed to that
topic, a normal inbound-shaped `event` with `by:"me"` is also emitted.

`messages.read` sends blue ticks. Never automatic. `ids` required. `by`
is required on groups (all ids in the call share that author) and must be
omitted on 1:1.

Unsubscribed chats are acknowledged to WhatsApp and dropped — no event,
no download.

### Events

Every notification:

```json
{"jsonrpc":"2.0","method":"event","params":{"topic":"…","kind":"…"}}
```

**`$session`** (always subscribed)

| `kind` | When |
| --- | --- |
| `qr` | Pairing — `code` to render |
| `paired` | Pair success — `me` |
| `pair_error` | Pair failed / passkey required |
| `online` | Socket up (including after reconnect) |
| `offline` | Socket down; will reconnect unless disconnect / logout / EOF |
| `logged_out` | Session revoked remotely; local identity is wiped, status `new` |
| `remap` | Topic moved PN → LID — `from`, `to` |
| `overflow` | Per-topic queue dropped oldest — `topic`, `dropped` |

**`$directory`**

| `kind` | Payload |
| --- | --- |
| `upsert` | Directory row (no `participants` — use `directory.get` for members) |
| `remove` | Canonical topic |
| `ready` | `{generated: n}` after the first populate wave |

**Chat topics** (bare JID)

| `kind` | Meaning |
| --- | --- |
| `text` | Body in `text` |
| `image` `video` `audio` `document` `sticker` | Media; `path` if `files` is set and download succeeded |
| `location` | `lat`, `lng` (optional `name` / `address`) |
| `reaction` | `emoji`, `target` (id), `by` |
| `ack` | `ids`, `ack` = `delivered` \| `read` \| `played` |
| `meta` | Room notice: `join` / `leave` / `promote` / `demote` / `rename` / `topic` / `icon` / … |
| `unknown` | Everything else (polls, view-once, buttons, …). No blob. |

```json
{"topic":"120363…@g.us","kind":"text","id":"3EB0…","by":"999@lid","pn":"1555…@s.whatsapp.net","text":"hi"}
```

`by` is `"me"` or a LID. Live events are at-most-once, in arrival order
per topic.

### Files

Optional. Set `initialize.files` to an absolute directory the process may
read and write. Without it: text-only — no inbound download, no icons,
`path` on send is `files_required`.

| Direction | Path | Who writes |
| --- | --- | --- |
| Inbound media | `{files}/in/{safeTopic}/{id}[.ext]` | whatsbox, after download, then the `event` |
| Icon from `directory.get` | `{files}/in/_dir/{safeTopic}[.ext]` | whatsbox, then `icon` on the result |
| Outbound | any file **under** `{files}` | you; the RPC carries a **relative** path |

whatsbox never deletes files. View-once and `unknown` are never written.
Paths that escape `files` are `path_escape`.

### Methods

| Method | In | Out |
| --- | --- | --- |
| `initialize` | `version`, `store?`, `files?`, `subscribe?`, `verbosity?`, `connect?` | status snapshot (`connect:true` ⇒ `session.connect`) |
| `session.connect` | — | status (`new` ⇒ implicit pair) |
| `session.pair` | — | status (no-op if already linked) |
| `session.disconnect` | — | status |
| `session.logout` | — | status `new` |
| `session.status` | — | `{me?, status, topics}` |
| `subscribe` | `{topics}` | `{topics}` canonical |
| `unsubscribe` | `{topics}` | `{topics}` remaining |
| `directory.list` | `{query?, kind?, limit?, cursor?}` | `{items, cursor?}` |
| `directory.get` | `{id}` | directory row (+ `participants`, + `icon` if `files`) |
| `messages.send` | `{to, text?, path?, reply?, react?}` | `{id, topic}` |
| `messages.read` | `{to, ids, by?}` | `{topic}` |

`initialize.store` vs `--store`: either is enough; both must be the same
absolute path or you get `store_mismatch`. `verbosity` is stderr only:
`error` | `warn` | `info` (default after init) | `debug`.

### Errors

Application errors use codes in `-32000…-32099` (or the stable `message`
token):

| Token | Meaning |
| --- | --- |
| `not_initialized` | RPC before `initialize` |
| `already_initialized` | Second `initialize` |
| `store_required` | No `--store` and no `initialize.store` |
| `store_mismatch` | Both set and not the same path |
| `store_locked` | Another process holds the store |
| `unsupported_version` | `version` is not `"0.1"` |
| `pair_error` | QR pairing failed / passkey required |
| `not_found` | Missing directory entry / unknown phone |
| `invalid_topic` | `$` reserved, bad JID, unsubscribe `$session` |
| `files_required` | Blob op without `files` |
| `path_escape` | `path` outside `files` |
| `invalid_params` | Missing `by` on group read / reply / react, etc. |
| `disconnected` | Needs `online` (send, read, live phone lookup) |

### v1 does not

Message history, search, backfill, or stored bodies. Typing or “available”
presence. Edit / revoke. Pair-code or passkey pairing. Channels, status
broadcasts, calls, blocklist or group-admin RPCs. MCP / ACP / A2A as the
native protocol. A Unix/TCP socket (stdio only). Multi-account in one
process. Topic wildcards. A default store path.

The full client contract is [SPEC.md](SPEC.md).

<!-- #content -->
---
<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
# Sponsors

<!-- sponsors.md -->
[![Clarius Org](https://avatars.githubusercontent.com/u/71888636?v=4&s=39 "Clarius Org")](https://github.com/clarius)
[![MFB Technologies, Inc.](https://avatars.githubusercontent.com/u/87181630?v=4&s=39 "MFB Technologies, Inc.")](https://github.com/MFB-Technologies-Inc)
[![SandRock](https://avatars.githubusercontent.com/u/321868?u=99e50a714276c43ae820632f1da88cb71632ec97&v=4&s=39 "SandRock")](https://github.com/sandrock)
[![DRIVE.NET, Inc.](https://avatars.githubusercontent.com/u/15047123?v=4&s=39 "DRIVE.NET, Inc.")](https://github.com/drivenet)
[![Keith Pickford](https://avatars.githubusercontent.com/u/16598898?u=64416b80caf7092a885f60bb31612270bffc9598&v=4&s=39 "Keith Pickford")](https://github.com/Keflon)
[![Thomas Bolon](https://avatars.githubusercontent.com/u/127185?u=7f50babfc888675e37feb80851a4e9708f573386&v=4&s=39 "Thomas Bolon")](https://github.com/tbolon)
[![Kori Francis](https://avatars.githubusercontent.com/u/67574?u=3991fb983e1c399edf39aebc00a9f9cd425703bd&v=4&s=39 "Kori Francis")](https://github.com/kfrancis)
[![Reuben Swartz](https://avatars.githubusercontent.com/u/724704?u=2076fe336f9f6ad678009f1595cbea434b0c5a41&v=4&s=39 "Reuben Swartz")](https://github.com/rbnswartz)
[![Jacob Foshee](https://avatars.githubusercontent.com/u/480334?v=4&s=39 "Jacob Foshee")](https://github.com/jfoshee)
[![](https://avatars.githubusercontent.com/u/33566379?u=bf62e2b46435a267fa246a64537870fd2449410f&v=4&s=39 "")](https://github.com/Mrxx99)
[![Eric Johnson](https://avatars.githubusercontent.com/u/26369281?u=41b560c2bc493149b32d384b960e0948c78767ab&v=4&s=39 "Eric Johnson")](https://github.com/eajhnsn1)
[![Jonathan ](https://avatars.githubusercontent.com/u/5510103?u=98dcfbef3f32de629d30f1f418a095bf09e14891&v=4&s=39 "Jonathan ")](https://github.com/Jonathan-Hickey)
[![Ken Bonny](https://avatars.githubusercontent.com/u/6417376?u=569af445b6f387917029ffb5129e9cf9f6f68421&v=4&s=39 "Ken Bonny")](https://github.com/KenBonny)
[![Simon Cropp](https://avatars.githubusercontent.com/u/122666?v=4&s=39 "Simon Cropp")](https://github.com/SimonCropp)
[![agileworks-eu](https://avatars.githubusercontent.com/u/5989304?v=4&s=39 "agileworks-eu")](https://github.com/agileworks-eu)
[![Zheyu Shen](https://avatars.githubusercontent.com/u/4067473?v=4&s=39 "Zheyu Shen")](https://github.com/arsdragonfly)
[![Vezel](https://avatars.githubusercontent.com/u/87844133?v=4&s=39 "Vezel")](https://github.com/vezel-dev)
[![ChilliCream](https://avatars.githubusercontent.com/u/16239022?v=4&s=39 "ChilliCream")](https://github.com/ChilliCream)
[![4OTC](https://avatars.githubusercontent.com/u/68428092?v=4&s=39 "4OTC")](https://github.com/4OTC)
[![domischell](https://avatars.githubusercontent.com/u/66068846?u=0a5c5e2e7d90f15ea657bc660f175605935c5bea&v=4&s=39 "domischell")](https://github.com/DominicSchell)
[![Adrian Alonso](https://avatars.githubusercontent.com/u/2027083?u=129cf516d99f5cb2fd0f4a0787a069f3446b7522&v=4&s=39 "Adrian Alonso")](https://github.com/adalon)
[![torutek](https://avatars.githubusercontent.com/u/33917059?v=4&s=39 "torutek")](https://github.com/torutek)
[![Ryan McCaffery](https://avatars.githubusercontent.com/u/16667079?u=c0daa64bb5c1b572130e05ae2b6f609ecc912d4d&v=4&s=39 "Ryan McCaffery")](https://github.com/mccaffers)
[![Seika Logiciel](https://avatars.githubusercontent.com/u/2564602?v=4&s=39 "Seika Logiciel")](https://github.com/SeikaLogiciel)
[![Andrew Grant](https://avatars.githubusercontent.com/devlooped-user?s=39 "Andrew Grant")](https://github.com/wizardness)
[![eska-gmbh](https://avatars.githubusercontent.com/devlooped-team?s=39 "eska-gmbh")](https://github.com/eska-gmbh)
[![Geodata AS](https://avatars.githubusercontent.com/u/5946299?v=4&s=39 "Geodata AS")](https://github.com/geodata-no)


<!-- sponsors.md -->
[![Sponsor this project](https://avatars.githubusercontent.com/devlooped-sponsor?s=118 "Sponsor this project")](https://github.com/sponsors/devlooped)

[Learn more about GitHub Sponsors](https://github.com/sponsors)

<!-- https://github.com/devlooped/sponsors/raw/main/footer.md -->
