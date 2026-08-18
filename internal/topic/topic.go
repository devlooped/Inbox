package topic

import (
	"strings"
	"unicode"

	"go.mau.fi/whatsmeow/types"
)

const (
	Session   = "$session"
	Directory = "$directory"
)

func IsSystem(s string) bool {
	return s == Session || s == Directory
}

func IsReserved(s string) bool {
	return strings.HasPrefix(s, "$") && !IsSystem(s)
}

func CanonicalizeKnown(raw string) (string, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return "", errInvalid("empty")
	}
	if strings.HasPrefix(raw, "$") {
		if IsSystem(raw) {
			return raw, nil
		}
		return "", errInvalid(raw)
	}
	if strings.Contains(raw, "@") {
		jid, err := types.ParseJID(raw)
		if err != nil || jid.IsEmpty() || jid.User == "" {
			return "", errInvalid(raw)
		}
		jid = jid.ToNonAD()
		switch jid.Server {
		case types.GroupServer:
			return jid.String(), nil
		case types.HiddenUserServer, types.DefaultUserServer, types.LegacyUserServer, types.HostedServer, types.HostedLIDServer:
			return jid.String(), nil
		default:
			return "", errInvalid(raw)
		}
	}
	return "", errNotJID
}

type Kind int

const (
	KindInvalid Kind = iota
	KindSystem
	KindGroup
	KindLID
	KindPN
	KindPhone
)

type Parsed struct {
	Raw    string
	Kind   Kind
	JID    types.JID
	Phone  string
	Canonical string
}

var errNotJID = errInvalid("not a jid")

type parseError struct{ s string }

func (e parseError) Error() string { return e.s }

func errInvalid(s string) error { return parseError{s: s} }

func IsInvalid(err error) bool {
	_, ok := err.(parseError)
	return ok
}

func Parse(raw string) (Parsed, error) {
	raw = strings.TrimSpace(raw)
	p := Parsed{Raw: raw}
	if raw == "" {
		p.Kind = KindInvalid
		return p, errInvalid("empty")
	}
	if strings.HasPrefix(raw, "$") {
		if IsSystem(raw) {
			p.Kind = KindSystem
			p.Canonical = raw
			return p, nil
		}
		p.Kind = KindInvalid
		return p, errInvalid(raw)
	}
	if strings.Contains(raw, "@") {
		jid, err := types.ParseJID(raw)
		if err != nil || jid.IsEmpty() || jid.User == "" {
			p.Kind = KindInvalid
			return p, errInvalid(raw)
		}
		jid = jid.ToNonAD()
		p.JID = jid
		switch jid.Server {
		case types.GroupServer:
			p.Kind = KindGroup
			p.Canonical = jid.String()
			return p, nil
		case types.HiddenUserServer, types.HostedLIDServer:
			p.Kind = KindLID
			p.Canonical = types.NewJID(jid.User, types.HiddenUserServer).String()
			return p, nil
		case types.DefaultUserServer, types.LegacyUserServer, types.HostedServer:
			p.Kind = KindPN
			p.JID = types.NewJID(jid.User, types.DefaultUserServer)
			p.Canonical = p.JID.String()
			p.Phone = digits(jid.User)
			return p, nil
		default:
			p.Kind = KindInvalid
			return p, errInvalid(raw)
		}
	}
	phone := NormalizePhone(raw)
	if phone == "" {
		p.Kind = KindInvalid
		return p, errInvalid(raw)
	}
	p.Kind = KindPhone
	p.Phone = phone
	return p, nil
}

func NormalizePhone(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	var b strings.Builder
	for i, r := range raw {
		if i == 0 && r == '+' {
			continue
		}
		if unicode.IsDigit(r) {
			b.WriteRune(r)
			continue
		}
		if r == '-' || r == ' ' || r == '(' || r == ')' {
			continue
		}
		return ""
	}
	s := b.String()
	if len(s) < 5 {
		return ""
	}
	return s
}

func digits(s string) string {
	var b strings.Builder
	for _, r := range s {
		if unicode.IsDigit(r) {
			b.WriteRune(r)
		}
	}
	return b.String()
}

func PNJID(phone string) string {
	phone = digits(phone)
	if phone == "" {
		return ""
	}
	return types.NewJID(phone, types.DefaultUserServer).String()
}

func LIDJID(user string) string {
	return types.NewJID(user, types.HiddenUserServer).String()
}

func IsGroup(canonical string) bool {
	jid, err := types.ParseJID(canonical)
	return err == nil && jid.Server == types.GroupServer
}

func IsLID(canonical string) bool {
	jid, err := types.ParseJID(canonical)
	return err == nil && jid.Server == types.HiddenUserServer
}

func IsPN(canonical string) bool {
	jid, err := types.ParseJID(canonical)
	return err == nil && (jid.Server == types.DefaultUserServer || jid.Server == types.LegacyUserServer)
}

func SafeFile(canonical string) string {
	s := strings.TrimSpace(canonical)
	s = strings.ReplaceAll(s, "@", "_at_")
	s = strings.ReplaceAll(s, "/", "_")
	s = strings.ReplaceAll(s, "\\", "_")
	s = strings.ReplaceAll(s, ":", "_")
	s = strings.ReplaceAll(s, "..", "_")
	if s == "" || s == "." {
		return "unknown"
	}
	return s
}
