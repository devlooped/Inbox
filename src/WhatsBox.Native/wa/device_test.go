package wa

import (
	"strings"
	"testing"

	"go.mau.fi/whatsmeow/proto/waCompanionReg"
	"go.mau.fi/whatsmeow/store"
)

func TestSetDeviceNameAppliesAndDefaults(t *testing.T) {
	prev := DeviceName()
	t.Cleanup(func() { SetDeviceName(prev) })

	if got := SetDeviceName("  Acme Laptop  "); got != "Acme Laptop" {
		t.Fatalf("custom: got %q", got)
	}
	if DeviceName() != "Acme Laptop" {
		t.Fatalf("DeviceName()=%q", DeviceName())
	}
	if store.DeviceProps.GetPlatformType() != waCompanionReg.DeviceProps_DESKTOP {
		t.Fatalf("platform=%v", store.DeviceProps.GetPlatformType())
	}
	if store.BaseClientPayload.UserAgent.GetDevice() != "Acme Laptop" {
		t.Fatalf("user-agent device=%q", store.BaseClientPayload.UserAgent.GetDevice())
	}

	got := SetDeviceName(" \t ")
	want := DefaultDeviceName()
	if got != want {
		t.Fatalf("default: got %q want %q", got, want)
	}
	if !strings.HasPrefix(want, "whatsbox") {
		t.Fatalf("default should start with whatsbox: %q", want)
	}
}
