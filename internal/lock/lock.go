package lock

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

type Lock struct {
	path string
	f    *os.File
}

var ErrLocked = errors.New("store locked")

func Acquire(storeDir string) (*Lock, error) {
	if err := os.MkdirAll(storeDir, 0o700); err != nil {
		return nil, fmt.Errorf("create store dir: %w", err)
	}
	_ = os.Chmod(storeDir, 0o700)
	path := filepath.Join(storeDir, "LOCK")
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return nil, fmt.Errorf("open lock file: %w", err)
	}
	if err := lockFile(f); err != nil {
		_ = f.Close()
		if isLockContention(err) {
			return nil, fmt.Errorf("%w: %v", ErrLocked, err)
		}
		return nil, fmt.Errorf("lock file: %w", err)
	}
	_ = f.Truncate(0)
	_, _ = f.Seek(0, 0)
	_, _ = fmt.Fprintf(f, "pid=%d\nacquired_at=%s\n", os.Getpid(), time.Now().Format(time.RFC3339Nano))
	_ = f.Sync()
	return &Lock{path: path, f: f}, nil
}

func (l *Lock) Release() error {
	if l == nil || l.f == nil {
		return nil
	}
	_ = unlockFile(l.f)
	err := l.f.Close()
	l.f = nil
	return err
}

func IsLocked(err error) bool {
	return errors.Is(err, ErrLocked)
}
