# Claimed `me` is pair input, not a `$session` wait

QR and device-code complete off-RPC (phone scan, Microsoft’s page). A claimed `me` cannot: the product does not know the name until the client sends it, and JSON-RPC cannot patch an in-flight `pair`. We require `me` on `initialize` / `session.pair` when `capabilities.me` is `claimed`; omit → error token `me_required`. No `me_required` event and no `token_required`-style file watch for a nickname. Issued products that receive `me` must not pretend to honor it.

**Considered:** hang `pair` and inject `me` later; clone `token_required`; a second `session.claim` RPC. All are extra channels for a field the client can already pass, and they fight `connect:true` one-shot.
