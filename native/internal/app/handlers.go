package app

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"

	"github.com/devlooped/whatsbox/internal/dirstore"
	"github.com/devlooped/whatsbox/internal/files"
	"github.com/devlooped/whatsbox/internal/rpc"
	"github.com/devlooped/whatsbox/internal/topic"
	"github.com/devlooped/whatsbox/internal/wa"
)

type topicsParams struct {
	Topics []string `json:"topics"`
}

func (d *Daemon) subscribe(ctx context.Context, raw json.RawMessage) (any, *rpc.Error) {
	var p topicsParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	return d.applySubscribe(ctx, p.Topics, true)
}

func (d *Daemon) applySubscribe(ctx context.Context, topics []string, requireParams bool) (any, *rpc.Error) {
	if requireParams && len(topics) == 0 {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "topics is required")
	}
	resolved := make([]string, 0, len(topics))
	seen := map[string]struct{}{}
	for _, t := range topics {
		canon, err := d.resolveTopic(ctx, t, true)
		if err != nil {
			return nil, err
		}
		if _, ok := seen[canon]; ok {
			continue
		}
		seen[canon] = struct{}{}
		resolved = append(resolved, canon)
	}
	d.bus.Subscribe(resolved...)
	return map[string]any{"topics": resolved}, nil
}

func (d *Daemon) unsubscribe(raw json.RawMessage) (any, *rpc.Error) {
	var p topicsParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	if len(p.Topics) == 0 {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "topics is required")
	}
	canon := make([]string, 0, len(p.Topics))
	for _, t := range p.Topics {
		if strings.TrimSpace(t) == topic.Session {
			return nil, rpc.ErrData(rpc.TokInvalidTopic, topic.Session)
		}
		c, err := d.resolveTopic(context.Background(), t, true)
		if err != nil {
			return nil, err
		}
		if c == topic.Session {
			return nil, rpc.ErrData(rpc.TokInvalidTopic, topic.Session)
		}
		canon = append(canon, c)
	}
	d.bus.Unsubscribe(canon...)
	return map[string]any{"topics": d.bus.Topics()}, nil
}

type listParams struct {
	Query  string `json:"query"`
	Kind   string `json:"kind"`
	Limit  int    `json:"limit"`
	Cursor string `json:"cursor"`
}

func (d *Daemon) directoryList(raw json.RawMessage) (any, *rpc.Error) {
	var p listParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	if p.Kind != "" && p.Kind != "user" && p.Kind != "group" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "kind")
	}
	st := d.store()
	if st == nil {
		return map[string]any{"items": []any{}}, nil
	}
	items, cursor, err := st.List(p.Query, p.Kind, p.Limit, p.Cursor)
	if err != nil {
		return nil, rpc.ErrData(rpc.TokInvalidParams, err.Error())
	}
	out := make([]map[string]any, 0, len(items))
	for _, it := range items {
		out = append(out, rowJSON(it, false))
	}
	res := map[string]any{"items": out}
	if cursor != "" {
		res["cursor"] = cursor
	}
	return res, nil
}

type getParams struct {
	ID   string `json:"id"`
	Icon *bool  `json:"icon"`
}

func (d *Daemon) directoryGet(ctx context.Context, raw json.RawMessage) (any, *rpc.Error) {
	var p getParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	if strings.TrimSpace(p.ID) == "" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "id is required")
	}
	canon, err := d.resolveTopic(ctx, p.ID, false)
	if err != nil {
		if err.Message == rpc.TokNotFound {
			return nil, rpc.Err(rpc.TokNotFound)
		}
		return nil, err
	}
	st := d.store()
	if st == nil {
		return nil, rpc.Err(rpc.TokNotFound)
	}
	row, ok, gerr := st.Get(canon)
	if gerr != nil {
		return nil, rpc.ErrData(rpc.TokInvalidParams, gerr.Error())
	}
	if !ok {
		return nil, rpc.Err(rpc.TokNotFound)
	}
	if row.Kind == "group" {
		ps, _ := st.Participants(row.Topic)
		row.Participants = ps
	}
	wantIcon := d.filesDir().Enabled()
	if p.Icon != nil {
		wantIcon = *p.Icon
	}
	if wantIcon && !d.filesDir().Enabled() {
		return nil, rpc.Err(rpc.TokFilesRequired)
	}
	if wantIcon {
		if ic, ierr := d.fetchIcon(ctx, row.Topic); ierr == nil && ic != "" {
			row.Icon = ic
		}
	} else {
		row.Icon = ""
	}
	return rowJSON(row, true), nil
}

func (d *Daemon) fetchIcon(ctx context.Context, jid string) (string, error) {
	cli := d.client()
	fd := d.filesDir()
	if cli == nil || !fd.Enabled() {
		return "", rpc.Err(rpc.TokFilesRequired)
	}
	if !cli.IsConnected() {
		return "", rpc.Err(rpc.TokDisconnected)
	}
	ic, err := cli.GetProfileIcon(ctx, jid)
	if err != nil || ic == nil {
		return "", err
	}
	ext := ic.Ext
	if ext == "" {
		ext = ".jpg"
	}
	return fd.WriteIcon(jid, ext, ic.Data)
}

func (d *Daemon) dirRow(id string) (dirstore.Row, bool) {
	st := d.store()
	if st == nil {
		return dirstore.Row{}, false
	}
	row, ok, err := st.Get(id)
	if err != nil || !ok {
		return dirstore.Row{}, false
	}
	return row, true
}

func (d *Daemon) emitDirectoryUpsert(row dirstore.Row) {
	d.emit(directoryUpsert(row))
}

func (d *Daemon) emitDirectoryRemove(canonical string) {
	d.emit(map[string]any{
		"topic": topic.Directory,
		"kind":  "remove",
		"jid":   canonical,
	})
}

func (d *Daemon) emitDirectoryReady(generated int) {
	d.emit(map[string]any{
		"topic":     topic.Directory,
		"kind":      "ready",
		"generated": generated,
	})
}

func directoryUpsert(row dirstore.Row) map[string]any {
	m := rowJSON(row, false)
	out := map[string]any{
		"topic": topic.Directory,
		"kind":  "upsert",
		"jid":   row.Topic,
	}
	for k, v := range m {
		if k == "topic" || k == "kind" || k == "participants" {
			continue
		}
		out[k] = v
	}
	out["entityKind"] = row.Kind
	return out
}

func rowJSON(row dirstore.Row, includeParticipants bool) map[string]any {
	m := map[string]any{
		"topic":    row.Topic,
		"kind":     row.Kind,
		"muted":    row.Muted,
		"pinned":   row.Pinned,
		"archived": row.Archived,
	}
	if row.Name != "" {
		m["name"] = row.Name
	}
	if h := dirstore.NormalizeHandle(row.Handle); h != "" {
		m["handle"] = h
	}
	if row.PN != "" {
		m["pn"] = row.PN
	}
	if row.Icon != "" {
		m["icon"] = row.Icon
	}
	if row.ParticipantCount > 0 {
		m["participantCount"] = row.ParticipantCount
	}
	if includeParticipants && row.Kind == "group" {
		ps := make([]map[string]any, 0, len(row.Participants))
		for _, p := range row.Participants {
			pm := map[string]any{"topic": p.Topic}
			if p.Name != "" {
				pm["name"] = p.Name
			}
			if h := dirstore.NormalizeHandle(p.Handle); h != "" {
				pm["handle"] = h
			}
			if p.PN != "" {
				pm["pn"] = p.PN
			}
			ps = append(ps, pm)
		}
		m["participants"] = ps
	}
	return m
}

func userRow(lid, pn, name, handle string) dirstore.Row {
	return dirstore.Row{Topic: lid, Kind: "user", PN: pn, Name: name, Handle: dirstore.NormalizeHandle(handle)}
}

func groupRow(jid, name string, parts []dirstore.Participant) dirstore.Row {
	return dirstore.Row{Topic: jid, Kind: "group", Name: name, Participants: parts, ParticipantCount: len(parts)}
}

type sendParams struct {
	To    string `json:"to"`
	Text  string `json:"text"`
	Path  string `json:"path"`
	Reply *struct {
		ID string `json:"id"`
		By string `json:"by"`
	} `json:"reply"`
	React *struct {
		ID    string `json:"id"`
		By    string `json:"by"`
		Emoji string `json:"emoji"`
	} `json:"react"`
}

func (d *Daemon) messagesSend(ctx context.Context, raw json.RawMessage) (any, *rpc.Error) {
	var p sendParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	if strings.TrimSpace(p.To) == "" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "to is required")
	}
	if p.Text == "" && p.Path == "" && p.React == nil {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "at least one of text, path, react")
	}
	if p.Reply != nil && (p.Reply.ID == "" || p.Reply.By == "") {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "reply requires id and by")
	}
	if p.React != nil && (p.React.ID == "" || p.React.By == "") {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "react requires id and by")
	}
	if p.Path != "" && !d.filesDir().Enabled() {
		return nil, rpc.Err(rpc.TokFilesRequired)
	}
	if !d.online() {
		return nil, rpc.Err(rpc.TokDisconnected)
	}
	canon, err := d.resolveTopic(ctx, p.To, false)
	if err != nil {
		return nil, err
	}
	cli := d.client()
	var id string
	var sendErr error
	var evKind, evText, evPath, evEmoji, evTarget string
	if p.React != nil && p.Text == "" && p.Path == "" {
		id, sendErr = cli.SendReact(ctx, wa.SendReact{
			To:    canon,
			ID:    p.React.ID,
			By:    d.normalizeBy(p.React.By),
			Emoji: p.React.Emoji,
		})
		evKind = "reaction"
		evEmoji = p.React.Emoji
		evTarget = p.React.ID
	} else if p.Path != "" {
		abs, rerr := d.filesDir().Resolve(p.Path)
		if rerr != nil {
			if e, ok := rerr.(*rpc.Error); ok {
				return nil, e
			}
			return nil, rpc.Err(rpc.TokPathEscape)
		}
		data, rerr := os.ReadFile(abs)
		if rerr != nil {
			return nil, rpc.ErrData(rpc.TokInvalidParams, rerr.Error())
		}
		kind := files.KindForPath(abs)
		id, sendErr = cli.SendMedia(ctx, wa.SendMedia{
			To:       canon,
			Path:     p.Path,
			Data:     data,
			MIME:     files.MIMEForPath(abs),
			FileName: filepath.Base(abs),
			Caption:  p.Text,
			Kind:     kind,
			ReplyID:  replyID(p),
			ReplyBy:  replyBy(d, p),
		})
		evKind = kind
		evText = p.Text
		evPath = p.Path
	} else {
		id, sendErr = cli.SendText(ctx, wa.SendText{
			To:      canon,
			Text:    p.Text,
			ReplyID: replyID(p),
			ReplyBy: replyBy(d, p),
		})
		evKind = "text"
		evText = p.Text
	}
	if sendErr != nil {
		if sendErr == wa.ErrNotConnected {
			return nil, rpc.Err(rpc.TokDisconnected)
		}
		return nil, rpc.ErrData(rpc.TokInvalidParams, sendErr.Error())
	}
	if d.bus.Has(canon) {
		ev := map[string]any{
			"topic": canon,
			"kind":  evKind,
			"id":    id,
			"by":    "me",
		}
		d.decorateChat(ev, canon, "me", "")
		if evText != "" {
			ev["text"] = evText
		}
		if evPath != "" {
			ev["path"] = evPath
		}
		if evKind == "reaction" {
			ev["emoji"] = evEmoji
			ev["target"] = evTarget
		}
		d.emit(ev)
	}
	return map[string]any{"id": id, "topic": canon}, nil
}

func replyID(p sendParams) string {
	if p.Reply != nil {
		return p.Reply.ID
	}
	return ""
}

func replyBy(d *Daemon, p sendParams) string {
	if p.Reply != nil {
		return d.normalizeBy(p.Reply.By)
	}
	return ""
}

func (d *Daemon) normalizeBy(by string) string {
	if by == "" || by == "me" {
		return "me"
	}
	return d.canonicalize(by)
}

type readParams struct {
	To  string   `json:"to"`
	IDs []string `json:"ids"`
	By  string   `json:"by"`
}

func (d *Daemon) messagesRead(ctx context.Context, raw json.RawMessage) (any, *rpc.Error) {
	var p readParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	if strings.TrimSpace(p.To) == "" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "to is required")
	}
	if len(p.IDs) == 0 {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "ids is required")
	}
	if !d.online() {
		return nil, rpc.Err(rpc.TokDisconnected)
	}
	canon, err := d.resolveTopic(ctx, p.To, false)
	if err != nil {
		return nil, err
	}
	isGroup := topic.IsGroup(canon)
	if isGroup && strings.TrimSpace(p.By) == "" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "by is required for groups")
	}
	if !isGroup && strings.TrimSpace(p.By) != "" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "by must be omitted for 1:1")
	}
	sender := ""
	if isGroup {
		sender = d.normalizeBy(p.By)
		if sender == "me" {
			sender = d.me()
		}
	}
	if err := d.client().MarkRead(ctx, canon, p.IDs, sender); err != nil {
		if err == wa.ErrNotConnected {
			return nil, rpc.Err(rpc.TokDisconnected)
		}
		return nil, rpc.ErrData(rpc.TokInvalidParams, err.Error())
	}
	return map[string]any{"topic": canon}, nil
}
