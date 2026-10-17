package main

import (
	"context"
	"os/exec"
	"strings"
	"testing"
	"time"
)

func TestRunnerStopsInfiniteOutputAtLimit(t *testing.T) {
	if _, err := exec.LookPath("python3"); err != nil {
		t.Skip("python3 unavailable")
	}
	started := time.Now()
	r := runCommandWithEnvContext(context.Background(), "python3", []string{
		"-c", "import sys\nwhile True:\n sys.stdout.write('Hello\\n'*2048)\n sys.stdout.flush()",
	}, "", "", 10*time.Second, nil)
	if !r.OutputLimited || r.ExitCode != 125 {
		t.Fatalf("want output_limit, got %+v", r)
	}
	if len(r.Stdout) != maxOutLen {
		t.Fatalf("captured %d bytes instead of %d", len(r.Stdout), maxOutLen)
	}
	if time.Since(started) >= 8*time.Second {
		t.Fatal("infinite stdout was not interrupted promptly")
	}
}

func TestRunnerPreservesSmallOutput(t *testing.T) {
	r := runCommandWithEnvContext(context.Background(), "/bin/sh", []string{
		"-c", "printf 'Hello\\n'",
	}, "", "", 5*time.Second, nil)
	if r.OutputLimited || r.TimedOut || r.ExitCode != 0 || r.Stdout != "Hello\n" {
		t.Fatalf("normal output changed: %+v", r)
	}
}

func TestTestResultPreviewIsBounded(t *testing.T) {
	source := strings.Repeat("Hello\n", 3000)
	result := previewCaseText(source)
	if len(result) >= len(source) || len(result) > maxTestResultPreviewBytes+40 {
		t.Fatalf("preview not capped: %d bytes", len(result))
	}
	if !strings.Contains(result, "preview truncated") {
		t.Fatalf("missing truncation marker: %q", result)
	}
	if previewCaseText("short") != "short" {
		t.Fatal("short result was modified")
	}
}

func TestRunnerTerminatesInfiniteComputationWithoutOutput(t *testing.T) {
	if _, err := exec.LookPath("/bin/sh"); err != nil {
		t.Skip("shell unavailable")
	}
	started := time.Now()
	result := runCommandWithEnvContext(context.Background(), "/bin/sh", []string{"-c", "while :; do :; done"}, "", "", 200*time.Millisecond, nil)
	if !result.TimedOut || result.ExitCode != 124 || result.OutputLimited {
		t.Fatalf("want hard time limit for silent infinite loop, got %+v", result)
	}
	if time.Since(started) > 3*time.Second {
		t.Fatalf("timeout blocked too long: %s", time.Since(started))
	}
}
