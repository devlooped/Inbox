package wa

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/store/sqlstore"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/whatsmeow/util/log"
	"google.golang.org/protobuf/proto"

	"github.com/devlooped/whatsbox/internal/sqliteutil"

	_ "modernc.org/sqlite"
)

var _ Client = (*Real)(nil)

type Real struct {
	mu        sync.Mutex
	cli       *whatsmeow.Client
	container *sqlstore.Container
	handler   HandlerMux
	handlerID uint32
}

func OpenReal(storeDir string) (Client, error) {
	dbPath := filepath.Join(storeDir, "session.db")
	uri := sqliteutil.FileURI(dbPath, "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)")
	log := waLog.Noop
	container, err := sqlstore.New(context.Background(), "sqlite", uri, log)
	if err != nil {
		return nil, fmt.Errorf("open session store: %w", err)
	}
	device, err := container.GetFirstDevice(context.Background())
	if err != nil {
		_ = container.Close()
		return nil, fmt.Errorf("get device: %w", err)
	}
	cli := whatsmeow.NewClient(device, log)
	cli.EnableAutoReconnect = false
	cli.AutomaticMessageRerequestFromPhone = true
	r := &Real{cli: cli, container: container}
	r.handlerID = cli.AddEventHandler(r.onEvent)
	_ = sqliteutil.ChmodFiles(dbPath, 0o600)
	return r, nil
}

func (r *Real) Close() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.cli != nil {
		r.cli.Disconnect()
	}
	if r.container != nil {
		return r.container.Close()
	}
	return nil
}

func (r *Real) SetHandler(h Handler) { r.handler.Set(h) }

func (r *Real) IsPaired() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.cli != nil && r.cli.Store != nil && r.cli.Store.ID != nil
}

func (r *Real) Me() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.cli == nil || r.cli.Store == nil {
		return ""
	}
	lid := r.cli.Store.GetLID()
	if !lid.IsEmpty() {
		return lid.ToNonAD().String()
	}
	if r.cli.Store.ID != nil {
		return r.cli.Store.ID.ToNonAD().String()
	}
	return ""
}

func (r *Real) IsConnected() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.cli != nil && r.cli.IsConnected()
}

func (r *Real) Connect(ctx context.Context) error {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil {
		return fmt.Errorf("client closed")
	}
	if cli.IsConnected() {
		return nil
	}
	if !r.IsPaired() {
		return r.Pair(ctx)
	}
	done := make(chan error, 1)
	var once sync.Once
	id := cli.AddEventHandler(func(evt any) {
		switch evt.(type) {
		case *events.Connected:
			once.Do(func() { done <- nil })
		case *events.LoggedOut:
			once.Do(func() { done <- fmt.Errorf("logged out") })
		}
	})
	defer cli.RemoveEventHandler(id)
	if err := cli.Connect(); err != nil {
		return err
	}
	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (r *Real) Pair(ctx context.Context) error {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil {
		return fmt.Errorf("client closed")
	}
	if r.IsPaired() && cli.IsConnected() {
		return nil
	}
	// Exclusive QR path: GetQRChannel rotates the pair-device batch (60s then 20s).
	// onEvent must not also handle *events.QR or the first code is emitted twice.
	ch, err := cli.GetQRChannel(ctx)
	if err != nil {
		if r.IsPaired() {
			return r.Connect(ctx)
		}
		return err
	}
	errCh := make(chan error, 1)
	go func() {
		for item := range ch {
			switch item.Event {
			case whatsmeow.QRChannelEventCode:
				r.handler.Emit(Event{Type: EvtQR, Code: item.Code})
			case "success":
				r.handler.Emit(Event{Type: EvtPaired, Me: r.Me()})
				errCh <- nil
				return
			case whatsmeow.QRChannelEventError:
				msg := "pair failed"
				if item.Error != nil {
					msg = item.Error.Error()
				}
				r.handler.Emit(Event{Type: EvtPairError, Message: msg})
				errCh <- item.Error
				return
			case whatsmeow.QRChannelEventPasskeyRequest:
				r.handler.Emit(Event{Type: EvtPairError, Message: "passkey required"})
				errCh <- ErrPasskey
				return
			default:
				if item.Event == QRChannelTimeoutEvent(item) {
					r.handler.Emit(Event{Type: EvtPairError, Message: item.Event})
					errCh <- fmt.Errorf("pair %s", item.Event)
					return
				}
			}
		}
		errCh <- fmt.Errorf("qr channel closed")
	}()
	if err := cli.Connect(); err != nil {
		return err
	}
	select {
	case err := <-errCh:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

func QRChannelTimeoutEvent(item whatsmeow.QRChannelItem) string {
	switch item.Event {
	case "timeout", "err-unexpected-state", "err-client-outdated", "err-scanned-without-multidevice":
		return item.Event
	default:
		return ""
	}
}

func (r *Real) Disconnect() {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli != nil {
		cli.EnableAutoReconnect = false
		cli.Disconnect()
	}
}

func (r *Real) Logout(ctx context.Context) error {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil {
		return nil
	}
	return cli.Logout(ctx)
}

func (r *Real) IsOnWhatsApp(ctx context.Context, phones []string) ([]PhoneInfo, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return nil, ErrNotConnected
	}
	norm := make([]string, 0, len(phones))
	for _, p := range phones {
		p = strings.TrimSpace(p)
		if p == "" {
			continue
		}
		if !strings.HasPrefix(p, "+") {
			p = "+" + strings.TrimLeft(p, "+")
		}
		norm = append(norm, p)
	}
	resp, err := cli.IsOnWhatsApp(ctx, norm)
	if err != nil {
		return nil, err
	}
	out := make([]PhoneInfo, 0, len(resp))
	for _, info := range resp {
		out = append(out, PhoneInfo{
			Query: info.Query,
			IsIn:  info.IsIn,
			JID:   info.JID.ToNonAD().String(),
			PN:    info.PhoneNumber.ToNonAD().String(),
		})
	}
	return out, nil
}

func (r *Real) SendText(ctx context.Context, req SendText) (string, error) {
	to, err := types.ParseJID(req.To)
	if err != nil {
		return "", err
	}
	msg := &waE2E.Message{
		Conversation: proto.String(req.Text),
	}
	if req.ReplyID != "" {
		msg.Conversation = nil
		msg.ExtendedTextMessage = &waE2E.ExtendedTextMessage{
			Text:        proto.String(req.Text),
			ContextInfo: replyContext(req.To, req.ReplyID, req.ReplyBy),
		}
	}
	return r.send(ctx, to, msg)
}

func (r *Real) SendMedia(ctx context.Context, req SendMedia) (string, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return "", ErrNotConnected
	}
	to, err := types.ParseJID(req.To)
	if err != nil {
		return "", err
	}
	mediaType := whatsmeow.MediaImage
	switch req.Kind {
	case "video":
		mediaType = whatsmeow.MediaVideo
	case "audio":
		mediaType = whatsmeow.MediaAudio
	case "document":
		mediaType = whatsmeow.MediaDocument
	case "sticker":
		mediaType = whatsmeow.MediaImage
	}
	up, err := cli.Upload(ctx, req.Data, mediaType)
	if err != nil {
		return "", err
	}
	ctxInfo := replyContext(req.To, req.ReplyID, req.ReplyBy)
	var msg *waE2E.Message
	switch req.Kind {
	case "video":
		msg = &waE2E.Message{VideoMessage: &waE2E.VideoMessage{
			URL: proto.String(up.URL), DirectPath: proto.String(up.DirectPath),
			MediaKey: up.MediaKey, FileSHA256: up.FileSHA256, FileEncSHA256: up.FileEncSHA256,
			FileLength: proto.Uint64(up.FileLength), Mimetype: proto.String(req.MIME),
			Caption: proto.String(req.Caption), ContextInfo: ctxInfo,
		}}
	case "audio":
		msg = &waE2E.Message{AudioMessage: &waE2E.AudioMessage{
			URL: proto.String(up.URL), DirectPath: proto.String(up.DirectPath),
			MediaKey: up.MediaKey, FileSHA256: up.FileSHA256, FileEncSHA256: up.FileEncSHA256,
			FileLength: proto.Uint64(up.FileLength), Mimetype: proto.String(req.MIME),
			ContextInfo: ctxInfo,
		}}
	case "document":
		msg = &waE2E.Message{DocumentMessage: &waE2E.DocumentMessage{
			URL: proto.String(up.URL), DirectPath: proto.String(up.DirectPath),
			MediaKey: up.MediaKey, FileSHA256: up.FileSHA256, FileEncSHA256: up.FileEncSHA256,
			FileLength: proto.Uint64(up.FileLength), Mimetype: proto.String(req.MIME),
			FileName: proto.String(req.FileName), Caption: proto.String(req.Caption), ContextInfo: ctxInfo,
		}}
	case "sticker":
		msg = &waE2E.Message{StickerMessage: &waE2E.StickerMessage{
			URL: proto.String(up.URL), DirectPath: proto.String(up.DirectPath),
			MediaKey: up.MediaKey, FileSHA256: up.FileSHA256, FileEncSHA256: up.FileEncSHA256,
			FileLength: proto.Uint64(up.FileLength), Mimetype: proto.String(req.MIME),
			ContextInfo: ctxInfo,
		}}
	default:
		msg = &waE2E.Message{ImageMessage: &waE2E.ImageMessage{
			URL: proto.String(up.URL), DirectPath: proto.String(up.DirectPath),
			MediaKey: up.MediaKey, FileSHA256: up.FileSHA256, FileEncSHA256: up.FileEncSHA256,
			FileLength: proto.Uint64(up.FileLength), Mimetype: proto.String(req.MIME),
			Caption: proto.String(req.Caption), ContextInfo: ctxInfo,
		}}
	}
	return r.send(ctx, to, msg)
}

func (r *Real) SendReact(ctx context.Context, req SendReact) (string, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return "", ErrNotConnected
	}
	chat, err := types.ParseJID(req.To)
	if err != nil {
		return "", err
	}
	sender := types.EmptyJID
	if req.By != "" && req.By != "me" {
		sender, _ = types.ParseJID(req.By)
	}
	msg := cli.BuildReaction(chat, sender, req.ID, req.Emoji)
	return r.send(ctx, chat, msg)
}

func (r *Real) send(ctx context.Context, to types.JID, msg *waE2E.Message) (string, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return "", ErrNotConnected
	}
	resp, err := cli.SendMessage(ctx, to, msg)
	if err != nil {
		return "", err
	}
	return resp.ID, nil
}

func (r *Real) MarkRead(ctx context.Context, chat string, ids []string, sender string) error {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return ErrNotConnected
	}
	cj, err := types.ParseJID(chat)
	if err != nil {
		return err
	}
	var sj types.JID
	if sender != "" && sender != "me" {
		sj, _ = types.ParseJID(sender)
	}
	mids := make([]types.MessageID, len(ids))
	for i, id := range ids {
		mids[i] = id
	}
	return cli.MarkRead(ctx, mids, time.Now(), cj, sj)
}

func (r *Real) GetContacts(ctx context.Context) ([]Contact, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || cli.Store == nil || cli.Store.Contacts == nil {
		return nil, nil
	}
	all, err := cli.Store.Contacts.GetAllContacts(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]Contact, 0, len(all))
	for jid, info := range all {
		name := firstName(info.FullName, info.PushName, info.BusinessName, info.FirstName)
		c := Contact{JID: jid.ToNonAD().String(), Name: name}
		if jid.Server == types.HiddenUserServer {
			c.LID = c.JID
		} else {
			c.PN = c.JID
		}
		out = append(out, c)
	}
	return out, nil
}

func (r *Real) GetJoinedGroups(ctx context.Context) ([]Group, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return nil, ErrNotConnected
	}
	gs, err := cli.GetJoinedGroups(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]Group, 0, len(gs))
	for _, g := range gs {
		gg := Group{JID: g.JID.ToNonAD().String(), Name: g.Name}
		for _, p := range g.Participants {
			pp := Participant{JID: p.JID.ToNonAD().String(), PN: p.PhoneNumber.ToNonAD().String(), Name: p.DisplayName}
			if !p.LID.IsEmpty() {
				pp.JID = p.LID.ToNonAD().String()
			}
			if p.IsSuperAdmin {
				pp.Role = "superadmin"
			} else if p.IsAdmin {
				pp.Role = "admin"
			}
			gg.Participants = append(gg.Participants, pp)
		}
		out = append(out, gg)
	}
	return out, nil
}

func (r *Real) FetchAppState(ctx context.Context) error {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return nil
	}
	for _, name := range []appstate.WAPatchName{
		appstate.WAPatchCriticalUnblockLow,
		appstate.WAPatchRegularHigh,
		appstate.WAPatchRegularLow,
		appstate.WAPatchRegular,
	} {
		_ = cli.FetchAppState(ctx, name, false, false)
	}
	return nil
}

func (r *Real) GetProfileIcon(ctx context.Context, jid string) (*ProfileIcon, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil || !cli.IsConnected() {
		return nil, ErrNotConnected
	}
	parsed, err := types.ParseJID(jid)
	if err != nil {
		return nil, err
	}
	info, err := cli.GetProfilePictureInfo(ctx, parsed, &whatsmeow.GetProfilePictureParams{Preview: true})
	if err != nil || info == nil || info.URL == "" {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, info.URL, nil)
	if err != nil {
		return nil, err
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("icon http %d", resp.StatusCode)
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	if err != nil {
		return nil, err
	}
	ext := ".jpg"
	ct := resp.Header.Get("Content-Type")
	if strings.Contains(ct, "png") {
		ext = ".png"
	} else if strings.Contains(ct, "webp") {
		ext = ".webp"
	}
	return &ProfileIcon{Data: data, Ext: ext}, nil
}

func (r *Real) onEvent(raw any) {
	switch evt := raw.(type) {
	case *events.Connected:
		r.handler.Emit(Event{Type: EvtConnected, Me: r.Me()})
	case *events.Disconnected:
		r.handler.Emit(Event{Type: EvtDisconnected})
	case *events.LoggedOut:
		r.handler.Emit(Event{Type: EvtLoggedOut, Reason: evt.Reason.String()})
	case *events.PairSuccess:
		r.handler.Emit(Event{Type: EvtPaired, Me: evt.LID.ToNonAD().String()})
	case *events.PairError:
		msg := "pair failed"
		if evt.Error != nil {
			msg = evt.Error.Error()
		}
		r.handler.Emit(Event{Type: EvtPairError, Message: msg})
	case *events.PairPasskeyRequest:
		r.handler.Emit(Event{Type: EvtPairError, Message: "passkey required"})
	// *events.QR is owned by GetQRChannel in Pair — do not re-emit Codes[0] here.
	case *events.Message:
		r.emitMessage(evt)
	case *events.Receipt:
		r.emitReceipt(evt)
	case *events.HistorySync:
		r.emitHistory(evt)
	case *events.JoinedGroup:
		r.handler.Emit(Event{
			Type: EvtMeta,
			Chat: evt.JID.ToNonAD().String(),
			Action: func() string {
				if evt.Type == "new" {
					return "join"
				}
				return "join"
			}(),
			Name: evt.Name,
		})
		r.handler.Emit(Event{Type: EvtContact, Contact: Contact{JID: evt.JID.ToNonAD().String(), Name: evt.Name}})
	case *events.GroupInfo:
		r.emitGroupInfo(evt)
	case *events.Contact:
		name := ""
		handle := ""
		if evt.Action != nil {
			name = evt.Action.GetFullName()
			if name == "" {
				name = evt.Action.GetFirstName()
			}
			handle = evt.Action.GetUsername()
		}
		r.handler.Emit(Event{Type: EvtContact, Contact: Contact{JID: evt.JID.ToNonAD().String(), Name: name, Handle: handle}})
	case *events.PushName:
		r.handler.Emit(Event{Type: EvtContact, Contact: Contact{JID: evt.JID.ToNonAD().String(), Name: evt.NewPushName}})
	case *events.Picture:
		r.handler.Emit(Event{Type: EvtMeta, Chat: evt.JID.ToNonAD().String(), Action: "icon"})
	}
}

func (r *Real) emitMessage(evt *events.Message) {
	if evt == nil {
		return
	}
	evt.UnwrapRaw()
	info := evt.Info
	chat := info.Chat.ToNonAD().String()
	sender := info.Sender.ToNonAD().String()
	ev := Event{
		Type:     EvtMessage,
		Chat:     chat,
		ID:       info.ID,
		Sender:   sender,
		FromMe:   info.IsFromMe,
		ViewOnce: evt.IsViewOnce,
		Name:     info.PushName,
	}
	if !info.SenderAlt.IsEmpty() && info.Sender.Server == types.HiddenUserServer {
		ev.PN = info.SenderAlt.ToNonAD().String()
	} else if !info.RecipientAlt.IsEmpty() {
		ev.PN = info.RecipientAlt.ToNonAD().String()
	}
	msg := evt.Message
	if msg == nil {
		ev.Kind = "unknown"
		r.handler.Emit(ev)
		return
	}
	if evt.IsViewOnce {
		ev.Kind = "unknown"
		ev.Label = "view_once"
		r.handler.Emit(ev)
		return
	}
	switch {
	case msg.GetConversation() != "":
		ev.Kind = "text"
		ev.Text = msg.GetConversation()
	case msg.GetExtendedTextMessage() != nil:
		ev.Kind = "text"
		ev.Text = msg.GetExtendedTextMessage().GetText()
	case msg.GetImageMessage() != nil:
		ev.Kind = "image"
		ev.Text = msg.GetImageMessage().GetCaption()
		ev.MIME = msg.GetImageMessage().GetMimetype()
		ev.MediaRef = msg.GetImageMessage()
	case msg.GetVideoMessage() != nil:
		ev.Kind = "video"
		ev.Text = msg.GetVideoMessage().GetCaption()
		ev.MIME = msg.GetVideoMessage().GetMimetype()
		ev.MediaRef = msg.GetVideoMessage()
	case msg.GetAudioMessage() != nil:
		ev.Kind = "audio"
		ev.MIME = msg.GetAudioMessage().GetMimetype()
		ev.MediaRef = msg.GetAudioMessage()
	case msg.GetDocumentMessage() != nil:
		ev.Kind = "document"
		ev.Text = msg.GetDocumentMessage().GetCaption()
		ev.MIME = msg.GetDocumentMessage().GetMimetype()
		ev.MediaRef = msg.GetDocumentMessage()
	case msg.GetStickerMessage() != nil:
		ev.Kind = "sticker"
		ev.MIME = msg.GetStickerMessage().GetMimetype()
		ev.MediaRef = msg.GetStickerMessage()
	case msg.GetLocationMessage() != nil:
		ev.Kind = "location"
		ev.Lat = msg.GetLocationMessage().GetDegreesLatitude()
		ev.Lng = msg.GetLocationMessage().GetDegreesLongitude()
		ev.LocName = msg.GetLocationMessage().GetName()
		ev.LocAddr = msg.GetLocationMessage().GetAddress()
	case msg.GetLiveLocationMessage() != nil:
		ev.Kind = "location"
		ev.Lat = msg.GetLiveLocationMessage().GetDegreesLatitude()
		ev.Lng = msg.GetLiveLocationMessage().GetDegreesLongitude()
	case msg.GetReactionMessage() != nil:
		ev.Kind = "reaction"
		ev.Emoji = msg.GetReactionMessage().GetText()
		if k := msg.GetReactionMessage().GetKey(); k != nil {
			ev.Target = k.GetID()
		}
	default:
		ev.Kind = "unknown"
	}
	r.handler.Emit(ev)
}

func (r *Real) Download(ctx context.Context, ref any) ([]byte, error) {
	r.mu.Lock()
	cli := r.cli
	r.mu.Unlock()
	if cli == nil {
		return nil, ErrNotConnected
	}
	dm, ok := ref.(whatsmeow.DownloadableMessage)
	if !ok || dm == nil {
		return nil, fmt.Errorf("invalid media ref")
	}
	return cli.Download(ctx, dm)
}

func (r *Real) emitReceipt(evt *events.Receipt) {
	ack := "delivered"
	switch evt.Type {
	case types.ReceiptTypeRead, types.ReceiptTypeReadSelf:
		ack = "read"
	case types.ReceiptTypePlayed, types.ReceiptTypePlayedSelf:
		ack = "played"
	case types.ReceiptTypeDelivered, types.ReceiptTypeSender:
		ack = "delivered"
	default:
		return
	}
	ids := make([]string, len(evt.MessageIDs))
	copy(ids, evt.MessageIDs)
	r.handler.Emit(Event{
		Type:   EvtReceipt,
		Chat:   evt.Chat.ToNonAD().String(),
		IDs:    ids,
		Ack:    ack,
		Sender: evt.Sender.ToNonAD().String(),
	})
}

func (r *Real) emitHistory(evt *events.HistorySync) {
	if evt == nil || evt.Data == nil {
		return
	}
	h := HistorySync{}
	for _, m := range evt.Data.GetPhoneNumberToLidMappings() {
		h.Mappings = append(h.Mappings, Mapping{LID: m.GetLidJID(), PN: m.GetPnJID()})
	}
	for _, p := range evt.Data.GetPushnames() {
		h.PushNames = append(h.PushNames, PushName{JID: p.GetID(), Name: p.GetPushname()})
	}
	for _, c := range evt.Data.GetConversations() {
		conv := Conversation{
			ID:       c.GetID(),
			Name:     firstName(c.GetName(), c.GetDisplayName()),
			Handle:   c.GetUsername(),
			Archived: c.GetArchived(),
			Pinned:   c.GetPinned() > 0,
			PN:       c.GetPnJID(),
			LID:      c.GetLidJID(),
		}
		for _, p := range c.GetParticipant() {
			conv.Participants = append(conv.Participants, Participant{JID: p.GetUserJID()})
		}
		// Intentionally ignore c.GetMessages() — headers only.
		h.Conversations = append(h.Conversations, conv)
		_ = c.GetMessages
	}
	for _, ic := range evt.Data.GetInlineContacts() {
		h.InlineContacts = append(h.InlineContacts, Contact{
			LID:    ic.GetLidJID(),
			PN:     ic.GetPnJID(),
			JID:    firstName(ic.GetLidJID(), ic.GetPnJID()),
			Name:   firstName(ic.GetFullName(), ic.GetFirstName()),
			Handle: ic.GetUsername(),
		})
	}
	for _, a := range evt.Data.GetAccounts() {
		if a.GetIsUsernameDeleted() {
			continue
		}
		if u := a.GetUsername(); u != "" {
			h.SelfHandle = u
			break
		}
	}
	r.handler.Emit(Event{Type: EvtHistory, History: h})
}

func (r *Real) emitGroupInfo(evt *events.GroupInfo) {
	chat := evt.JID.ToNonAD().String()
	emit := func(action, name string) {
		r.handler.Emit(Event{Type: EvtMeta, Chat: chat, Action: action, Name: name})
	}
	if evt.Name != nil {
		emit("rename", evt.Name.Name)
	}
	if evt.Topic != nil {
		emit("topic", evt.Topic.Topic)
	}
	for range evt.Join {
		emit("join", "")
	}
	for range evt.Leave {
		emit("leave", "")
	}
	for range evt.Promote {
		emit("promote", "")
	}
	for range evt.Demote {
		emit("demote", "")
	}
	if evt.Delete != nil && evt.Delete.Deleted {
		r.handler.Emit(Event{Type: EvtRemove, Chat: chat})
	}
}

func replyContext(chat, id, by string) *waE2E.ContextInfo {
	if id == "" {
		return nil
	}
	ci := &waE2E.ContextInfo{StanzaID: proto.String(id)}
	if by != "" && by != "me" {
		ci.Participant = proto.String(by)
	}
	if chat != "" {
		ci.RemoteJID = proto.String(chat)
	}
	return ci
}

func firstName(ss ...string) string {
	for _, s := range ss {
		if s != "" {
			return s
		}
	}
	return ""
}
