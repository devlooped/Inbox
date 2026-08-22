package files

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/devlooped/whatsbox/rpc"
	"github.com/devlooped/whatsbox/topic"
)

type Dir struct {
	Root string
}

func (d *Dir) Enabled() bool {
	return d != nil && d.Root != ""
}

func Open(abs string) (*Dir, error) {
	if abs == "" {
		return nil, nil
	}
	if !filepath.IsAbs(abs) {
		return nil, rpc.ErrData(rpc.TokInvalidParams, "files must be an absolute path")
	}
	if err := os.MkdirAll(abs, 0o700); err != nil {
		return nil, err
	}
	return &Dir{Root: abs}, nil
}

func (d *Dir) Resolve(rel string) (string, error) {
	if !d.Enabled() {
		return "", rpc.Err(rpc.TokFilesRequired)
	}
	rel = strings.TrimSpace(rel)
	if rel == "" {
		return "", rpc.ErrData(rpc.TokInvalidParams, "path is required")
	}
	if filepath.IsAbs(rel) {
		return "", rpc.Err(rpc.TokPathEscape)
	}
	clean := filepath.Clean(rel)
	if clean == "." || clean == ".." || strings.HasPrefix(clean, ".."+string(filepath.Separator)) {
		return "", rpc.Err(rpc.TokPathEscape)
	}
	abs := filepath.Join(d.Root, clean)
	root := filepath.Clean(d.Root)
	relToRoot, err := filepath.Rel(root, abs)
	if err != nil || strings.HasPrefix(relToRoot, "..") {
		return "", rpc.Err(rpc.TokPathEscape)
	}
	return abs, nil
}

func (d *Dir) WriteInbound(canonical, id, ext string, data []byte) (string, error) {
	if !d.Enabled() {
		return "", rpc.Err(rpc.TokFilesRequired)
	}
	safe := topic.SafeFile(canonical)
	dir := filepath.Join(d.Root, "in", safe)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
	if ext != "" && !strings.HasPrefix(ext, ".") {
		ext = "." + ext
	}
	name := id + ext
	abs := filepath.Join(dir, name)
	tmp := abs + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return "", err
	}
	if err := os.Rename(tmp, abs); err != nil {
		_ = os.Remove(tmp)
		return "", err
	}
	return slash("in/" + safe + "/" + name), nil
}

func (d *Dir) WriteIcon(canonical, ext string, data []byte) (string, error) {
	if !d.Enabled() {
		return "", rpc.Err(rpc.TokFilesRequired)
	}
	safe := topic.SafeFile(canonical)
	dir := filepath.Join(d.Root, "in", "_dir")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
	if ext != "" && !strings.HasPrefix(ext, ".") {
		ext = "." + ext
	}
	if ext == "" {
		ext = ".jpg"
	}
	name := safe + ext
	abs := filepath.Join(dir, name)
	tmp := abs + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return "", err
	}
	if err := os.Rename(tmp, abs); err != nil {
		_ = os.Remove(tmp)
		return "", err
	}
	return slash("in/_dir/" + name), nil
}

func slash(p string) string {
	return strings.ReplaceAll(p, "\\", "/")
}

func ExtForMIME(mime, name string) string {
	if e := strings.ToLower(filepath.Ext(name)); e != "" && e != "." {
		return e
	}
	switch strings.ToLower(mime) {
	case "image/jpeg":
		return ".jpg"
	case "image/png":
		return ".png"
	case "image/webp", "image/webp; codecs=vp8":
		return ".webp"
	case "image/gif":
		return ".gif"
	case "video/mp4":
		return ".mp4"
	case "audio/ogg", "audio/opus":
		return ".ogg"
	case "audio/mpeg":
		return ".mp3"
	case "application/pdf":
		return ".pdf"
	default:
		return ""
	}
}

func KindForPath(path string) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".jpg", ".jpeg", ".png", ".gif", ".bmp":
		return "image"
	case ".webp":
		return "sticker"
	case ".mp4", ".mov", ".webm", ".mkv":
		return "video"
	case ".ogg", ".opus", ".mp3", ".m4a", ".wav", ".aac":
		return "audio"
	default:
		return "document"
	}
}

func MIMEForPath(path string) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".jpg", ".jpeg":
		return "image/jpeg"
	case ".png":
		return "image/png"
	case ".gif":
		return "image/gif"
	case ".webp":
		return "image/webp"
	case ".mp4":
		return "video/mp4"
	case ".ogg", ".opus":
		return "audio/ogg"
	case ".mp3":
		return "audio/mpeg"
	case ".pdf":
		return "application/pdf"
	default:
		return "application/octet-stream"
	}
}

func MustAbs(p string) error {
	if p == "" {
		return nil
	}
	if !filepath.IsAbs(p) {
		return fmt.Errorf("path must be absolute")
	}
	return nil
}
