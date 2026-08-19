package wa

import (
	"testing"

	"go.mau.fi/whatsmeow/types/events"
)

func TestOnEventDoesNotForwardQR(t *testing.T) {
	var got []Event
	r := &Real{}
	r.handler.Set(func(ev Event) { got = append(got, ev) })

	r.onEvent(&events.QR{Codes: []string{"2@first", "2@second"}})

	for _, ev := range got {
		if ev.Type == EvtQR {
			t.Fatalf("onEvent must not emit QR (GetQRChannel is the exclusive path): %+v", ev)
		}
	}
	if len(got) != 0 {
		t.Fatalf("onEvent forwarded unexpected events for *events.QR: %#v", got)
	}
}
