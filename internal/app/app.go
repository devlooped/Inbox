package app

import (
	"bufio"
	"context"
	"flag"
	"fmt"
	"io"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/devlooped/whatsbox/internal/rpc"
	"github.com/devlooped/whatsbox/internal/wa"
)

const (
	ProductVersion = "0.1"
	VersionText    = "whatsbox 0.1"
)

const HelpText = `whatsbox is a local WhatsApp companion that speaks JSON-RPC 2.0 over stdio.

Usage:
  whatsbox [--store ABSOLUTE_PATH] [--version] [--help]

Flags:
  --store    Absolute path to the session store directory (no default)
  --version  Print version and exit
  --help     Print this help and exit
`

type Config struct {
	Store   string
	Version bool
	Help    bool
}

func ParseFlags(args []string) (Config, string, error) {
	var cfg Config
	fs := flag.NewFlagSet("whatsbox", flag.ContinueOnError)
	var usage strings.Builder
	fs.SetOutput(&usage)
	fs.StringVar(&cfg.Store, "store", "", "Absolute path to the session store directory")
	fs.BoolVar(&cfg.Version, "version", false, "Print version and exit")
	fs.BoolVar(&cfg.Help, "help", false, "Print this help and exit")
	if err := fs.Parse(args); err != nil {
		return cfg, usage.String(), err
	}
	return cfg, usage.String(), nil
}

func Main(args []string, in io.Reader, out io.Writer, errOut io.Writer) int {
	argv := args
	if len(argv) > 0 {
		argv = argv[1:]
	}
	cfg, usage, err := ParseFlags(argv)
	if err != nil {
		fmt.Fprint(errOut, usage)
		return 2
	}
	if cfg.Help {
		fmt.Fprint(out, HelpText)
		return 0
	}
	if cfg.Version {
		fmt.Fprintln(out, VersionText)
		return 0
	}
	if cfg.Store != "" && !filepath.IsAbs(cfg.Store) {
		fmt.Fprintln(errOut, "whatsbox: --store must be an absolute path")
		return 2
	}
	d := New(Options{
		StoreFlag: cfg.Store,
		Factory:   wa.OpenReal,
		Log:       errOut,
	})
	if err := d.Run(context.Background(), in, out); err != nil && err != io.EOF {
		fmt.Fprintln(errOut, "whatsbox:", err)
		return 1
	}
	return 0
}

func SamePath(a, b string) bool {
	a = filepath.Clean(mustAbs(a))
	b = filepath.Clean(mustAbs(b))
	if runtime.GOOS == "windows" {
		return strings.EqualFold(a, b)
	}
	return a == b
}

func mustAbs(p string) string {
	abs, err := filepath.Abs(p)
	if err != nil {
		return p
	}
	return abs
}

func writeLine(w io.Writer, mu interface{ Lock(); Unlock() }, line []byte) error {
	mu.Lock()
	defer mu.Unlock()
	if _, err := w.Write(line); err != nil {
		return err
	}
	_, err := w.Write([]byte{'\n'})
	return err
}

func newScanner(r io.Reader) *bufio.Scanner {
	sc := bufio.NewScanner(r)
	buf := make([]byte, 0, 64*1024)
	sc.Buffer(buf, 16*1024*1024)
	return sc
}

func logf(w io.Writer, level, format string, args ...any) {
	if w == nil {
		return
	}
	fmt.Fprintf(w, "whatsbox %s: %s\n", level, fmt.Sprintf(format, args...))
}

func requireAbs(p, name string) *rpc.Error {
	if p == "" {
		return nil
	}
	if !filepath.IsAbs(p) {
		return rpc.ErrData(rpc.TokInvalidParams, name+" must be an absolute path")
	}
	return nil
}

func stderrOrDiscard(w io.Writer) io.Writer {
	if w == nil {
		return io.Discard
	}
	return w
}
