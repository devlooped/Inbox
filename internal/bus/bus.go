package bus

import (
	"sync"
)

const DefaultBound = 256

type Event map[string]any

type Bus struct {
	mu     sync.Mutex
	subs   map[string]struct{}
	queues map[string][]Event
	bound  int
	notify chan struct{}
	closed bool
}

func New(bound int) *Bus {
	if bound <= 0 {
		bound = DefaultBound
	}
	return &Bus{
		subs:   map[string]struct{}{"$session": {}},
		queues: map[string][]Event{},
		bound:  bound,
		notify: make(chan struct{}, 1),
	}
}

func (b *Bus) Bound() int { return b.bound }

func (b *Bus) Subscribe(topics ...string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, t := range topics {
		if t == "" {
			continue
		}
		b.subs[t] = struct{}{}
	}
}

func (b *Bus) Unsubscribe(topics ...string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, t := range topics {
		if t == "" || t == "$session" {
			continue
		}
		delete(b.subs, t)
		delete(b.queues, t)
	}
}

func (b *Bus) Move(from, to string) {
	if from == "" || to == "" || from == to {
		return
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if _, ok := b.subs[from]; ok {
		delete(b.subs, from)
		b.subs[to] = struct{}{}
		if q, ok := b.queues[from]; ok {
			delete(b.queues, from)
			b.queues[to] = append(b.queues[to], q...)
		}
	}
}

func (b *Bus) Clear() {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.subs = map[string]struct{}{"$session": {}}
	b.queues = map[string][]Event{}
}

func (b *Bus) Has(topic string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	_, ok := b.subs[topic]
	return ok
}

func (b *Bus) Topics() []string {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]string, 0, len(b.subs))
	if _, ok := b.subs["$session"]; ok {
		out = append(out, "$session")
	}
	if _, ok := b.subs["$directory"]; ok {
		out = append(out, "$directory")
	}
	for t := range b.subs {
		if t == "$session" || t == "$directory" {
			continue
		}
		out = append(out, t)
	}
	return sortTopics(out)
}

func (b *Bus) Push(ev Event) {
	if ev == nil {
		return
	}
	topic, _ := ev["topic"].(string)
	if topic == "" {
		return
	}
	var overflowTopic string
	var dropped int
	b.mu.Lock()
	if b.closed {
		b.mu.Unlock()
		return
	}
	if _, ok := b.subs[topic]; !ok {
		b.mu.Unlock()
		return
	}
	q := b.queues[topic]
	if len(q) >= b.bound {
		drop := len(q) - b.bound + 1
		if drop < 1 {
			drop = 1
		}
		q = q[drop:]
		overflowTopic = topic
		dropped = drop
	}
	b.queues[topic] = append(q, ev)
	b.mu.Unlock()
	b.kick()
	if overflowTopic != "" && overflowTopic != "$session" {
		// Never recurse into $session overflow to avoid a loop.
		b.Push(Event{
			"topic":   "$session",
			"kind":    "overflow",
			"dropped": dropped,
			"queue":   overflowTopic,
		})
	}
}

func (b *Bus) Notify() <-chan struct{} { return b.notify }

func (b *Bus) Drain() []Event {
	b.mu.Lock()
	defer b.mu.Unlock()
	var out []Event
	// $session first so overflow/status stay ahead of chat when both pending.
	order := make([]string, 0, len(b.queues))
	if _, ok := b.queues["$session"]; ok {
		order = append(order, "$session")
	}
	if _, ok := b.queues["$directory"]; ok {
		order = append(order, "$directory")
	}
	for t := range b.queues {
		if t == "$session" || t == "$directory" {
			continue
		}
		order = append(order, t)
	}
	for _, t := range order {
		out = append(out, b.queues[t]...)
		delete(b.queues, t)
	}
	return out
}

func (b *Bus) Close() {
	b.mu.Lock()
	b.closed = true
	b.mu.Unlock()
	b.kick()
}

func (b *Bus) Closed() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.closed
}

func (b *Bus) kick() {
	select {
	case b.notify <- struct{}{}:
	default:
	}
}

func sortTopics(in []string) []string {
	// tiny insertion sort to avoid extra imports in this file's hot path
	for i := 1; i < len(in); i++ {
		j := i
		for j > 0 && in[j] < in[j-1] {
			in[j], in[j-1] = in[j-1], in[j]
			j--
		}
	}
	// keep $session, $directory first regardless of sort
	var sess, dir bool
	rest := in[:0]
	for _, t := range in {
		switch t {
		case "$session":
			sess = true
		case "$directory":
			dir = true
		default:
			rest = append(rest, t)
		}
	}
	out := make([]string, 0, len(in))
	if sess {
		out = append(out, "$session")
	}
	if dir {
		out = append(out, "$directory")
	}
	// rest is already sorted relative to itself? after filter order preserved from sorted in
	out = append(out, rest...)
	return out
}
