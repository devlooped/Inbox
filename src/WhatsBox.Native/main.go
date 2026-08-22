package main

import (
	"os"

	"github.com/devlooped/whatsbox/app"
)

func main() {
	os.Exit(app.Main(os.Args, os.Stdin, os.Stdout, os.Stderr))
}
