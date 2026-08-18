package sqliteutil

import (
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
)

func ChmodFiles(path string, mode os.FileMode) error {
	for _, suffix := range []string{"", "-wal", "-shm", "-journal"} {
		p := path + suffix
		if err := os.Chmod(p, mode); err != nil && !os.IsNotExist(err) {
			return fmt.Errorf("chmod %s: %w", filepath.Base(p), err)
		}
	}
	return nil
}

func RemoveFiles(path string) error {
	var first error
	for _, suffix := range []string{"", "-wal", "-shm", "-journal"} {
		p := path + suffix
		if err := os.Remove(p); err != nil && !os.IsNotExist(err) {
			if first == nil {
				first = fmt.Errorf("remove %s: %w", filepath.Base(p), err)
			}
		}
	}
	return first
}

func FileURI(path, rawQuery string) string {
	return (&url.URL{Scheme: "file", Path: sqliteFileURLPath(path), RawQuery: rawQuery}).String()
}

func sqliteFileURLPath(path string) string {
	if len(path) >= 3 && isASCIIAlpha(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/') {
		return "/" + strings.ReplaceAll(path, `\`, "/")
	}
	if strings.HasPrefix(path, `\\`) {
		return strings.ReplaceAll(path, `\`, "/")
	}
	return path
}

func isASCIIAlpha(value byte) bool {
	return value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z'
}
