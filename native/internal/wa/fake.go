package wa

import (
	"context"
	"fmt"
	"sync"
	"sync/atomic"
)

var _ Client = (*Fake)(nil)

// Fake is an in-process WhatsApp client for protocol tests. It never dials.
type Fake struct {
	mu        sync.Mutex
	paired    bool
	me        string
	connected bool
	closed    bool
	passkey   bool
	handler   HandlerMux

	phones   map[string]PhoneInfo
	contacts []Contact
	groups   []Group
	icons    map[string]ProfileIcon

	pairWait chan struct{}
	pairOnce sync.Once
	msgSeq   atomic.Uint64

	ConnectCalls  int
	DownloadCalls []any
	Dialed        bool
	Presence      []string
	Sent          []any
	ReadCalls     []ReadCall
	Populate      []Contact

	DropAfterPair bool
	ConnectHold   <-chan struct{}
}

// MediaBlob is a Fake download handle. Tests set Event.MediaRef to this
// (and leave Event.Media empty) so handleMessage must call Download.
type MediaBlob struct {
	Key  string
	Data []byte
}

type ReadCall struct {
	Chat   string
	IDs    []string
	Sender string
}

func NewFake() *Fake {
	return &Fake{
		phones:   map[string]PhoneInfo{},
		icons:    map[string]ProfileIcon{},
		pairWait: make(chan struct{}),
		me:       "111@lid",
	}
}

func (f *Fake) SetPaired(me string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.paired = true
	if me != "" {
		f.me = me
	}
}

func (f *Fake) SetPasskeyRequired(v bool) {
	f.mu.Lock()
	f.passkey = v
	f.mu.Unlock()
}

func (f *Fake) SetPhone(info PhoneInfo) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.phones[digits(info.Query)] = info
	if info.Query != "" {
		f.phones[info.Query] = info
	}
}

func (f *Fake) SetContacts(c []Contact) { f.mu.Lock(); f.contacts = c; f.mu.Unlock() }
func (f *Fake) SetGroups(g []Group)     { f.mu.Lock(); f.groups = g; f.mu.Unlock() }
func (f *Fake) SetIcon(jid string, ic ProfileIcon) {
	f.mu.Lock()
	f.icons[jid] = ic
	f.mu.Unlock()
}

func (f *Fake) CompletePair() {
	f.pairOnce.Do(func() { close(f.pairWait) })
}

func (f *Fake) ResetPairWait() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.pairWait = make(chan struct{})
	f.pairOnce = sync.Once{}
}

func (f *Fake) Inject(ev Event) { f.handler.Emit(ev) }

func (f *Fake) Close() error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.closed = true
	f.connected = false
	return nil
}

func (f *Fake) IsPaired() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.paired
}

func (f *Fake) Me() string {
	f.mu.Lock()
	defer f.mu.Unlock()
	if !f.paired {
		return ""
	}
	return f.me
}

func (f *Fake) IsConnected() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.connected
}

func (f *Fake) SetHandler(h Handler) { f.handler.Set(h) }

func (f *Fake) Connect(ctx context.Context) error {
	f.mu.Lock()
	f.ConnectCalls++
	if !f.paired {
		f.mu.Unlock()
		return f.Pair(ctx)
	}
	hold := f.ConnectHold
	f.mu.Unlock()
	if hold != nil {
		select {
		case <-hold:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	f.mu.Lock()
	f.Dialed = true
	f.connected = true
	f.mu.Unlock()
	f.handler.Emit(Event{Type: EvtConnected, Me: f.Me()})
	return ctx.Err()
}

func (f *Fake) Pair(ctx context.Context) error {
	f.mu.Lock()
	if f.paired {
		f.mu.Unlock()
		return f.Connect(ctx)
	}
	if f.passkey {
		f.mu.Unlock()
		f.handler.Emit(Event{Type: EvtPairError, Message: "passkey required"})
		return ErrPasskey
	}
	wait := f.pairWait
	f.mu.Unlock()

	f.handler.Emit(Event{Type: EvtQR, Code: "2@fake-qr"})
	select {
	case <-wait:
	case <-ctx.Done():
		return ctx.Err()
	}
	f.mu.Lock()
	f.paired = true
	f.connected = true
	f.Dialed = true
	me := f.me
	f.mu.Unlock()
	f.handler.Emit(Event{Type: EvtPaired, Me: me})
	f.handler.Emit(Event{Type: EvtConnected, Me: me})
	f.mu.Lock()
	drop := f.DropAfterPair
	f.mu.Unlock()
	if drop {
		f.mu.Lock()
		f.connected = false
		f.mu.Unlock()
		f.handler.Emit(Event{Type: EvtDisconnected, Reason: "drop-after-pair"})
	}
	return nil
}

func (f *Fake) Disconnect() {
	f.mu.Lock()
	f.connected = false
	f.mu.Unlock()
	f.handler.Emit(Event{Type: EvtDisconnected, Reason: "disconnect"})
}

func (f *Fake) Logout(ctx context.Context) error {
	_ = ctx
	f.mu.Lock()
	f.connected = false
	f.paired = false
	f.mu.Unlock()
	return nil
}

func (f *Fake) IsOnWhatsApp(ctx context.Context, phones []string) ([]PhoneInfo, error) {
	if !f.IsConnected() {
		return nil, ErrNotConnected
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]PhoneInfo, 0, len(phones))
	for _, p := range phones {
		if info, ok := f.phones[p]; ok {
			info.Query = p
			out = append(out, info)
			continue
		}
		if info, ok := f.phones[digits(p)]; ok {
			info.Query = p
			out = append(out, info)
			continue
		}
		out = append(out, PhoneInfo{Query: p, IsIn: false})
	}
	return out, ctx.Err()
}

func (f *Fake) SendText(ctx context.Context, req SendText) (string, error) {
	if err := f.requireOnline(ctx); err != nil {
		return "", err
	}
	id := f.nextID()
	f.mu.Lock()
	f.Sent = append(f.Sent, req)
	f.mu.Unlock()
	return id, nil
}

func (f *Fake) SendMedia(ctx context.Context, req SendMedia) (string, error) {
	if err := f.requireOnline(ctx); err != nil {
		return "", err
	}
	id := f.nextID()
	f.mu.Lock()
	f.Sent = append(f.Sent, req)
	f.mu.Unlock()
	return id, nil
}

func (f *Fake) SendReact(ctx context.Context, req SendReact) (string, error) {
	if err := f.requireOnline(ctx); err != nil {
		return "", err
	}
	id := f.nextID()
	f.mu.Lock()
	f.Sent = append(f.Sent, req)
	f.mu.Unlock()
	return id, nil
}

func (f *Fake) MarkRead(ctx context.Context, chat string, ids []string, sender string) error {
	if err := f.requireOnline(ctx); err != nil {
		return err
	}
	f.mu.Lock()
	f.ReadCalls = append(f.ReadCalls, ReadCall{Chat: chat, IDs: append([]string(nil), ids...), Sender: sender})
	f.mu.Unlock()
	return nil
}

func (f *Fake) GetContacts(ctx context.Context) ([]Contact, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := append([]Contact(nil), f.contacts...)
	out = append(out, f.Populate...)
	return out, ctx.Err()
}

func (f *Fake) GetJoinedGroups(ctx context.Context) ([]Group, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]Group(nil), f.groups...), ctx.Err()
}

func (f *Fake) FetchAppState(ctx context.Context) error { return ctx.Err() }

func (f *Fake) Download(ctx context.Context, ref any) ([]byte, error) {
	if ctx.Err() != nil {
		return nil, ctx.Err()
	}
	f.mu.Lock()
	f.DownloadCalls = append(f.DownloadCalls, ref)
	f.mu.Unlock()
	switch r := ref.(type) {
	case MediaBlob:
		return append([]byte(nil), r.Data...), nil
	case *MediaBlob:
		if r == nil {
			return nil, fmt.Errorf("nil media ref")
		}
		return append([]byte(nil), r.Data...), nil
	case []byte:
		return append([]byte(nil), r...), nil
	default:
		return nil, fmt.Errorf("unknown media ref %T", ref)
	}
}

func (f *Fake) DownloadCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.DownloadCalls)
}

func (f *Fake) GetProfileIcon(ctx context.Context, jid string) (*ProfileIcon, error) {
	if err := f.requireOnline(ctx); err != nil {
		return nil, err
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if ic, ok := f.icons[jid]; ok {
		cp := ic
		return &cp, nil
	}
	return nil, fmt.Errorf("no icon")
}

func (f *Fake) SendPresence(kind string) {
	f.mu.Lock()
	f.Presence = append(f.Presence, kind)
	f.mu.Unlock()
}

func (f *Fake) requireOnline(ctx context.Context) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if !f.IsConnected() {
		return ErrNotConnected
	}
	return nil
}

func (f *Fake) nextID() string {
	n := f.msgSeq.Add(1)
	return fmt.Sprintf("3EB0%08d", n)
}

func digits(s string) string {
	var b []rune
	for _, r := range s {
		if r >= '0' && r <= '9' {
			b = append(b, r)
		}
	}
	return string(b)
}
