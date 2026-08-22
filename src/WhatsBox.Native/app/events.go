package app

import (
	"context"
	"strings"

	"github.com/devlooped/whatsbox/dirstore"
	"github.com/devlooped/whatsbox/files"
	"github.com/devlooped/whatsbox/topic"
	"github.com/devlooped/whatsbox/wa"
)

func (d *Daemon) onWA(ev wa.Event) {
	switch ev.Type {
	case wa.EvtQR:
		d.emit(map[string]any{"topic": topic.Session, "kind": "qr", "code": ev.Code})
	case wa.EvtPaired:
		d.emit(map[string]any{"topic": topic.Session, "kind": "paired", "me": ev.Me})
	case wa.EvtPairError:
		d.emit(map[string]any{"topic": topic.Session, "kind": "pair_error", "message": ev.Message})
	case wa.EvtConnected:
		d.mu.Lock()
		d.status = "online"
		d.mu.Unlock()
	case wa.EvtDisconnected:
		d.mu.Lock()
		armed := d.autoReconnect
		if d.wa != nil && d.wa.IsPaired() {
			d.status = "offline"
		} else if d.status != "new" {
			d.status = "offline"
		}
		d.mu.Unlock()
		reason := ev.Reason
		d.emit(map[string]any{"topic": topic.Session, "kind": "offline", "reason": reason})
		if armed {
			d.startReconnect()
		}
	case wa.EvtLoggedOut:
		d.wipeIdentity(context.Background(), ev.Reason)
	case wa.EvtMapping:
		d.applyMapping(ev.LID, ev.PN)
	case wa.EvtHistory:
		d.ingestHistory(ev.History)
	case wa.EvtContact:
		d.ingestContact(ev.Contact)
	case wa.EvtRemove:
		canon := d.canonicalize(ev.Chat)
		if st := d.store(); st != nil {
			_ = st.Remove(canon)
		}
		if d.bus.Has(canon) {
			d.emit(map[string]any{"topic": canon, "kind": "meta", "action": "leave"})
		}
		d.emitDirectoryRemove(canon)
	case wa.EvtMeta:
		d.handleMeta(ev)
	case wa.EvtReceipt:
		d.handleReceipt(ev)
	case wa.EvtMessage:
		d.handleMessage(ev)
	}
}

func (d *Daemon) handleMessage(ev wa.Event) {
	chat := d.canonicalize(firstNonEmpty(ev.Chat, ev.LID))
	if chat == "" {
		return
	}
	if ev.PN != "" && topic.IsLID(chat) {
		d.applyMapping(chat, ev.PN)
		chat = d.canonicalize(chat)
	}
	// On from-me 1:1 sends, PN is the recipient (RecipientAlt), not the sender.
	// Mapping sender→PN would steal the peer's phone onto our LID.
	if !ev.FromMe && ev.Sender != "" && ev.PN != "" && topic.IsLID(d.canonicalize(ev.Sender)) {
		d.applyMapping(d.canonicalize(ev.Sender), ev.PN)
	}

	if !d.bus.Has(chat) {
		// Protocol-ack (whatsmeow already did) then drop. Warm directory only.
		d.touchDirectoryFromMessage(ev, chat)
		return
	}

	kind := ev.Kind
	if kind == "" {
		kind = "text"
	}

	by := "me"
	if !ev.FromMe {
		by = d.canonicalize(ev.Sender)
		if by == "" || by == d.me() {
			if ev.Sender != "" {
				by = ev.Sender
			}
		}
		if by == d.me() {
			by = "me"
		}
	}

	out := map[string]any{
		"topic": chat,
		"id":    ev.ID,
		"by":    by,
	}
	d.decorateChat(out, chat, by, ev.Name)

	if kind == "reaction" {
		out["kind"] = "reaction"
		out["contents"] = []map[string]any{{
			"type":   "reaction",
			"emoji":  ev.Emoji,
			"target": ev.Target,
		}}
		d.touchDirectoryFromMessage(ev, chat)
		d.emit(out)
		return
	}

	out["kind"] = "message"
	var parts []map[string]any
	if ev.ViewOnce {
		label := ev.Label
		if label == "" {
			label = "view_once"
		}
		parts = []map[string]any{{"type": "unknown", "label": label}}
	} else {
		switch kind {
		case "text":
			parts = []map[string]any{{"type": "text", "text": ev.Text}}
		case "image", "video", "audio", "document", "sticker":
			part := map[string]any{"type": kind}
			fd := d.filesDir()
			if !fd.Enabled() {
				part["error"] = "files_required"
			} else {
				data := ev.Media
				if ev.MediaRef != nil {
					cli := d.client()
					if cli == nil {
						part["error"] = "download_failed"
					} else {
						got, err := cli.Download(context.Background(), ev.MediaRef)
						if err != nil {
							part["error"] = "download_failed"
						} else {
							data = got
						}
					}
				}
				if part["error"] == nil {
					if len(data) == 0 {
						part["error"] = "download_failed"
					} else {
						ext := ev.MediaExt
						if ext == "" {
							ext = files.ExtForMIME(ev.MIME, "")
						}
						rel, err := fd.WriteInbound(chat, ev.ID, ext, data)
						if err != nil {
							part["error"] = err.Error()
						} else {
							part["path"] = rel
						}
					}
				}
			}
			parts = []map[string]any{part}
			if ev.Text != "" {
				parts = append(parts, map[string]any{"type": "text", "text": ev.Text})
			}
		case "location":
			loc := map[string]any{"type": "location", "lat": ev.Lat, "lng": ev.Lng}
			if ev.LocName != "" {
				loc["name"] = ev.LocName
			}
			if ev.LocAddr != "" {
				loc["address"] = ev.LocAddr
			}
			parts = []map[string]any{loc}
		default:
			unk := map[string]any{"type": "unknown"}
			if ev.Label != "" {
				unk["label"] = ev.Label
			}
			parts = []map[string]any{unk}
		}
	}
	out["contents"] = parts
	d.touchDirectoryFromMessage(ev, chat)
	d.emit(out)
}

func (d *Daemon) handleReceipt(ev wa.Event) {
	chat := d.canonicalize(ev.Chat)
	if chat == "" || !d.bus.Has(chat) {
		return
	}
	ack := ev.Ack
	if ack == "" {
		ack = "delivered"
	}
	out := map[string]any{
		"topic": chat,
		"kind":  "ack",
		"contents": []map[string]any{{
			"type": "ack",
			"ids":  ev.IDs,
			"ack":  ack,
		}},
	}
	d.decorateChat(out, chat, "", "")
	d.emit(out)
}

func (d *Daemon) handleMeta(ev wa.Event) {
	chat := d.canonicalize(ev.Chat)
	if chat == "" {
		return
	}
	if d.bus.Has(chat) {
		part := map[string]any{"type": "meta", "action": ev.Action}
		if ev.Name != "" {
			part["name"] = ev.Name
		}
		m := map[string]any{
			"topic":    chat,
			"kind":     "meta",
			"contents": []map[string]any{part},
		}
		by := ""
		if ev.Sender != "" {
			by = d.canonicalize(ev.Sender)
			if by == d.me() {
				by = "me"
			}
			m["by"] = by
		}
		d.decorateChat(m, chat, by, "")
		d.emit(m)
	}
	// Keep directory warm even when not subscribed.
	if ev.Action == "rename" || ev.Action == "icon" || ev.Action == "topic" {
		if row, ok := d.dirRow(chat); ok {
			if ev.Name != "" {
				row.Name = ev.Name
			}
			if st := d.store(); st != nil {
				_ = st.Upsert(row)
			}
			d.emitDirectoryUpsert(row)
		}
	}
}

func (d *Daemon) touchDirectoryFromMessage(ev wa.Event, chat string) {
	st := d.store()
	if st == nil || chat == "" || strings.HasPrefix(chat, "$") {
		return
	}
	kind := "user"
	if topic.IsGroup(chat) {
		kind = "group"
	}
	// PushName is the sender's. Never paint it onto a group (that replaces
	// the subject with the last author) or onto the 1:1 peer on from-me.
	name := ev.Name
	pn := ev.PN
	if kind == "group" || (ev.FromMe && kind == "user") {
		name = ""
		if kind == "group" {
			pn = ""
		}
	}
	row := dirstore.Row{Topic: chat, Kind: kind, Name: name, PN: pn}
	if existing, ok, _ := st.Get(chat); ok {
		if row.Name == "" {
			row.Name = existing.Name
		}
		if row.PN == "" {
			row.PN = existing.PN
		}
		row.Muted = existing.Muted
		row.Pinned = existing.Pinned
		row.Archived = existing.Archived
		if kind == "group" {
			row.Handle = existing.Handle
		}
	}
	_ = st.Upsert(row)
}

func (d *Daemon) ingestContact(c wa.Contact) {
	lid := c.LID
	if lid == "" {
		lid = d.canonicalize(c.JID)
	}
	if lid == "" {
		return
	}
	pn := c.PN
	if pn != "" && topic.IsLID(lid) {
		d.applyMapping(lid, pn)
	}
	st := d.store()
	if st == nil {
		return
	}
	_ = st.Upsert(userRow(lid, pn, c.Name, c.Handle))
	if row, ok := d.dirRow(lid); ok {
		d.emitDirectoryUpsert(row)
	}
}

func (d *Daemon) ingestHistory(h wa.HistorySync) {
	for _, m := range h.Mappings {
		d.applyMapping(m.LID, m.PN)
	}
	for _, p := range h.PushNames {
		if p.JID == "" {
			continue
		}
		d.ingestContact(wa.Contact{JID: p.JID, Name: p.Name})
	}
	for _, c := range h.InlineContacts {
		d.ingestContact(c)
	}
	if h.SelfHandle != "" {
		d.ingestContact(wa.Contact{JID: d.me(), LID: d.me(), Handle: h.SelfHandle})
	}
	st := d.store()
	for _, c := range h.Conversations {
		id := firstNonEmpty(c.LID, c.ID)
		id = d.canonicalize(id)
		if id == "" {
			continue
		}
		if c.PN != "" && topic.IsLID(id) {
			d.applyMapping(id, c.PN)
		}
		kind := "user"
		handle := c.Handle
		if topic.IsGroup(id) {
			kind = "group"
			handle = ""
		}
		var parts []dirstore.Participant
		for _, p := range c.Participants {
			pt := d.canonicalize(p.JID)
			if p.PN != "" && topic.IsLID(pt) {
				d.applyMapping(pt, p.PN)
			}
			parts = append(parts, dirstore.Participant{Topic: pt, Name: p.Name, Handle: p.Handle, PN: p.PN, Role: p.Role})
		}
		if st != nil {
			_ = st.Upsert(dirstore.Row{
				Topic:            id,
				Kind:             kind,
				Name:             c.Name,
				Handle:           handle,
				PN:               c.PN,
				Archived:         c.Archived,
				Pinned:           c.Pinned,
				Participants:     parts,
				ParticipantCount: len(parts),
			})
			if row, ok := d.dirRow(id); ok {
				d.emitDirectoryUpsert(row)
			}
		}
	}
	// HistorySync message bodies: deliberately ignored. Never emit, never store.
	_ = h.Messages
}

func (d *Daemon) populate() {
	d.mu.Lock()
	if d.populating {
		d.mu.Unlock()
		return
	}
	d.populating = true
	d.mu.Unlock()
	defer func() {
		d.mu.Lock()
		d.populating = false
		d.mu.Unlock()
	}()

	cli := d.client()
	if cli == nil {
		return
	}
	ctx := context.Background()
	_ = cli.FetchAppState(ctx)
	n := 0
	if groups, err := cli.GetJoinedGroups(ctx); err == nil {
		for _, g := range groups {
			var parts []dirstore.Participant
			for _, p := range g.Participants {
				pt := d.canonicalize(p.JID)
				if p.PN != "" && topic.IsLID(pt) {
					d.applyMapping(pt, p.PN)
				}
				parts = append(parts, dirstore.Participant{Topic: pt, Name: p.Name, Handle: p.Handle, PN: p.PN, Role: p.Role})
			}
			jid := d.canonicalize(g.JID)
			if st := d.store(); st != nil {
				_ = st.Upsert(groupRow(jid, g.Name, parts))
				if row, ok := d.dirRow(jid); ok {
					d.emitDirectoryUpsert(row)
					n++
				}
			}
		}
	}
	if contacts, err := cli.GetContacts(ctx); err == nil {
		for _, c := range contacts {
			lid := c.LID
			if lid == "" {
				lid = d.canonicalize(c.JID)
			}
			if lid == "" {
				continue
			}
			if c.PN != "" && topic.IsLID(lid) {
				d.applyMapping(lid, c.PN)
			}
			if st := d.store(); st != nil {
				_ = st.Upsert(userRow(lid, c.PN, c.Name, c.Handle))
				if row, ok := d.dirRow(lid); ok {
					d.emitDirectoryUpsert(row)
					n++
				}
			}
		}
	}
	d.emitDirectoryReady(n)
}

func (d *Daemon) decorateChat(out map[string]any, chat, by, pushName string) {
	if name := d.lookupTopicName(chat); name != "" {
		out["topicName"] = name
	}
	if by == "" {
		return
	}
	handle, byName := d.authorIdentity(chat, by, pushName)
	if handle != "" {
		out["handle"] = handle
	}
	if byName != "" {
		out["byName"] = byName
	}
}

func (d *Daemon) lookupTopicName(chat string) string {
	if row, ok := d.dirRow(chat); ok {
		return row.Name
	}
	return ""
}

func (d *Daemon) authorIdentity(chat, by, pushName string) (handle, byName string) {
	author := by
	if by == "me" {
		author = d.me()
	}
	if topic.IsGroup(chat) {
		if p, ok := d.lookupParticipant(chat, author); ok {
			handle = dirstore.NormalizeHandle(p.Handle)
			if isRealDisplayName(p.Name) {
				byName = p.Name
			}
		}
	}
	if row, ok := d.dirRow(author); ok {
		if handle == "" {
			handle = dirstore.NormalizeHandle(row.Handle)
		}
		if byName == "" {
			byName = row.Name
		}
	}
	if byName == "" && by != "me" {
		byName = cleanPushName(pushName)
	}
	return handle, byName
}

func (d *Daemon) lookupParticipant(group, user string) (dirstore.Participant, bool) {
	st := d.store()
	if st == nil || group == "" || user == "" {
		return dirstore.Participant{}, false
	}
	ps, err := st.Participants(group)
	if err != nil {
		return dirstore.Participant{}, false
	}
	for _, p := range ps {
		if p.Topic == user {
			return p, true
		}
	}
	return dirstore.Participant{}, false
}

func isRealDisplayName(s string) bool {
	s = strings.TrimSpace(s)
	if s == "" || s == "-" {
		return false
	}
	if strings.Contains(s, "∙") || strings.Contains(s, "•") {
		return false
	}
	return true
}

func cleanPushName(s string) string {
	s = strings.TrimSpace(s)
	if s == "" || s == "-" {
		return ""
	}
	return s
}

func firstNonEmpty(ss ...string) string {
	for _, s := range ss {
		if s != "" {
			return s
		}
	}
	return ""
}
