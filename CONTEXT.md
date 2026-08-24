# Inbox

A same-machine companion bus: one **box** owns one messaging-product session and exposes it as **ICP**. This glossary is the language for that bus, not for any particular consumer (REPL, agent, appliance).

## Language

**ICP**:
Inbox Client Protocol: JSON-RPC 2.0 pub/sub on stdio (NDJSON). The client-facing contract every box speaks.
_Avoid_: IPC, MCP, ACP, A2A, the native product API.

**Box**:
One process, one store, one product session. Speaks only ICP on stdin/stdout.
_Avoid_: daemon-as-product, gateway, service, adapter-as-the-protocol.

**Adapter**:
A box for a specific product (`whatsbox`, `pubsubbox`). The adapter is not ICP. `pubsubbox` omits `profile`; a later hub surface would set `profile: "hub"` (`profile` omitted ≡ chat).
_Avoid_: calling the managed NuGet host “the protocol.”

**Client**:
Anything that speaks ICP to a box (`InboxClient`, a REPL, an appliance). Product HTTP, Bonjour, and operator chrome belong to the client, not the box.
_Avoid_: treating an appliance image as this repository’s product.

**Me**:
The paired product identity string on the session (`SessionSnapshot.me`, event `by: "me"`). Opaque. Chosen according to **me binding**, never `deviceName`, never the operator’s Entra UPN.
_Avoid_: self, userId-as-a-protocol-type, handle, deviceName.

**Me binding**:
How `me` is bound at pair time. **Issued**: the product supplies `me` after auth (WhatsApp LID, Discord bot snowflake). Passing `me` on initialize/pair → `invalid_params`. **Claimed**: the client supplies `me` as input on `initialize` and/or `session.pair`. Omit when claimed → error token `me_required` (not a `$session` kind). `me` on `initialize` (even with `connect: false`) is remembered for the following pair; both set and different → `invalid_params`.
_Avoid_: asserted, impersonating, self-registering, externally registered (those phrases collapse into issued vs claimed).

**Roster**:
The directory’s set of chats this `me` already belongs to. `directory.list` / `directory.get` read the roster. Create and join write it; find does not.
_Avoid_: address book as a second store, live product search.

**Find**:
Live product lookup of chats this `me` could join. Same params as `directory.list` (`query?`, `kind?`, `limit?`, `cursor?`). Returns rows (canonical `topic` + labels). Does not write the roster. When `membership` is `join` or `create`, `subscribe` of a topic with no roster row is `not_found`.
_Avoid_: overloading `directory.list` query, search-as-join.

**Create**:
`directory.create` `{name, topic?}` — always a group. Product assigns the topic if omitted. Writes the roster; does not subscribe. Result is `{topic}`. Same `me` + same topic → no-op. Another occupant → `topic_taken`. `$` / garbage → `invalid_topic`.
_Avoid_: 1:1 create, participant lists on create, join-or-create.

**Join**:
`directory.join` `{id}` — `id` is a canonical topic from find, create, or the roster. Names are `invalid_topic`. Already a member → no-op `{topic}`. A `kind: "user"` find row opens (or reuses) a 1:1; the result topic may `remap`. Writes the roster; does not subscribe. Leave of a non-member → `not_found`.
_Avoid_: alias join, send-to-create-DM.

**Membership**:
Product-side add/remove of this `me` from a chat. Not subscribe. `capabilities.membership` is `"none"` | `"join"` | `"create"` (total order: `create` ⊃ `join` ⊃ roster). `none`: find/join/leave/create → `unsupported`. `join`: find/join/leave. `create`: those plus `directory.create`. Join writes the roster and does not subscribe. Leave ends product membership, `$directory` `remove`s the row, and drops a held subscription. `unsubscribe` never leaves. Find/join/leave/create are **online-only**.
_Avoid_: using subscribe to join, per-product verbs (`rooms.join`), a second boolean for create.

**Subscribe**:
Client intent to receive live `event`s on a canonical topic. Newly subscribed topics get no replay. Unrelated to product membership.
_Avoid_: watch, join, follow.

**Topic**:
Opaque chat id (or `$session` / `$directory`). The client copies it; it does not parse product suffixes.
_Avoid_: JID, snowflake, roomId as client-visible types.
