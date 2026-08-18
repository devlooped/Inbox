package dirstore

import (
	"testing"
)

func TestNormalizeHandle(t *testing.T) {
	t.Parallel()
	cases := []struct{ in, want string }{
		{"", ""},
		{"-", ""},
		{"  ", ""},
		{"ada", "@ada"},
		{"@ada", "@ada"},
		{" @@ada ", "@ada"},
		{"@Ada", "@Ada"},
	}
	for _, c := range cases {
		if got := NormalizeHandle(c.in); got != c.want {
			t.Errorf("NormalizeHandle(%q)=%q want %q", c.in, got, c.want)
		}
	}
}

func TestHandleRoundTripAndListQuery(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	st, err := Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = st.Close() })

	if err := st.Upsert(Row{Topic: "999@lid", Kind: "user", Name: "Ada", Handle: "ada", PN: "1555@s.whatsapp.net"}); err != nil {
		t.Fatal(err)
	}
	row, ok, err := st.Get("999@lid")
	if err != nil || !ok {
		t.Fatalf("get: ok=%v err=%v", ok, err)
	}
	if row.Handle != "@ada" {
		t.Fatalf("handle=%q", row.Handle)
	}
	items, _, err := st.List("ada", "user", 10, "")
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 1 || items[0].Handle != "@ada" {
		t.Fatalf("list=%#v", items)
	}
}
