package main

import (
	"bytes"
	"context"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func buildSandboxPreload(t *testing.T) string {
	t.Helper()
	for _, tool := range []string{"gcc", "python3"} {
		if _, err := exec.LookPath(tool); err != nil {
			t.Skip(tool + " is not available")
		}
	}
	output := filepath.Join(t.TempDir(), "libtaskforge_sandbox.so")
	cmd := exec.Command(
		"gcc", "-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror",
		"-fstack-protector-strong", "-D_FORTIFY_SOURCE=2",
		"-Wl,-z,relro,-z,now,-z,noexecstack",
		"-o", output, "security/sandbox_preload.c",
	)
	if combined, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to compile sandbox preload: %v\n%s", err, combined)
	}
	return output
}

func sandboxTestEnvironment(preload, profile string) []string {
	return []string{
		"PATH=" + os.Getenv("PATH"),
		"HOME=/tmp",
		"TMPDIR=/tmp",
		"TASKFORGE_SUBMISSION=1",
		"TASKFORGE_SANDBOX_PROFILE=" + profile,
		"TASKFORGE_LIMIT_CPU_SECONDS=10",
		"TASKFORGE_LIMIT_FSIZE_MB=16",
		"TASKFORGE_LIMIT_NOFILE=128",
		"LD_PRELOAD=" + preload,
		"PYTHONWARNINGS=ignore",
	}
}

func TestSandboxPreloadAllowsPythonThreadsAndBlocksProcessAndInternet(t *testing.T) {
	preload := buildSandboxPreload(t)
	script := `
import errno, os, socket, threading
value = []
thread = threading.Thread(target=lambda: value.append("thread"))
thread.start(); thread.join()
if value != ["thread"]:
    raise SystemExit(2)
try:
    os.fork()
    raise SystemExit(3)
except OSError as error:
    if error.errno != errno.EPERM:
        raise
try:
    socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    raise SystemExit(4)
except OSError as error:
    if error.errno != errno.EPERM:
        raise
print("blocked")
`
	cmd := exec.Command("python3", "-c", script)
	cmd.Env = sandboxTestEnvironment(preload, "managed")
	output, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("sandboxed Python fixture failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "blocked" {
		t.Fatalf("unexpected sandbox output: %q", output)
	}
}

func TestSandboxPreloadImageProfileAllowsOnlyUnixSockets(t *testing.T) {
	preload := buildSandboxPreload(t)
	script := `
import errno, socket
unix_socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
unix_socket.close()
try:
    socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    raise SystemExit(2)
except OSError as error:
    if error.errno != errno.EPERM:
        raise
print("blocked")
`
	cmd := exec.Command("python3", "-c", script)
	cmd.Env = sandboxTestEnvironment(preload, "image")
	output, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("image-profile fixture failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "blocked" {
		t.Fatalf("unexpected image-profile output: %q", output)
	}
}

func TestSandboxPreloadXServerProfileAllowsHelpersButBlocksInternet(t *testing.T) {
	preload := buildSandboxPreload(t)
	script := `
import errno, socket
unix_socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
unix_socket.close()
try:
    socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    raise SystemExit(2)
except OSError as error:
    if error.errno != errno.EPERM:
        raise
print("xserver")
`
	scriptPath := filepath.Join(t.TempDir(), "xserver_test.py")
	if err := os.WriteFile(scriptPath, []byte(script), 0o600); err != nil {
		t.Fatal(err)
	}
	command := "/bin/true & wait; exec python3 " + scriptPath
	cmd := exec.Command("/bin/sh", "-c", command)
	cmd.Env = sandboxTestEnvironment(preload, "xserver")
	output, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("xserver-profile fixture failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "xserver" {
		t.Fatalf("unexpected xserver-profile output: %q", output)
	}
}

func TestDecodeJSONRejectsUnknownFieldsAndMultipleValues(t *testing.T) {
	for name, body := range map[string]string{
		"unknown field":   `{"code":"print(1)","unexpected":true}`,
		"multiple values": `{"code":"print(1)"} {"code":"print(2)"}`,
	} {
		t.Run(name, func(t *testing.T) {
			request, err := http.NewRequest(http.MethodPost, "/run", bytes.NewBufferString(body))
			if err != nil {
				t.Fatal(err)
			}
			var decoded runRequest
			if err := decodeJSON(request, &decoded); err == nil {
				t.Fatal("expected malformed request to be rejected")
			}
		})
	}
}

func TestDecodeJSONRejectsOversizedBody(t *testing.T) {
	body := strings.Repeat("x", maxRequestBytes+1)
	request, err := http.NewRequest(http.MethodPost, "/run", strings.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	var decoded runRequest
	err = decodeJSON(request, &decoded)
	if err == nil || !strings.Contains(err.Error(), "too large") {
		t.Fatalf("expected body-size error, got %v", err)
	}
}

func TestRunCommandStopsWhenParentContextIsCancelled(t *testing.T) {
	if _, err := exec.LookPath("python3"); err != nil {
		t.Skip("python3 is not available")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Millisecond)
	defer cancel()
	started := time.Now()
	result := runCommandWithEnvContext(ctx, "python3", []string{"-c", "import time; time.sleep(10)"}, t.TempDir(), "", 15*time.Second, nil)
	if !result.TimedOut || result.ExitCode != 124 {
		t.Fatalf("expected cancelled child to be reported as timed out, got %#v", result)
	}
	if elapsed := time.Since(started); elapsed > 2*time.Second {
		t.Fatalf("cancelled child was not killed promptly: %s", elapsed)
	}
}
