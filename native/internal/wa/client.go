package wa

import (
	"context"
	"errors"
	"sync"
)

var (
	ErrPasskey      = errors.New("passkey required")
	ErrNotConnected = errors.New("disconnected")
	ErrNotPaired    = errors.New("not_paired")
)

type Factory func(storeDir string) (Client, error)

type Client interface {
	Close() error
	IsPaired() bool
	Me() string
	IsConnected() bool
	Connect(ctx context.Context) error
	Pair(ctx context.Context) error
	Disconnect()
	Logout(ctx context.Context) error
	SetHandler(Handler)

	IsOnWhatsApp(ctx context.Context, phones []string) ([]PhoneInfo, error)
	SendText(ctx context.Context, req SendText) (string, error)
	SendMedia(ctx context.Context, req SendMedia) (string, error)
	SendReact(ctx context.Context, req SendReact) (string, error)
	MarkRead(ctx context.Context, chat string, ids []string, sender string) error

	GetContacts(ctx context.Context) ([]Contact, error)
	GetJoinedGroups(ctx context.Context) ([]Group, error)
	FetchAppState(ctx context.Context) error
	GetProfileIcon(ctx context.Context, jid string) (*ProfileIcon, error)
	// Download fetches inbound media bytes. The daemon must call this only
	// after the chat is subscribed and initialize.files is set.
	Download(ctx context.Context, ref any) ([]byte, error)
}

type Handler func(Event)

type EventType string

const (
	EvtQR           EventType = "qr"
	EvtPaired       EventType = "paired"
	EvtPairError    EventType = "pair_error"
	EvtConnected    EventType = "connected"
	EvtDisconnected EventType = "disconnected"
	EvtLoggedOut    EventType = "logged_out"
	EvtMessage      EventType = "message"
	EvtReceipt      EventType = "receipt"
	EvtMeta         EventType = "meta"
	EvtMapping      EventType = "mapping"
	EvtHistory      EventType = "history"
	EvtContact      EventType = "contact"
	EvtRemove       EventType = "remove"
)

type Event struct {
	Type    EventType
	Code    string
	Message string
	Me      string
	Reason  string

	Chat     string
	ID       string
	Sender   string
	FromMe   bool
	Kind     string
	Text     string
	PN       string
	ViewOnce bool
	Media    []byte
	MediaRef any
	MediaExt string
	MIME     string
	Lat      float64
	Lng      float64
	LocName  string
	LocAddr  string
	Emoji    string
	Target   string
	Label    string
	Ack      string
	IDs      []string
	Action   string
	Name     string
	LID      string

	History HistorySync
	Contact Contact
	Group   Group
}

type HistorySync struct {
	Conversations  []Conversation
	Mappings       []Mapping
	PushNames      []PushName
	InlineContacts []Contact
	SelfHandle     string
	// Messages are accepted on the wire from WhatsApp but must never be
	// persisted or emitted. Tests inject them to prove discard.
	Messages []HistMessage
}

type Conversation struct {
	ID           string
	Name         string
	Handle       string
	Archived     bool
	Pinned       bool
	PN           string
	LID          string
	Participants []Participant
}

type HistMessage struct {
	Chat string
	ID   string
	Body string
}

type Mapping struct {
	LID string
	PN  string
}

type PushName struct {
	JID  string
	Name string
}

type PhoneInfo struct {
	Query string
	IsIn  bool
	JID   string
	PN    string
}

type Contact struct {
	JID    string
	LID    string
	PN     string
	Name   string
	Handle string
}

type Group struct {
	JID          string
	Name         string
	Participants []Participant
}

type Participant struct {
	JID    string
	PN     string
	Name   string
	Handle string
	Role   string
}

type ProfileIcon struct {
	Data []byte
	Ext  string
}

type SendText struct {
	To      string
	Text    string
	ReplyID string
	ReplyBy string
}

type SendMedia struct {
	To       string
	Path     string
	Data     []byte
	MIME     string
	FileName string
	Caption  string
	ReplyID  string
	ReplyBy  string
	Kind     string
}

type SendReact struct {
	To    string
	ID    string
	By    string
	Emoji string
}

// HandlerMux is a small helper so adapters can swap handlers safely.
type HandlerMux struct {
	mu sync.Mutex
	h  Handler
}

func (m *HandlerMux) Set(h Handler) {
	m.mu.Lock()
	m.h = h
	m.mu.Unlock()
}

func (m *HandlerMux) Emit(ev Event) {
	m.mu.Lock()
	h := m.h
	m.mu.Unlock()
	if h != nil {
		h(ev)
	}
}
