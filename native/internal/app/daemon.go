package app

import (
	"context"
	"encoding/json"
	"io"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/devlooped/whatsbox/internal/bus"
	"github.com/devlooped/whatsbox/internal/dirstore"
	"github.com/devlooped/whatsbox/internal/files"
	"github.com/devlooped/whatsbox/internal/lock"
	"github.com/devlooped/whatsbox/internal/rpc"
	"github.com/devlooped/whatsbox/internal/sqliteutil"
	"github.com/devlooped/whatsbox/internal/topic"
	"github.com/devlooped/whatsbox/internal/wa"
)

type Options struct {
	StoreFlag string
	Factory   wa.Factory
	QueueSize int
	Log       io.Writer
}

type Daemon struct {
	opts Options

	mu          sync.Mutex
	initialized bool
	storeDir    string
	verbosity   string
	status      string // new | offline | online
	lk          *lock.Lock
	dir         *dirstore.Store
	files       *files.Dir
	wa          wa.Client
	bus         *bus.Bus
	eventsOn    bool
	autoReconnect bool
	reconnectMu   sync.Mutex
	populating    bool

	outMu sync.Mutex
	out   io.Writer
	log   io.Writer

	closed    chan struct{}
	closeOnce sync.Once
	cancel    context.CancelFunc
}

func New(opts Options) *Daemon {
	if opts.Factory == nil {
		opts.Factory = wa.OpenReal
	}
	if opts.QueueSize <= 0 {
		opts.QueueSize = bus.DefaultBound
	}
	return &Daemon{
		opts:       opts,
		verbosity:  "warn",
		status:     "new",
		bus:        bus.New(opts.QueueSize),
		log:        stderrOrDiscard(opts.Log),
		closed:     make(chan struct{}),
	}
}

func (d *Daemon) Close() { d.shutdown() }

func (d *Daemon) Run(ctx context.Context, in io.Reader, out io.Writer) error {
	ctx, cancel := context.WithCancel(ctx)
	d.cancel = cancel
	d.out = out
	go d.pump(ctx)
	sc := newScanner(in)
	for sc.Scan() {
		line := sc.Bytes()
		if len(strings.TrimSpace(string(line))) == 0 {
			continue
		}
		resp := d.Handle(ctx, append([]byte(nil), line...))
		if len(resp) == 0 {
			continue
		}
		if err := writeLine(out, &d.outMu, resp); err != nil {
			d.shutdown()
			return err
		}
	}
	err := sc.Err()
	d.shutdown()
	if err != nil {
		return err
	}
	return io.EOF
}

func (d *Daemon) shutdown() {
	d.mu.Lock()
	if d.cancel != nil {
		d.cancel()
	}
	d.autoReconnect = false
	cli := d.wa
	d.wa = nil
	d.mu.Unlock()
	if cli != nil {
		cli.Disconnect()
		_ = cli.Close()
	}
	d.mu.Lock()
	if d.dir != nil {
		_ = d.dir.Close()
		d.dir = nil
	}
	if d.lk != nil {
		_ = d.lk.Release()
		d.lk = nil
	}
	d.mu.Unlock()
	d.bus.Close()
	d.closeOnce.Do(func() { close(d.closed) })
}

func (d *Daemon) pump(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-d.closed:
			return
		case <-d.bus.Notify():
			d.flushEvents()
		}
	}
}

func (d *Daemon) flushEvents() {
	if d.out == nil {
		return
	}
	for _, ev := range d.bus.Drain() {
		_ = writeLine(d.out, &d.outMu, rpc.Event(ev))
	}
}

func (d *Daemon) Handle(ctx context.Context, line []byte) []byte {
	req, perr := rpc.Parse(line)
	if perr != nil {
		return rpc.ErrorLine(nil, perr)
	}
	result, err := d.dispatch(ctx, req)
	if err != nil {
		return rpc.ErrorLine(req.ID, err)
	}
	return rpc.Result(req.ID, result)
}

func (d *Daemon) dispatch(ctx context.Context, req *rpc.Request) (any, *rpc.Error) {
	if req.Method != "initialize" {
		d.mu.Lock()
		ok := d.initialized
		d.mu.Unlock()
		if !ok {
			return nil, rpc.Err(rpc.TokNotInitialized)
		}
	}
	switch req.Method {
	case "initialize":
		return d.initialize(ctx, req.Params)
	case "session.connect":
		return d.sessionConnect(ctx)
	case "session.pair":
		return d.sessionPair(ctx)
	case "session.disconnect":
		return d.sessionDisconnect()
	case "session.logout":
		return d.sessionLogout(ctx)
	case "session.status":
		return d.sessionStatus(), nil
	case "subscribe":
		return d.subscribe(ctx, req.Params)
	case "unsubscribe":
		return d.unsubscribe(req.Params)
	case "directory.list":
		return d.directoryList(req.Params)
	case "directory.get":
		return d.directoryGet(ctx, req.Params)
	case "messages.send":
		return d.messagesSend(ctx, req.Params)
	case "messages.read":
		return d.messagesRead(ctx, req.Params)
	default:
		return nil, rpc.Err(rpc.TokMethodNotFound)
	}
}

type initParams struct {
	Version    string   `json:"version"`
	Store      string   `json:"store"`
	Files      string   `json:"files"`
	Subscribe  []string `json:"subscribe"`
	Verbosity  string   `json:"verbosity"`
	Connect    *bool    `json:"connect"`
}

func (d *Daemon) initialize(ctx context.Context, raw json.RawMessage) (any, *rpc.Error) {
	d.mu.Lock()
	if d.initialized {
		d.mu.Unlock()
		return nil, rpc.Err(rpc.TokAlreadyInitialized)
	}
	d.mu.Unlock()

	var p initParams
	if err := rpc.DecodeParams(raw, &p); err != nil {
		return nil, err
	}
	if p.Version == "" {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "version is required")
	}
	if p.Version != rpc.Version {
		return nil, rpc.ErrData(rpc.TokUnsupportedVersion, map[string]any{"supported": []string{rpc.Version}})
	}
	store, err := d.resolveStore(p.Store)
	if err != nil {
		return nil, err
	}
	if e := requireAbs(p.Files, "files"); e != nil {
		return nil, e
	}
	if p.Verbosity != "" {
		switch p.Verbosity {
		case "error", "warn", "info", "debug":
			d.verbosity = p.Verbosity
		default:
			return nil, rpc.ErrData(rpc.TokInvalidParams, "verbosity")
		}
	} else {
		d.verbosity = "info"
	}

	lk, lerr := lock.Acquire(store)
	if lerr != nil {
		if lock.IsLocked(lerr) {
			return nil, rpc.Err(rpc.TokStoreLocked)
		}
		return nil, rpc.ErrData(rpc.TokInvalidParams, lerr.Error())
	}
	dir, derr := dirstore.Open(store)
	if derr != nil {
		_ = lk.Release()
		return nil, rpc.ErrData(rpc.TokInvalidParams, derr.Error())
	}
	_ = dirstore.TouchSessionDB(store)
	fd, ferr := files.Open(p.Files)
	if ferr != nil {
		_ = dir.Close()
		_ = lk.Release()
		if e, ok := ferr.(*rpc.Error); ok {
			return nil, e
		}
		return nil, rpc.ErrData(rpc.TokInvalidParams, ferr.Error())
	}
	cli, cerr := d.opts.Factory(store)
	if cerr != nil {
		_ = dir.Close()
		_ = lk.Release()
		return nil, rpc.ErrData(rpc.TokInvalidParams, cerr.Error())
	}
	cli.SetHandler(d.onWA)

	d.mu.Lock()
	d.storeDir = store
	d.lk = lk
	d.dir = dir
	d.files = fd
	d.wa = cli
	if cli.IsPaired() {
		d.status = "offline"
	} else {
		d.status = "new"
	}
	d.bus.Subscribe(topic.Session)
	d.mu.Unlock()

	if len(p.Subscribe) > 0 {
		if _, err := d.applySubscribe(ctx, p.Subscribe, false); err != nil {
			d.rollbackInit()
			return nil, err
		}
	}

	d.mu.Lock()
	d.initialized = true
	d.eventsOn = true
	d.mu.Unlock()

	connect := p.Connect != nil && *p.Connect
	if connect {
		return d.sessionConnect(ctx)
	}
	st := d.statusSnapshot(true)
	return st, nil
}

func (d *Daemon) rollbackInit() {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.initialized = false
	d.eventsOn = false
	if d.wa != nil {
		_ = d.wa.Close()
		d.wa = nil
	}
	if d.dir != nil {
		_ = d.dir.Close()
		d.dir = nil
	}
	if d.lk != nil {
		_ = d.lk.Release()
		d.lk = nil
	}
}

func (d *Daemon) resolveStore(param string) (string, *rpc.Error) {
	flagStore := strings.TrimSpace(d.opts.StoreFlag)
	param = strings.TrimSpace(param)
	switch {
	case flagStore != "" && param == "":
		if e := requireAbs(flagStore, "store"); e != nil {
			return "", e
		}
		return filepath.Clean(flagStore), nil
	case flagStore == "" && param != "":
		if e := requireAbs(param, "store"); e != nil {
			return "", e
		}
		return filepath.Clean(param), nil
	case flagStore != "" && param != "":
		if e := requireAbs(flagStore, "store"); e != nil {
			return "", e
		}
		if e := requireAbs(param, "store"); e != nil {
			return "", e
		}
		if !SamePath(flagStore, param) {
			return "", rpc.Err(rpc.TokStoreMismatch)
		}
		return filepath.Clean(mustAbs(flagStore)), nil
	default:
		return "", rpc.Err(rpc.TokStoreRequired)
	}
}

type statusResult struct {
	Version string   `json:"version,omitempty"`
	Status  string   `json:"status"`
	Me      string   `json:"me,omitempty"`
	Self    string   `json:"self,omitempty"`
	Topics  []string `json:"topics"`
}

func (d *Daemon) sessionStatus() statusResult {
	return d.statusSnapshot(false)
}

func (d *Daemon) statusSnapshot(withVersion bool) statusResult {
	d.mu.Lock()
	st := d.status
	var me string
	if d.wa != nil {
		me = d.wa.Me()
	}
	d.mu.Unlock()
	if st == "new" {
		me = ""
	}
	res := statusResult{
		Status: st,
		Me:     me,
		Self:   me,
		Topics: d.bus.Topics(),
	}
	if withVersion {
		res.Version = rpc.Version
	}
	return res
}

func (d *Daemon) sessionConnect(ctx context.Context) (any, *rpc.Error) {
	d.mu.Lock()
	cli := d.wa
	st := d.status
	d.mu.Unlock()
	if cli == nil {
		return nil, rpc.Err(rpc.TokNotPaired)
	}
	if st == "online" && cli.IsConnected() {
		return d.statusSnapshot(true), nil
	}
	// Arm before Pair/Connect so a drop during or right after link is retried.
	d.mu.Lock()
	d.autoReconnect = true
	d.mu.Unlock()
	if !cli.IsPaired() || st == "new" {
		if err := cli.Pair(ctx); err != nil {
			d.mu.Lock()
			d.autoReconnect = false
			d.mu.Unlock()
			if err == wa.ErrPasskey {
				return nil, rpc.ErrData(rpc.TokPairError, "passkey required")
			}
			return nil, rpc.ErrData(rpc.TokPairError, err.Error())
		}
	} else if err := cli.Connect(ctx); err != nil {
		return nil, rpc.ErrData(rpc.TokDisconnected, err.Error())
	}
	if !cli.IsConnected() {
		return d.statusSnapshot(true), nil
	}
	d.mu.Lock()
	d.status = "online"
	d.mu.Unlock()
	d.emit(map[string]any{"topic": topic.Session, "kind": "online", "me": cli.Me()})
	go d.populate()
	res := d.statusSnapshot(true)
	return res, nil
}

func (d *Daemon) sessionPair(ctx context.Context) (any, *rpc.Error) {
	d.mu.Lock()
	cli := d.wa
	st := d.status
	d.mu.Unlock()
	if cli == nil {
		return nil, rpc.Err(rpc.TokNotPaired)
	}
	if cli.IsPaired() && st != "new" {
		return d.statusSnapshot(false), nil
	}
	d.mu.Lock()
	d.autoReconnect = true
	d.mu.Unlock()
	if err := cli.Pair(ctx); err != nil {
		d.mu.Lock()
		d.autoReconnect = false
		d.mu.Unlock()
		if err == wa.ErrPasskey {
			return nil, rpc.ErrData(rpc.TokPairError, "passkey required")
		}
		return nil, rpc.ErrData(rpc.TokPairError, err.Error())
	}
	if !cli.IsConnected() {
		return d.statusSnapshot(false), nil
	}
	d.mu.Lock()
	d.status = "online"
	d.mu.Unlock()
	d.emit(map[string]any{"topic": topic.Session, "kind": "online", "me": cli.Me()})
	go d.populate()
	return d.statusSnapshot(false), nil
}

func (d *Daemon) sessionDisconnect() (any, *rpc.Error) {
	d.mu.Lock()
	d.autoReconnect = false
	cli := d.wa
	d.mu.Unlock()
	if cli != nil {
		cli.Disconnect()
	}
	d.mu.Lock()
	if cli != nil && cli.IsPaired() {
		d.status = "offline"
	} else {
		d.status = "new"
	}
	d.mu.Unlock()
	return d.statusSnapshot(false), nil
}

func (d *Daemon) sessionLogout(ctx context.Context) (any, *rpc.Error) {
	d.wipeIdentity(ctx, "")
	return d.statusSnapshot(false), nil
}

func (d *Daemon) wipeIdentity(ctx context.Context, reason string) {
	d.mu.Lock()
	d.autoReconnect = false
	cli := d.wa
	dir := d.dir
	store := d.storeDir
	d.wa = nil
	d.dir = nil
	d.mu.Unlock()

	if cli != nil {
		if cli.IsConnected() {
			_ = cli.Logout(ctx)
		}
		_ = cli.Close()
	}
	if dir != nil {
		_ = dir.Close()
	}
	if store != "" {
		_ = sqliteutil.RemoveFiles(filepath.Join(store, "session.db"))
		_ = sqliteutil.RemoveFiles(filepath.Join(store, "whatsbox.db"))
	}
	d.bus.Clear()
	if store != "" && d.opts.Factory != nil {
		if ncli, err := d.opts.Factory(store); err == nil {
			ncli.SetHandler(d.onWA)
			d.mu.Lock()
			d.wa = ncli
			d.mu.Unlock()
		}
	}
	d.mu.Lock()
	d.status = "new"
	d.mu.Unlock()
	if reason != "" {
		d.emit(map[string]any{"topic": topic.Session, "kind": "logged_out", "reason": reason})
	}
}

func (d *Daemon) emit(ev map[string]any) {
	d.mu.Lock()
	on := d.eventsOn
	d.mu.Unlock()
	if !on {
		return
	}
	d.bus.Push(ev)
}

func (d *Daemon) online() bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.status == "online" && d.wa != nil && d.wa.IsConnected()
}

func (d *Daemon) client() wa.Client {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.wa
}

func (d *Daemon) store() *dirstore.Store {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.dir != nil {
		return d.dir
	}
	if d.storeDir == "" {
		return nil
	}
	st, err := dirstore.Open(d.storeDir)
	if err != nil {
		return nil
	}
	d.dir = st
	return st
}

func (d *Daemon) filesDir() *files.Dir {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.files
}

func (d *Daemon) me() string {
	cli := d.client()
	if cli == nil {
		return ""
	}
	return cli.Me()
}

func (d *Daemon) logf(level, format string, args ...any) {
	order := map[string]int{"error": 0, "warn": 1, "info": 2, "debug": 3}
	if order[level] > order[d.verbosity] {
		return
	}
	logf(d.log, level, format, args...)
}

func (d *Daemon) startReconnect() {
	d.reconnectMu.Lock()
	defer d.reconnectMu.Unlock()
	go func() {
		backoff := 500 * time.Millisecond
		for {
			d.mu.Lock()
			armed := d.autoReconnect
			cli := d.wa
			d.mu.Unlock()
			if !armed || cli == nil {
				return
			}
			if cli.IsConnected() {
				return
			}
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			err := cli.Connect(ctx)
			cancel()
			if err == nil {
				d.mu.Lock()
				d.status = "online"
				d.mu.Unlock()
				d.emit(map[string]any{"topic": topic.Session, "kind": "online", "me": cli.Me()})
				go d.populate()
				return
			}
			time.Sleep(backoff)
			if backoff < 30*time.Second {
				backoff *= 2
			}
		}
	}()
}
