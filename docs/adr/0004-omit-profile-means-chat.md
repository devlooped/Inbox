# Pubsubbox omits `profile`; omitted ≡ chat

`product: "webpubsub"` with no `profile` is the Chat hub. A later base-hub adapter on the same product string would advertise `profile: "hub"`. Telegram still uses `profile` when one binary is bot or user. We do not emit `profile: "chat"` just to have a string.
