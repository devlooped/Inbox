package main

import (
	"os"

	"github.com/devlooped/whatsbox/internal/app"
)

func main() {
	os.Exit(app.Main(os.Args, os.Stdin, os.Stdout, os.Stderr))
}
