package app_test

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/devlooped/whatsbox/internal/app"
	"github.com/devlooped/whatsbox/internal/dirstore"
	"github.com/devlooped/whatsbox/internal/rpc"
	"github.com/devlooped/whatsbox/internal/wa"
)

// testClient drives the shipped NDJSON Run loop with a fake WhatsApp client.
type testClient struct {
	t      *testing.T
	fake   *wa.Fake
	store  string
	files  string
	in     io.WriteCloser
	out    *bufio.Scanner
	cancel context.CancelFunc
	done   chan error

	mu     sync.Mutex
	events []map[string]any
	resps  map[string]map[string]any
	nextID int
	got    chan struct{}
	readMu sync.Mutex
	paused bool
}

func startDaemon(t *testing.T, fake *wa.Fake, queue int) *testClient {
	t.Helper()
	store := t.TempDir()
	filesDir := filepath.Join(t.TempDir(), "files")
	if err := os.MkdirAll(filesDir, 0o700); err != nil {
		t.Fatal(err)
	}
	rIn, wIn := io.Pipe()
	rOut, wOut := io.Pipe()
	ctx, cancel := context.WithCancel(context.Background())
	d := app.New(app.Options{
		Factory:   func(string) (wa.Client, error) { return fake, nil },
		QueueSize: queue,
		Log:       io.Discard,
	})
	done := make(chan error, 1)
	go func() { done <- d.Run(ctx, rIn, wOut) }()
	tc := &testClient{
		t:      t,
		fake:   fake,
		store:  store,
		files:  filesDir,
		in:     wIn,
		out:    newScan(rOut),
		cancel: cancel,
		done:   done,
		resps:  map[string]map[string]any{},
		got:    make(chan struct{}, 8),
	}
	go tc.readLoop()
	t.Cleanup(func() {
		tc.resumeReads()
		_ = wIn.Close()
		cancel()
		select {
		case <-done:
		case <-time.After(2 * time.Second):
		}
	})
	return tc
}

func newScan(r io.Reader) *bufio.Scanner {
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 0, 64*1024), 8<<20)
	return sc
}

func (c *testClient) pauseReads() {
	c.readMu.Lock()
	c.paused = true
	c.readMu.Unlock()
}
func (c *testClient) resumeReads() {
	c.readMu.Lock()
	c.paused = false
	c.readMu.Unlock()
}

func (c *testClient) readLoop() {
	for c.out.Scan() {
		for {
			c.readMu.Lock()
			p := c.paused
			c.readMu.Unlock()
			if !p {
				break
			}
			time.Sleep(5 * time.Millisecond)
		}
		line := append([]byte(nil), c.out.Bytes()...)
		var msg map[string]any
		if err := json.Unmarshal(line, &msg); err != nil {
			continue
		}
		c.mu.Lock()
		if method, _ := msg["method"].(string); method == "event" {
			if p, ok := msg["params"].(map[string]any); ok {
				c.events = append(c.events, p)
			}
		} else if id, ok := rawID(msg["id"]); ok {
			c.resps[id] = msg
		}
		c.mu.Unlock()
		select {
		case c.got <- struct{}{}:
		default:
		}
	}
}

func rawID(v any) (string, bool) {
	switch x := v.(type) {
	case string:
		return x, true
	case float64:
		return fmt.Sprintf("%.0f", x), true
	case json.Number:
		return x.String(), true
	default:
		return "", false
	}
}

func (c *testClient) call(method string, params any) (map[string]any, *rpcErr) {
	c.t.Helper()
	c.mu.Lock()
	c.nextID++
	id := fmt.Sprintf("%d", c.nextID)
	c.mu.Unlock()
	req := map[string]any{"jsonrpc": "2.0", "id": id, "method": method}
	if params != nil {
		req["params"] = params
	}
	b, err := json.Marshal(req)
	if err != nil {
		c.t.Fatal(err)
	}
	if _, err := c.in.Write(append(b, '\n')); err != nil {
		c.t.Fatal(err)
	}
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		c.mu.Lock()
		msg, ok := c.resps[id]
		c.mu.Unlock()
		if ok {
			if e, ok := msg["error"].(map[string]any); ok {
				re := &rpcErr{Message: fmt.Sprint(e["message"])}
				if code, ok := e["code"].(float64); ok {
					re.Code = int(code)
				}
				re.Data = e["data"]
				return nil, re
			}
			if res, ok := msg["result"].(map[string]any); ok {
				return res, nil
			}
			return map[string]any{}, nil
		}
		select {
		case <-c.got:
		case <-time.After(20 * time.Millisecond):
		}
	}
	c.t.Fatalf("timeout waiting for %s id=%s", method, id)
	return nil, nil
}

type rpcErr struct {
	Code    int
	Message string
	Data    any
}

func (c *testClient) mustCall(method string, params any) map[string]any {
	c.t.Helper()
	res, err := c.call(method, params)
	if err != nil {
		c.t.Fatalf("%s: %s (%d) %v", method, err.Message, err.Code, err.Data)
	}
	return res
}

func (c *testClient) waitEvent(kind string, timeout time.Duration) map[string]any {
	c.t.Helper()
	return c.waitEventWhere(timeout, func(ev map[string]any) bool { return ev["kind"] == kind })
}

func eventContents(ev map[string]any) []map[string]any {
	raw, ok := ev["contents"].([]any)
	if !ok {
		return nil
	}
	out := make([]map[string]any, 0, len(raw))
	for _, x := range raw {
		if m, ok := x.(map[string]any); ok {
			out = append(out, m)
		}
	}
	return out
}

func firstPart(ev map[string]any) map[string]any {
	cs := eventContents(ev)
	if len(cs) == 0 {
		return nil
	}
	return cs[0]
}

func contentText(ev map[string]any) string {
	var b strings.Builder
	for _, p := range eventContents(ev) {
		if p["type"] == "text" {
			if s, ok := p["text"].(string); ok {
				b.WriteString(s)
			}
		}
	}
	return b.String()
}

func textContents(text string) []map[string]any {
	return []map[string]any{{"type": "text", "text": text}}
}

func (c *testClient) waitEventWhere(timeout time.Duration, pred func(map[string]any) bool) map[string]any {
	c.t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		c.mu.Lock()
		for i, ev := range c.events {
			if pred(ev) {
				c.events = append(c.events[:i], c.events[i+1:]...)
				c.mu.Unlock()
				return ev
			}
		}
		c.mu.Unlock()
		select {
		case <-c.got:
		case <-time.After(20 * time.Millisecond):
		}
	}
	c.t.Fatalf("timeout waiting for event")
	return nil
}

func (c *testClient) eventsOf(kind string) []map[string]any {
	c.mu.Lock()
	defer c.mu.Unlock()
	var out []map[string]any
	for _, ev := range c.events {
		if ev["kind"] == kind {
			out = append(out, ev)
		}
	}
	return out
}

func (c *testClient) allEvents() []map[string]any {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]map[string]any(nil), c.events...)
}

func (c *testClient) init(extra map[string]any) (map[string]any, *rpcErr) {
	p := map[string]any{"version": "0.1", "store": c.store}
	for k, v := range extra {
		p[k] = v
	}
	return c.call("initialize", p)
}

func (c *testClient) mustInit(extra map[string]any) map[string]any {
	c.t.Helper()
	res, err := c.init(extra)
	if err != nil {
		c.t.Fatalf("initialize: %s (%d) %v", err.Message, err.Code, err.Data)
	}
	return res
}

func TestFlagsVersionAndHelp(t *testing.T) {
	var out, errb bytes.Buffer
	code := app.Main([]string{"whatsbox", "--version"}, bytes.NewReader(nil), &out, &errb)
	if code != 0 {
		t.Fatalf("version exit %d stderr=%s", code, errb.String())
	}
	if !strings.Contains(out.String(), "0.1") {
		t.Fatalf("version text: %q", out.String())
	}
	if bytes.Contains(out.Bytes(), []byte(`"jsonrpc"`)) {
		t.Fatalf("version wrote jsonrpc")
	}
	out.Reset()
	code = app.Main([]string{"whatsbox", "--help"}, bytes.NewReader(nil), &out, &errb)
	if code != 0 {
		t.Fatalf("help exit %d", code)
	}
	help := out.String()
	for _, flag := range []string{"--store", "--version", "--help"} {
		if !strings.Contains(help, flag) {
			t.Fatalf("help missing %s:\n%s", flag, help)
		}
	}
}

func TestNotInitializedAndAlreadyInitialized(t *testing.T) {
	fake := wa.NewFake()
	c := startDaemon(t, fake, 0)
	_, err := c.call("session.status", nil)
	if err == nil || err.Message != rpc.TokNotInitialized {
		t.Fatalf("want not_initialized, got %#v", err)
	}
	res, err := c.init(map[string]any{"connect": false})
	if err != nil {
		t.Fatal(err.Message)
	}
	if res["status"] != "new" {
		t.Fatalf("status=%v", res["status"])
	}
	if res["version"] != "0.1" {
		t.Fatalf("version=%v", res["version"])
	}
	if res["product"] != "whatsapp" || res["identity"] != "user" {
		t.Fatalf("product/identity=%v %v", res["product"], res["identity"])
	}
	caps, _ := res["capabilities"].(map[string]any)
	if caps["reply"] != "quote" || caps["read"] != "message" || caps["attachments"] != "single" {
		t.Fatalf("capabilities=%v", caps)
	}
	topics := asStrings(res["topics"])
	if !contains(topics, "$session") {
		t.Fatalf("topics=%v", topics)
	}
	if _, ok := res["me"]; ok && res["me"] != "" {
		t.Fatalf("me present on new: %v", res["me"])
	}
	_, err = c.init(nil)
	if err == nil || err.Message != rpc.TokAlreadyInitialized {
		t.Fatalf("want already_initialized, got %#v", err)
	}
}

func TestUnsupportedVersionAndStoreRules(t *testing.T) {
	fake := wa.NewFake()
	c := startDaemon(t, fake, 0)
	_, err := c.call("initialize", map[string]any{"version": "9.9", "store": c.store})
	if err == nil || err.Message != rpc.TokUnsupportedVersion {
		t.Fatalf("want unsupported_version, got %#v", err)
	}

	// store_required: no --store and no initialize.store
	c2 := startDaemon(t, wa.NewFake(), 0)
	_, err = c2.call("initialize", map[string]any{"version": "0.1"})
	if err == nil || err.Message != rpc.TokStoreRequired {
		t.Fatalf("want store_required, got %#v", err)
	}

	// store_mismatch via --store vs params
	flagStore := filepath.Join(t.TempDir(), "a")
	d := app.New(app.Options{
		StoreFlag: flagStore,
		Factory:   func(string) (wa.Client, error) { return wa.NewFake(), nil },
		Log:       io.Discard,
	})
	t.Cleanup(d.Close)
	other := filepath.Join(t.TempDir(), "b")
	_ = os.MkdirAll(other, 0o700)
	line, _ := json.Marshal(map[string]any{
		"jsonrpc": "2.0", "id": "1", "method": "initialize",
		"params": map[string]any{"version": "0.1", "store": other},
	})
	resp := decodeLine(t, d.Handle(context.Background(), line))
	errObj, _ := resp["error"].(map[string]any)
	if errObj == nil || errObj["message"] != rpc.TokStoreMismatch {
		t.Fatalf("want store_mismatch, got %v", resp)
	}

	// same absolute path from flag and params is ok
	same := t.TempDir()
	d2 := app.New(app.Options{
		StoreFlag: same,
		Factory:   func(string) (wa.Client, error) { return wa.NewFake(), nil },
		Log:       io.Discard,
	})
	t.Cleanup(d2.Close)
	line, _ = json.Marshal(map[string]any{
		"jsonrpc": "2.0", "id": "1", "method": "initialize",
		"params": map[string]any{"version": "0.1", "store": same, "connect": false},
	})
	resp = decodeLine(t, d2.Handle(context.Background(), line))
	if resp["error"] != nil {
		t.Fatalf("same path should succeed: %v", resp)
	}
	res, _ := resp["result"].(map[string]any)
	if res["status"] != "new" {
		t.Fatalf("status=%v", res["status"])
	}
}

func TestInitializeAcceptsDeviceName(t *testing.T) {
	c := startDaemon(t, wa.NewFake(), 0)
	res := c.mustInit(map[string]any{"connect": false, "deviceName": "Lab Box"})
	if res["status"] != "new" {
		t.Fatalf("status=%v", res["status"])
	}
}

func TestStoreLocked(t *testing.T) {
	store := t.TempDir()
	fake1 := wa.NewFake()
	d1 := app.New(app.Options{Factory: func(string) (wa.Client, error) { return fake1, nil }, Log: io.Discard})
	t.Cleanup(d1.Close)
	line, _ := json.Marshal(map[string]any{
		"jsonrpc": "2.0", "id": "1", "method": "initialize",
		"params": map[string]any{"version": "0.1", "store": store, "connect": false},
	})
	resp := decodeLine(t, d1.Handle(context.Background(), line))
	if resp["error"] != nil {
		t.Fatalf("first lock: %v", resp)
	}
	fake2 := wa.NewFake()
	d2 := app.New(app.Options{Factory: func(string) (wa.Client, error) { return fake2, nil }, Log: io.Discard})
	t.Cleanup(d2.Close)
	resp = decodeLine(t, d2.Handle(context.Background(), line))
	errObj, _ := resp["error"].(map[string]any)
	if errObj == nil || errObj["message"] != rpc.TokStoreLocked {
		t.Fatalf("want store_locked, got %v", resp)
	}
}

func TestConnectFalseDoesNotDial(t *testing.T) {
	fake := wa.NewFake()
	c := startDaemon(t, fake, 0)
	res, err := c.init(map[string]any{"connect": false})
	if err != nil {
		t.Fatal(err.Message)
	}
	if res["status"] != "new" {
		t.Fatalf("status=%v", res["status"])
	}
	if fake.Dialed || fake.ConnectCalls > 0 {
		t.Fatalf("dialed=%v calls=%d", fake.Dialed, fake.ConnectCalls)
	}
	st := c.mustCall("session.status", nil)
	if st["status"] != "new" {
		t.Fatalf("status=%v", st["status"])
	}
	if _, ok := st["me"]; ok && st["me"] != "" {
		t.Fatalf("me on new: %v", st["me"])
	}
}

func TestConnectNewEmitsQRThenOnline(t *testing.T) {
	fake := wa.NewFake()
	c := startDaemon(t, fake, 0)
	var res map[string]any
	var rpcE *rpcErr
	done := make(chan struct{})
	go func() {
		res, rpcE = c.init(map[string]any{"connect": true})
		close(done)
	}()
	qr := c.waitEvent("qr", 3*time.Second)
	if qr["code"] == "" {
		t.Fatalf("qr missing code: %v", qr)
	}
	fake.CompletePair()
	<-done
	if rpcE != nil {
		t.Fatal(rpcE.Message)
	}
	if res["status"] != "online" {
		t.Fatalf("status=%v", res["status"])
	}
	if res["me"] != "111@lid" {
		t.Fatalf("me=%v", res["me"])
	}
	if _, ok := res["self"]; ok {
		t.Fatalf("self must not be present: %v", res)
	}
	_ = c.waitEvent("paired", time.Second)
	_ = c.waitEvent("online", time.Second)
}

func TestPairNoopWhenLinkedAndDisconnectLogout(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	c := startDaemon(t, fake, 0)
	res := c.mustInit(map[string]any{"connect": false})
	if res["status"] != "offline" {
		t.Fatalf("status=%v", res["status"])
	}
	if res["me"] != "111@lid" {
		t.Fatalf("me missing: %v", res)
	}
	if _, ok := res["self"]; ok {
		t.Fatalf("self must not be present: %v", res)
	}
	pair := c.mustCall("session.pair", nil)
	if pair["status"] != "offline" {
		t.Fatalf("pair no-op status=%v", pair["status"])
	}
	if fake.Dialed {
		t.Fatalf("pair should not dial when already linked and we only no-op")
	}
	online := c.mustCall("session.connect", nil)
	if online["status"] != "online" {
		t.Fatalf("connect status=%v", online["status"])
	}
	off := c.mustCall("session.disconnect", nil)
	if off["status"] != "offline" {
		t.Fatalf("disconnect status=%v", off["status"])
	}
	if off["me"] != "111@lid" {
		t.Fatalf("me should remain after disconnect: %v", off)
	}
	if _, ok := off["self"]; ok {
		t.Fatalf("self must not be present: %v", off)
	}

	// logout wipes identity + dbs
	if err := os.WriteFile(filepath.Join(c.store, "session.db"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	out := c.mustCall("session.logout", nil)
	if out["status"] != "new" {
		t.Fatalf("logout status=%v", out["status"])
	}
	if _, ok := out["me"]; ok && out["me"] != "" {
		t.Fatalf("me after logout: %v", out["me"])
	}
	if _, err := os.Stat(filepath.Join(c.store, "session.db")); !os.IsNotExist(err) {
		t.Fatalf("session.db should be deleted after logout, stat err=%v", err)
	}
	if _, err := os.Stat(filepath.Join(c.store, "whatsbox.db")); !os.IsNotExist(err) {
		t.Fatalf("whatsbox.db should be deleted after logout, stat err=%v", err)
	}
	list := c.mustCall("directory.list", map[string]any{})
	items, _ := list["items"].([]any)
	if len(items) != 0 {
		t.Fatalf("directory not wiped: %v", items)
	}
}

func TestSubscribeNormalizeAndAtomic(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	c := startDaemon(t, fake, 0)
	_ = c.mustInit(map[string]any{"connect": true})

	res := c.mustCall("subscribe", map[string]any{
		"topics": []string{"999@lid", "12036342@g.us"},
	})
	topics := asStrings(res["topics"])
	if !contains(topics, "999@lid") || !contains(topics, "12036342@g.us") {
		t.Fatalf("canonical topics=%v", topics)
	}

	_, err := c.call("subscribe", map[string]any{"topics": []string{"$foo"}})
	if err == nil || err.Message != rpc.TokInvalidTopic {
		t.Fatalf("want invalid_topic, got %#v", err)
	}

	_, err = c.call("unsubscribe", map[string]any{"topics": []string{"$session"}})
	if err == nil || err.Message != rpc.TokInvalidTopic {
		t.Fatalf("want invalid_topic on unsub $session, got %#v", err)
	}

	for _, bad := range []string{"+15551234567", "Nosotros", "@ada"} {
		_, err = c.call("subscribe", map[string]any{"topics": []string{bad}})
		if err == nil || err.Message != rpc.TokInvalidTopic {
			t.Fatalf("want invalid_topic for %q, got %#v", bad, err)
		}
	}

	before := c.mustCall("session.status", nil)
	_, err = c.call("subscribe", map[string]any{"topics": []string{"+19990001111", "888@lid"}})
	if err == nil || err.Message != rpc.TokInvalidTopic {
		t.Fatalf("want invalid_topic for phone in batch, got %#v", err)
	}
	after := c.mustCall("session.status", nil)
	if fmt.Sprint(before["topics"]) != fmt.Sprint(after["topics"]) {
		t.Fatalf("partial apply: before=%v after=%v", before["topics"], after["topics"])
	}
}

func TestDirectoryListGetAndPopulate(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	fake.SetContacts([]wa.Contact{{LID: "999@lid", PN: "15551234567@s.whatsapp.net", Name: "Ada", Handle: "ada"}})
	fake.SetGroups([]wa.Group{{
		JID:  "12036342@g.us",
		Name: "Team",
		Participants: []wa.Participant{
			{JID: "999@lid", Name: "Ada"},
			{JID: "888@lid", Name: "Bob"},
		},
	}})
	fake.SetIcon("999@lid", wa.ProfileIcon{Data: []byte("jpegdata"), Ext: ".jpg"})
	c := startDaemon(t, fake, 0)
	res := c.mustInit(map[string]any{"connect": true, "files": c.files, "subscribe": []string{"$directory"}})
	if res["status"] != "online" {
		t.Fatalf("status=%v", res["status"])
	}
	// populate must not block connect; ready arrives after
	ready := c.waitEvent("ready", 3*time.Second)
	if ready["topic"] != "$directory" {
		t.Fatalf("ready topic=%v", ready["topic"])
	}
	// upserts must not include participants
	for _, ev := range c.eventsOf("upsert") {
		if _, ok := ev["participants"]; ok {
			t.Fatalf("upsert has participants: %v", ev)
		}
	}
	list := c.mustCall("directory.list", map[string]any{"query": "ada", "kind": "user", "limit": 10})
	items, _ := list["items"].([]any)
	if len(items) == 0 {
		t.Fatalf("list empty: %v", list)
	}
	// paging
	page := c.mustCall("directory.list", map[string]any{"limit": 1})
	pitems, _ := page["items"].([]any)
	if len(pitems) != 1 {
		t.Fatalf("limit 1 => %d", len(pitems))
	}

	got := c.mustCall("directory.get", map[string]any{"id": "+15551234567"})
	if got["topic"] != "999@lid" {
		t.Fatalf("get topic=%v", got["topic"])
	}
	if got["handle"] != "@ada" {
		t.Fatalf("handle=%v", got["handle"])
	}
	if got["icon"] == nil || !strings.HasPrefix(fmt.Sprint(got["icon"]), "in/_dir/") {
		t.Fatalf("icon=%v", got["icon"])
	}

	byHandle := c.mustCall("directory.list", map[string]any{"query": "@ada"})
	hitems, _ := byHandle["items"].([]any)
	if len(hitems) == 0 {
		t.Fatalf("list query handle empty: %v", byHandle)
	}

	iconsBefore := fake.IconCalls
	noIcon := c.mustCall("directory.get", map[string]any{"id": "999@lid", "icon": false})
	if _, ok := noIcon["icon"]; ok && noIcon["icon"] != "" {
		t.Fatalf("icon:false returned icon=%v", noIcon["icon"])
	}
	if fake.IconCalls != iconsBefore {
		t.Fatalf("icon:false fetched icon (%d -> %d)", iconsBefore, fake.IconCalls)
	}

	g := c.mustCall("directory.get", map[string]any{"id": "12036342@g.us"})
	if _, ok := g["participants"]; !ok {
		t.Fatalf("group get missing participants: %v", g)
	}
	_, err := c.call("directory.get", map[string]any{"id": "404@lid"})
	if err == nil || err.Message != rpc.TokNotFound {
		t.Fatalf("want not_found, got %#v", err)
	}

	// no files + icon:true
	c2fake := wa.NewFake()
	c2fake.SetPaired("111@lid")
	c2fake.SetContacts([]wa.Contact{{LID: "999@lid", Name: "Ada"}})
	c2 := startDaemon(t, c2fake, 0)
	_ = c2.mustInit(map[string]any{"connect": true, "subscribe": []string{"$directory"}})
	_ = c2.waitEvent("ready", 3*time.Second)
	_, err = c2.call("directory.get", map[string]any{"id": "999@lid", "icon": true})
	if err == nil || err.Message != rpc.TokFilesRequired {
		t.Fatalf("want files_required, got %#v", err)
	}
	okGet := c2.mustCall("directory.get", map[string]any{"id": "999@lid"})
	if _, has := okGet["icon"]; has && okGet["icon"] != "" {
		t.Fatalf("no files omitted icon: %v", okGet["icon"])
	}
}

func TestEventIdentityFields(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	fake.SetContacts([]wa.Contact{
		{LID: "999@lid", PN: "15551234567@s.whatsapp.net", Name: "Ada", Handle: "@ada"},
		{LID: "111@lid", Name: "MeName", Handle: "mehandle"},
	})
	fake.SetGroups([]wa.Group{{
		JID:  "12036342@g.us",
		Name: "Team",
		Participants: []wa.Participant{
			{JID: "999@lid", Name: "+1∙∙∙∙∙∙∙∙80"},
			{JID: "888@lid", Name: "Bob"},
		},
	}})
	c := startDaemon(t, fake, 0)
	_ = c.mustInit(map[string]any{"connect": true, "subscribe": []string{"$directory", "999@lid", "12036342@g.us"}})
	_ = c.waitEvent("ready", 3*time.Second)

	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: "t1", Sender: "999@lid", Kind: "text", Text: "hello"})
	txt := c.waitEvent("message", time.Second)
	if txt["id"] != "t1" || contentText(txt) != "hello" {
		t.Fatalf("text=%v", txt)
	}
	if _, ok := txt["pn"]; ok {
		t.Fatalf("pn on event: %v", txt)
	}
	if txt["handle"] != "@ada" || txt["topicName"] != "Ada" || txt["byName"] != "Ada" {
		t.Fatalf("1:1 identity=%v", txt)
	}

	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "12036342@g.us", ID: "g1", Sender: "999@lid", Kind: "text", Text: "hi team"})
	gt := c.waitEventWhere(time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "g1"
	})
	if gt["topicName"] != "Team" {
		t.Fatalf("group topicName=%v", gt["topicName"])
	}
	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "12036342@g.us", ID: "g2", Sender: "888@lid",
		Kind: "text", Text: "from bob", Name: "Bob",
	})
	g2 := c.waitEventWhere(time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "g2"
	})
	if g2["topicName"] != "Team" {
		t.Fatalf("group subject must not become the sender push name: %v", g2)
	}
	team := c.mustCall("directory.get", map[string]any{"id": "12036342@g.us"})
	if team["name"] != "Team" {
		t.Fatalf("directory group name after inbound=%v", team)
	}
	if gt["byName"] != "Ada" {
		t.Fatalf("group byName should be contact name, not redacted DisplayName: %v", gt)
	}
	if gt["handle"] != "@ada" {
		t.Fatalf("group handle=%v", gt["handle"])
	}

	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: "m1", Sender: "111@lid", FromMe: true, Kind: "text", Text: "from me"})
	mine := c.waitEventWhere(time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "m1"
	})
	if mine["by"] != "me" {
		t.Fatalf("from-me by=%v", mine["by"])
	}
	if mine["byName"] != "MeName" {
		t.Fatalf("from-me byName=%v", mine["byName"])
	}
	if mine["handle"] != "@mehandle" {
		t.Fatalf("from-me handle=%v", mine["handle"])
	}
	if mine["topicName"] != "Ada" {
		t.Fatalf("from-me topicName=%v", mine["topicName"])
	}

	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "999@lid", ID: "m2", Sender: "111@lid",
		FromMe: true, Kind: "text", Text: "yo", Name: "MeName", PN: "15551234567@s.whatsapp.net",
	})
	_ = c.waitEventWhere(time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "m2"
	})
	ada := c.mustCall("directory.get", map[string]any{"id": "999@lid"})
	if ada["name"] != "Ada" {
		t.Fatalf("from-me must not rename the peer: %v", ada)
	}
	self := c.mustCall("directory.get", map[string]any{"id": "111@lid"})
	if pn, _ := self["pn"].(string); strings.Contains(pn, "15551234567") {
		t.Fatalf("from-me must not map self LID to the peer phone: %v", self)
	}

	fake.Inject(wa.Event{
		Type: wa.EvtHistory,
		History: wa.HistorySync{
			InlineContacts: []wa.Contact{{LID: "888@lid", Name: "Cara", Handle: "cara"}},
			SelfHandle:     "selfuser",
		},
	})
	time.Sleep(50 * time.Millisecond)
	cara := c.mustCall("directory.get", map[string]any{"id": "888@lid"})
	if cara["handle"] != "@cara" || cara["name"] != "Cara" {
		t.Fatalf("inline contact=%v", cara)
	}
	meRow := c.mustCall("directory.get", map[string]any{"id": "111@lid"})
	if meRow["handle"] != "@selfuser" {
		t.Fatalf("me handle after history=%v", meRow)
	}
}

func TestRemapMovesPNSubscription(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	c := startDaemon(t, fake, 0)
	_ = c.mustInit(map[string]any{"connect": true})
	sub := c.mustCall("subscribe", map[string]any{"topics": []string{"15551234567@s.whatsapp.net"}})
	if !contains(asStrings(sub["topics"]), "15551234567@s.whatsapp.net") {
		t.Fatalf("expected PN topic while unmapped: %v", sub["topics"])
	}
	fake.Inject(wa.Event{Type: wa.EvtMapping, LID: "999@lid", PN: "15551234567@s.whatsapp.net"})
	remap := c.waitEvent("remap", 2*time.Second)
	if remap["from"] != "15551234567@s.whatsapp.net" || remap["to"] != "999@lid" {
		t.Fatalf("remap=%v", remap)
	}
	st := c.mustCall("session.status", nil)
	topics := asStrings(st["topics"])
	if contains(topics, "15551234567@s.whatsapp.net") {
		t.Fatalf("PN sub still present: %v", topics)
	}
	if !contains(topics, "999@lid") {
		t.Fatalf("LID sub missing: %v", topics)
	}
	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "15551234567@s.whatsapp.net",
		ID: "m1", Sender: "999@lid", Kind: "text", Text: "hi",
	})
	ev := c.waitEvent("message", 2*time.Second)
	if ev["topic"] != "999@lid" {
		t.Fatalf("chat event topic=%v", ev["topic"])
	}
	if ev["by"] != "999@lid" {
		t.Fatalf("by=%v", ev["by"])
	}
}

func TestHistorySyncBodiesDiscarded(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	c := startDaemon(t, fake, 0)
	_ = c.mustInit(map[string]any{"connect": true, "subscribe": []string{"$directory", "999@lid"}})
	const body = "HISTORY_BODY_MUST_NOT_PERSIST"
	fake.Inject(wa.Event{
		Type: wa.EvtHistory,
		History: wa.HistorySync{
			Conversations: []wa.Conversation{{ID: "999@lid", Name: "Ada", LID: "999@lid", PN: "15551234567@s.whatsapp.net"}},
			Messages:      []wa.HistMessage{{Chat: "999@lid", ID: "h1", Body: body}},
		},
	})
	// allow ingest
	time.Sleep(50 * time.Millisecond)
	for _, ev := range c.allEvents() {
		if ev["text"] == body || ev["id"] == "h1" {
			t.Fatalf("history body emitted: %v", ev)
		}
	}
	st, err := dirstore.Open(c.store)
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()
	if st.HasMessagesTable() {
		t.Fatal("messages table must not exist")
	}
	if st.ContainsText(body) {
		t.Fatal("history body persisted in whatsbox.db")
	}
}

func TestChatEventsDiscardOverflowSendRead(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	c := startDaemon(t, fake, 2)
	_ = c.mustInit(map[string]any{"connect": true, "files": c.files})
	_ = c.mustCall("subscribe", map[string]any{"topics": []string{"999@lid", "12036342@g.us"}})

	// unsubscribed inbound is not emitted, not written, and not downloaded
	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "777@lid", ID: "x1", Sender: "777@lid",
		Kind: "image", MediaRef: wa.MediaBlob{Key: "x1", Data: []byte("SECRETBYTES")}, MediaExt: ".jpg",
	})
	time.Sleep(30 * time.Millisecond)
	for _, ev := range c.allEvents() {
		if ev["topic"] == "777@lid" {
			t.Fatalf("unsubscribed event leaked: %v", ev)
		}
	}
	if matches, _ := filepath.Glob(filepath.Join(c.files, "in", "*", "x1*")); len(matches) != 0 {
		t.Fatalf("unsubscribed media written: %v", matches)
	}
	if fake.DownloadCount() != 0 {
		t.Fatalf("unsubscribed inbound downloaded %d times", fake.DownloadCount())
	}

	// subscribed text / ack / meta / unknown / reaction
	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: "t1", Sender: "999@lid", Kind: "text", Text: "hello"})
	txt := c.waitEvent("message", time.Second)
	if txt["by"] != "999@lid" || txt["id"] != "t1" || contentText(txt) != "hello" {
		t.Fatalf("text=%v", txt)
	}
	fake.Inject(wa.Event{Type: wa.EvtReceipt, Chat: "999@lid", IDs: []string{"t1"}, Ack: "read"})
	ack := c.waitEvent("ack", time.Second)
	if firstPart(ack)["ack"] != "read" {
		t.Fatalf("ack=%v", ack)
	}
	fake.Inject(wa.Event{Type: wa.EvtMeta, Chat: "12036342@g.us", Action: "rename", Name: "New"})
	meta := c.waitEvent("meta", time.Second)
	if firstPart(meta)["action"] != "rename" {
		t.Fatalf("meta=%v", meta)
	}
	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: "u1", Sender: "999@lid", Kind: "unknown", Label: "poll"})
	unk := c.waitEventWhere(time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "u1"
	})
	if firstPart(unk)["label"] != "poll" {
		t.Fatalf("unknown=%v", unk)
	}
	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: "r1", Sender: "999@lid", Kind: "reaction", Emoji: "👍", Target: "t1"})
	re := c.waitEvent("reaction", time.Second)
	if firstPart(re)["emoji"] != "👍" || firstPart(re)["target"] != "t1" {
		t.Fatalf("reaction=%v", re)
	}

	// Back up stdout so the per-topic queue actually overflows.
	c.pauseReads()
	for i := 0; i < 400; i++ {
		fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: fmt.Sprintf("f%d", i), Sender: "999@lid", Kind: "text", Text: "flood"})
	}
	c.resumeReads()
	ov := c.waitEvent("overflow", 3*time.Second)
	if ov["topic"] != "$session" && ov["kind"] != "overflow" {
		t.Fatalf("overflow=%v", ov)
	}

	// send text + echo
	sent := c.mustCall("messages.send", map[string]any{"to": "999@lid", "contents": textContents("yo")})
	if sent["topic"] != "999@lid" || sent["id"] == "" {
		t.Fatalf("send=%v", sent)
	}
	me := c.waitEventWhere(time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["by"] == "me" && ev["id"] == sent["id"]
	})
	if me["by"] != "me" || contentText(me) != "yo" {
		t.Fatalf("me event=%v", me)
	}

	// reply / react validation
	_, err := c.call("messages.send", map[string]any{"to": "999@lid", "contents": textContents("x"), "reply": map[string]any{"id": "t1"}})
	if err == nil || err.Message != rpc.TokInvalidParams {
		t.Fatalf("reply missing by: %#v", err)
	}
	quoted := c.mustCall("messages.send", map[string]any{
		"to": "999@lid", "contents": textContents("obvio que anda"),
		"reply": map[string]any{"id": "3EB0", "by": "me", "text": "anda?"},
	})
	if quoted["id"] == "" {
		t.Fatalf("quoted send=%v", quoted)
	}
	foundQuote := false
	for _, s := range fake.Sent {
		st, ok := s.(wa.SendText)
		if !ok || st.ReplyID != "3EB0" {
			continue
		}
		foundQuote = true
		if st.ReplyBy != "111@lid" || st.ReplyText != "anda?" {
			t.Fatalf("reply fields by=%q text=%q", st.ReplyBy, st.ReplyText)
		}
	}
	if !foundQuote {
		t.Fatal("expected SendText with reply stub")
	}
	groupQuote := c.mustCall("messages.send", map[string]any{
		"to": "12036342@g.us", "contents": textContents("quoted in group"),
		"reply": map[string]any{"id": "g1", "by": "me", "text": "hello"},
	})
	if groupQuote["id"] == "" {
		t.Fatalf("group quoted send=%v", groupQuote)
	}
	foundGroup := false
	for _, s := range fake.Sent {
		st, ok := s.(wa.SendText)
		if !ok || st.ReplyID != "g1" {
			continue
		}
		foundGroup = true
		if st.ReplyBy != "111@lid" || st.ReplyText != "hello" {
			t.Fatalf("group reply by=%q want 111@lid text=%q", st.ReplyBy, st.ReplyText)
		}
	}
	if !foundGroup {
		t.Fatal("expected group SendText with me resolved to LID")
	}
	_, err = c.call("messages.send", map[string]any{"to": "999@lid"})
	if err == nil || err.Message != rpc.TokInvalidParams {
		t.Fatalf("empty send: %#v", err)
	}
	reac := c.mustCall("messages.send", map[string]any{
		"to": "999@lid",
		"contents": []map[string]any{{"type": "reaction", "target": "t1", "by": "999@lid", "emoji": ""}},
	})
	if reac["id"] == "" {
		t.Fatalf("react result=%v", reac)
	}

	// path send
	outFile := filepath.Join(c.files, "out")
	_ = os.MkdirAll(outFile, 0o700)
	photo := filepath.Join(outFile, "photo.jpg")
	if err := os.WriteFile(photo, []byte("JPEG"), 0o600); err != nil {
		t.Fatal(err)
	}
	media := c.mustCall("messages.send", map[string]any{
		"to": "999@lid",
		"contents": []map[string]any{{"type": "image", "path": "out/photo.jpg"}},
	})
	if media["topic"] != "999@lid" {
		t.Fatalf("path send=%v", media)
	}
	_, err = c.call("messages.send", map[string]any{
		"to": "999@lid",
		"contents": []map[string]any{{"type": "image", "path": "../secret.jpg"}},
	})
	if err == nil || err.Message != rpc.TokPathEscape {
		t.Fatalf("path_escape: %#v", err)
	}
	_, err = c.call("messages.send", map[string]any{
		"to": "999@lid",
		"contents": []map[string]any{
			{"type": "image", "path": "out/photo.jpg"},
			{"type": "image", "path": "out/photo.jpg"},
		},
	})
	if err == nil || err.Message != rpc.TokUnsupported {
		t.Fatalf("extra blob: %#v", err)
	}
	if err != nil {
		data, _ := err.Data.(map[string]any)
		if data["capability"] != "attachments" {
			t.Fatalf("unsupported data=%v", err.Data)
		}
	}

	// inbound media: download via Client.Download, then write-then-notify
	beforeDL := fake.DownloadCount()
	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "999@lid", ID: "img1", Sender: "999@lid",
		Kind: "image", MediaRef: wa.MediaBlob{Key: "img1", Data: []byte("IMAGEDATA")}, MediaExt: ".jpg",
	})
	img := c.waitEventWhere(2*time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "img1"
	})
	rel, _ := firstPart(img)["path"].(string)
	if firstPart(img)["type"] != "image" || rel == "" || !strings.HasPrefix(rel, "in/") {
		t.Fatalf("inbound path=%v", img)
	}
	abs := filepath.Join(c.files, filepath.FromSlash(rel))
	got, err2 := os.ReadFile(abs)
	if err2 != nil || string(got) != "IMAGEDATA" {
		t.Fatalf("written file: %v %q", err2, got)
	}
	if fake.DownloadCount() != beforeDL+1 {
		t.Fatalf("subscribed+files should download once, got %d (was %d)", fake.DownloadCount(), beforeDL)
	}

	// view-once → unknown, no file, no download
	voBefore := fake.DownloadCount()
	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "999@lid", ID: "vo1", Sender: "999@lid",
		Kind: "image", ViewOnce: true, MediaRef: wa.MediaBlob{Key: "vo1", Data: []byte("VO")}, MediaExt: ".jpg",
	})
	vo := c.waitEventWhere(2*time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "vo1"
	})
	if firstPart(vo)["type"] != "unknown" || firstPart(vo)["label"] != "view_once" || firstPart(vo)["path"] != nil {
		t.Fatalf("view-once=%v", vo)
	}
	if matches, _ := filepath.Glob(filepath.Join(c.files, "in", "*", "vo1*")); len(matches) != 0 {
		t.Fatalf("view-once file written: %v", matches)
	}
	if fake.DownloadCount() != voBefore {
		t.Fatalf("view-once must not download, got %d", fake.DownloadCount())
	}

	// read without by (1:1 and groups)
	_, err = c.call("messages.read", map[string]any{"to": "12036342@g.us", "ids": []string{"t1"}})
	if err == nil || err.Message != rpc.TokInvalidParams {
		t.Fatalf("group read without by: %#v", err)
	}
	_, err = c.call("messages.read", map[string]any{"to": "999@lid", "ids": []string{"t1"}})
	if err == nil || err.Message != rpc.TokInvalidParams {
		t.Fatalf("1:1 read without by: %#v", err)
	}
	rd := c.mustCall("messages.read", map[string]any{"to": "12036342@g.us", "ids": []string{"t1"}, "by": "999@lid"})
	if rd["topic"] != "12036342@g.us" {
		t.Fatalf("read=%v", rd)
	}
	if n := len(fake.ReadCalls); n < 1 || fake.ReadCalls[n-1].Sender != "999@lid" {
		t.Fatalf("group MarkRead sender=%v", fake.ReadCalls)
	}
	rd1 := c.mustCall("messages.read", map[string]any{"to": "999@lid", "ids": []string{"t1"}, "by": "999@lid"})
	if rd1["topic"] != "999@lid" {
		t.Fatalf("1:1 read=%v", rd1)
	}
	if n := len(fake.ReadCalls); n < 1 || fake.ReadCalls[n-1].Sender != "" {
		t.Fatalf("1:1 MarkRead should ignore by, sender=%v", fake.ReadCalls)
	}

	// disconnected
	_ = c.mustCall("session.disconnect", nil)
	_, err = c.call("messages.send", map[string]any{"to": "999@lid", "contents": textContents("x")})
	if err == nil || err.Message != rpc.TokDisconnected {
		t.Fatalf("send offline: %#v", err)
	}
	_, err = c.call("messages.read", map[string]any{"to": "999@lid", "ids": []string{"t1"}, "by": "999@lid"})
	if err == nil || err.Message != rpc.TokDisconnected {
		t.Fatalf("read offline: %#v", err)
	}
}

func TestFilesRequiredOnSendPath(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPaired("111@lid")
	c := startDaemon(t, fake, 0)
	_ = c.mustInit(map[string]any{"connect": true})
	_, err := c.call("messages.send", map[string]any{
		"to": "999@lid",
		"contents": []map[string]any{{"type": "image", "path": "out/a.jpg"}},
	})
	if err == nil || err.Message != rpc.TokFilesRequired {
		t.Fatalf("want files_required, got %#v", err)
	}
	_ = c.mustCall("subscribe", map[string]any{"topics": []string{"999@lid"}})
	fake.Inject(wa.Event{
		Type: wa.EvtMessage, Chat: "999@lid", ID: "nf1", Sender: "999@lid",
		Kind: "image", MediaRef: wa.MediaBlob{Key: "nf1", Data: []byte("N")}, MediaExt: ".jpg",
	})
	img := c.waitEventWhere(2*time.Second, func(ev map[string]any) bool {
		return ev["kind"] == "message" && ev["id"] == "nf1"
	})
	if firstPart(img)["path"] != nil || firstPart(img)["error"] != "files_required" {
		t.Fatalf("no-files inbound: %v", img)
	}
	if fake.DownloadCount() != 0 {
		t.Fatalf("no-files inbound must not download, got %d", fake.DownloadCount())
	}
}

func TestConnectDoesNotReportOnlineAfterDrop(t *testing.T) {
	fake := wa.NewFake()
	hold := make(chan struct{})
	fake.DropAfterPair = true
	fake.ConnectHold = hold
	c := startDaemon(t, fake, 0)
	t.Cleanup(func() { close(hold) })

	var res map[string]any
	var rpcE *rpcErr
	done := make(chan struct{})
	go func() {
		res, rpcE = c.init(map[string]any{"connect": true})
		close(done)
	}()
	_ = c.waitEvent("qr", 3*time.Second)
	fake.CompletePair()
	<-done
	if rpcE != nil {
		t.Fatal(rpcE.Message)
	}
	if res["status"] == "online" {
		t.Fatalf("sessionConnect must not report online after post-pair drop: %v", res)
	}
	// Reconnect must have been armed before Pair returned, so Connect is retried
	// even though the socket dropped immediately after pair.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) && fake.ConnectCalls == 0 {
		time.Sleep(10 * time.Millisecond)
	}
	if fake.ConnectCalls == 0 {
		t.Fatal("expected auto-reconnect Connect after post-pair drop")
	}
}

func TestPairErrorPasskey(t *testing.T) {
	fake := wa.NewFake()
	fake.SetPasskeyRequired(true)
	c := startDaemon(t, fake, 0)
	_, err := c.init(map[string]any{"connect": true})
	if err == nil || err.Message != rpc.TokPairError {
		t.Fatalf("want pair_error, got %#v", err)
	}
}

func TestNoEventsBeforeInitialize(t *testing.T) {
	fake := wa.NewFake()
	c := startDaemon(t, fake, 0)
	fake.Inject(wa.Event{Type: wa.EvtMessage, Chat: "999@lid", ID: "early", Kind: "text", Text: "nope"})
	time.Sleep(30 * time.Millisecond)
	if evs := c.allEvents(); len(evs) != 0 {
		t.Fatalf("events before initialize: %v", evs)
	}
}

func asStrings(v any) []string {
	switch x := v.(type) {
	case []string:
		return x
	case []any:
		out := make([]string, 0, len(x))
		for _, e := range x {
			out = append(out, fmt.Sprint(e))
		}
		return out
	default:
		return nil
	}
}

func contains(ss []string, want string) bool {
	for _, s := range ss {
		if s == want {
			return true
		}
	}
	return false
}

func must(t *testing.T, res map[string]any, err *rpcErr) map[string]any {
	t.Helper()
	if err != nil {
		t.Fatal(err.Message)
	}
	return res
}

func decodeLine(t *testing.T, b []byte) map[string]any {
	t.Helper()
	var m map[string]any
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatal(err)
	}
	return m
}
