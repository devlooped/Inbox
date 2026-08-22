package wa

import "testing"

func TestReplyContextOneToOneSetsParticipantAndQuotedStub(t *testing.T) {
	ci := replyContext("142300973904104@lid", "3EB0C83A", "142300973904104@lid", "anda?")
	if ci == nil {
		t.Fatal("expected context")
	}
	if ci.GetStanzaID() != "3EB0C83A" {
		t.Fatalf("stanza=%q", ci.GetStanzaID())
	}
	if ci.GetParticipant() != "142300973904104@lid" {
		t.Fatalf("1:1 quote needs quoted author as participant, got %q", ci.GetParticipant())
	}
	if ci.GetRemoteJID() != "" {
		t.Fatalf("1:1 quote must not set remoteJid (renders as Group • contact), got %q", ci.GetRemoteJID())
	}
	if ci.QuotedMessage.GetConversation() != "anda?" {
		t.Fatalf("quoted stub=%q", ci.QuotedMessage.GetConversation())
	}
}

func TestReplyContextGroupSetsParticipantWithoutRemoteJID(t *testing.T) {
	ci := replyContext("12036342@g.us", "3EB0", "999@lid", "hello")
	if ci.GetParticipant() != "999@lid" {
		t.Fatalf("participant=%q", ci.GetParticipant())
	}
	if ci.GetRemoteJID() != "" {
		t.Fatalf("group in-chat quote must not set remoteJid (renders as Group • subject), got %q", ci.GetRemoteJID())
	}
	if ci.QuotedMessage.GetConversation() != "hello" {
		t.Fatalf("quoted stub=%q", ci.QuotedMessage.GetConversation())
	}
}

func TestReplyContextAlwaysAttachesQuotedStub(t *testing.T) {
	ci := replyContext("111@lid", "3EB0", "me", "")
	if ci.QuotedMessage == nil {
		t.Fatal("quoted stub required even when client omits text")
	}
}
