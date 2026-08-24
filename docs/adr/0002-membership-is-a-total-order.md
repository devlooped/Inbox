# Membership is `none` | `join` | `create`, and subscribe is not join

Some products have no join API (WhatsApp — the phone is the registrar). Some can join but not create. Pubsubbox has no external registrar, so ICP must create. One string enum with implication (`create` ⊃ `join` ⊃ roster) advertises that without a boolean soup or an `auth`-style array. `directory.find` / `join` / `leave` / `create` are the verbs; `subscribe` stays live-event intent (RFC-1). Join does not subscribe; leave drops a held subscription because the topic is gone.

**Considered:** `membership: join` plus a separate `create` boolean (can contradict); a membership array (`find`/`join`/`leave`/`create`); overloading `subscribe` or `messages.send` to create chats (ghost topics, RFC-1).
