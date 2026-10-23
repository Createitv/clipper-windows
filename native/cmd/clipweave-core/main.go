package main

import (
	"bufio"
	"clipweave.local/device/bridge"
	"flag"
	"fmt"
	"os"
)

func main() {
	dir := flag.String("vault-dir", "", "private encrypted vault directory")
	flag.Parse()
	if *dir == "" {
		fmt.Fprintln(os.Stderr, "--vault-dir is required")
		os.Exit(2)
	}
	client := bridge.NewClient(*dir)
	defer client.Close()
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 65536), 36*1024*1024)
	writer := bufio.NewWriter(os.Stdout)
	for scanner.Scan() {
		_, _ = fmt.Fprintln(writer, client.Invoke(scanner.Text()))
		if err := writer.Flush(); err != nil {
			return
		}
	}
	if scanner.Err() != nil {
		fmt.Fprintln(os.Stderr, "client request stream failed")
		os.Exit(1)
	}
}
