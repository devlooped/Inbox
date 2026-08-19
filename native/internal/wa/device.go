package wa

import (
	"os"
	"strings"
	"sync"

	"go.mau.fi/whatsmeow/proto/waCompanionReg"
	"go.mau.fi/whatsmeow/store"
	"google.golang.org/protobuf/proto"
)

var deviceMu sync.Mutex

// DefaultDeviceName is "whatsbox on {hostname}", or "whatsbox" if the host is unknown.
func DefaultDeviceName() string {
	host, err := os.Hostname()
	host = strings.TrimSpace(host)
	if err != nil || host == "" {
		return "whatsbox"
	}
	return "whatsbox on " + host
}

// SetDeviceName publishes the companion name WhatsApp shows under Linked devices.
// Empty/whitespace falls back to DefaultDeviceName. The value is process-global
// (whatsmeow DeviceProps) and is read at pairing.
func SetDeviceName(name string) string {
	name = strings.TrimSpace(name)
	if name == "" {
		name = DefaultDeviceName()
	}
	deviceMu.Lock()
	defer deviceMu.Unlock()
	store.SetOSInfo(name, [3]uint32{0, 1, 0})
	store.DeviceProps.PlatformType = waCompanionReg.DeviceProps_DESKTOP.Enum()
	if store.BaseClientPayload != nil && store.BaseClientPayload.UserAgent != nil {
		store.BaseClientPayload.UserAgent.Device = proto.String(name)
		store.BaseClientPayload.UserAgent.Manufacturer = proto.String(name)
	}
	return name
}

// DeviceName is the name last applied by SetDeviceName (or whatsmeow's default).
func DeviceName() string {
	deviceMu.Lock()
	defer deviceMu.Unlock()
	return store.DeviceProps.GetOs()
}
