package app

import (
	"context"
	"strings"

	"github.com/devlooped/whatsbox/internal/rpc"
	"github.com/devlooped/whatsbox/internal/topic"
	"github.com/devlooped/whatsbox/internal/wa"
)

func (d *Daemon) resolveTopic(ctx context.Context, raw string, allowSystem bool) (string, *rpc.Error) {
	p, err := topic.Parse(raw)
	if err != nil {
		return "", rpc.ErrData(rpc.TokInvalidTopic, raw)
	}
	switch p.Kind {
	case topic.KindSystem:
		if !allowSystem {
			return "", rpc.ErrData(rpc.TokInvalidTopic, raw)
		}
		return p.Canonical, nil
	case topic.KindGroup:
		return p.Canonical, nil
	case topic.KindLID:
		return p.Canonical, nil
	case topic.KindPN:
		if lid, ok := d.lookupLID(p.Canonical, p.Phone); ok {
			return lid, nil
		}
		if info, err := d.usync(ctx, p.Phone); err != nil {
			return "", err
		} else if info != "" {
			return info, nil
		}
		// Offline / no mapping: keep PN JID as a temporary topic.
		return p.Canonical, nil
	case topic.KindPhone:
		if lid, ok := d.lookupLID(topic.PNJID(p.Phone), p.Phone); ok {
			return lid, nil
		}
		if info, err := d.usync(ctx, p.Phone); err != nil {
			return "", err
		} else if info != "" {
			return info, nil
		}
		if d.online() {
			return "", rpc.ErrData(rpc.TokNotFound, raw)
		}
		return "", rpc.ErrData(rpc.TokNotFound, raw)
	default:
		return "", rpc.ErrData(rpc.TokInvalidTopic, raw)
	}
}

// resolveSubscribeTopic accepts only canonical chat JIDs (LID, group) and
// system topics. Names, handles, and phone numbers are the client's job via
// directory.list. A PN JID is kept as a temporary topic until a LID mapping
// exists (then remap).
func (d *Daemon) resolveSubscribeTopic(raw string, allowSystem bool) (string, *rpc.Error) {
	p, err := topic.Parse(raw)
	if err != nil {
		return "", rpc.ErrData(rpc.TokInvalidTopic, raw)
	}
	switch p.Kind {
	case topic.KindSystem:
		if !allowSystem {
			return "", rpc.ErrData(rpc.TokInvalidTopic, raw)
		}
		return p.Canonical, nil
	case topic.KindGroup, topic.KindLID:
		return p.Canonical, nil
	case topic.KindPN:
		if lid, ok := d.lookupLID(p.Canonical, p.Phone); ok {
			return lid, nil
		}
		return p.Canonical, nil
	default:
		return "", rpc.ErrData(rpc.TokInvalidTopic, raw)
	}
}

func (d *Daemon) lookupLID(pnJID, phone string) (string, bool) {
	st := d.store()
	if st == nil {
		return "", false
	}
	if pnJID != "" {
		if lid, ok := st.LIDForPN(pnJID); ok {
			return lid, true
		}
	}
	if phone != "" {
		if lid, ok := st.LIDForPN(topic.PNJID(phone)); ok {
			return lid, true
		}
		if lid, pn, ok := st.LookupEither(phone); ok && lid != "" {
			_ = pn
			return lid, true
		}
	}
	return "", false
}

func (d *Daemon) usync(ctx context.Context, phone string) (string, *rpc.Error) {
	cli := d.client()
	if cli == nil || !cli.IsConnected() {
		return "", nil
	}
	infos, err := cli.IsOnWhatsApp(ctx, []string{"+" + phone, phone})
	if err != nil {
		if err == wa.ErrNotConnected {
			return "", rpc.Err(rpc.TokDisconnected)
		}
		return "", rpc.ErrData(rpc.TokNotFound, phone)
	}
	for _, info := range infos {
		if !info.IsIn {
			continue
		}
		lid := info.JID
		pn := info.PN
		if pn == "" {
			pn = topic.PNJID(phone)
		}
		if topic.IsPN(lid) && info.PN != "" && topic.IsLID(info.PN) {
			lid, pn = info.PN, lid
		}
		if lid == "" {
			continue
		}
		if !topic.IsLID(lid) && topic.IsPN(lid) {
			// usync returned only a PN; do not invent a LID.
			continue
		}
		if topic.IsLID(lid) {
			d.applyMapping(lid, pn)
		}
		if topic.IsLID(lid) {
			return lid, nil
		}
	}
	return "", nil
}

func (d *Daemon) canonicalize(raw string) string {
	if raw == "" {
		return ""
	}
	p, err := topic.Parse(raw)
	if err != nil {
		return raw
	}
	switch p.Kind {
	case topic.KindLID, topic.KindGroup, topic.KindSystem:
		return p.Canonical
	case topic.KindPN:
		if lid, ok := d.lookupLID(p.Canonical, p.Phone); ok {
			return lid
		}
		return p.Canonical
	case topic.KindPhone:
		if lid, ok := d.lookupLID(topic.PNJID(p.Phone), p.Phone); ok {
			return lid
		}
		return topic.PNJID(p.Phone)
	default:
		return raw
	}
}

func (d *Daemon) applyMapping(lid, pn string) {
	if lid == "" || pn == "" {
		return
	}
	if !topic.IsLID(lid) && topic.IsLID(pn) {
		lid, pn = pn, lid
	}
	if !strings.Contains(pn, "@") {
		pn = topic.PNJID(pn)
	}
	st := d.store()
	if st != nil {
		_ = st.PutMapping(lid, pn)
		if row, ok, _ := st.Get(lid); ok {
			row.PN = pn
			_ = st.Upsert(row)
		} else if row, ok, _ := st.Get(pn); ok {
			_ = st.Remove(pn)
			row.Topic = lid
			row.PN = pn
			_ = st.Upsert(row)
		} else {
			_ = st.Upsert(userRow(lid, pn, "", ""))
		}
	}
	if d.bus.Has(pn) && pn != lid {
		d.bus.Move(pn, lid)
		d.emit(map[string]any{
			"topic": topic.Session,
			"kind":  "remap",
			"from":  pn,
			"to":    lid,
		})
	}
	if row, ok := d.dirRow(lid); ok {
		d.emitDirectoryUpsert(row)
	}
}
