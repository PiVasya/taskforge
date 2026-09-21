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

func TestPythonValidationSeparatesSyntaxAndPolicyFailures(t *testing.T) {
	if _, err := exec.LookPath("python3"); err != nil {
		t.Skip("python3 is not available")
	}
	policyPath, err := filepath.Abs("security/python_policy.py")
	if err != nil {
		t.Fatal(err)
	}

	cases := []struct {
		name       string
		source     string
		wantStatus string
	}{
		{name: "valid source", source: "print(1)\n", wantStatus: ""},
		{name: "missing closing parenthesis", source: "x = int(input())\nif x > 0:\n    print(\"Positive\")\n    print(\"Accepted\")\nelse:\n    print(\"Rejected\"\n", wantStatus: "compile_error"},
		{name: "stray quote in expression", source: "total = 0\nc = 0\nx = int(input())\nwhile x != 0:\n    if x > 0:\n        total += x\n        c += 1\n    x = int(input())\nprint(total/c\")\n", wantStatus: "compile_error"},
		{name: "missing print separator", source: "a = float(input())\nprint(\"Value:\" a)\n", wantStatus: "compile_error"},
		{name: "broken f string", source: "a = float(input())\nprint(f\"Value:\" a)\n", wantStatus: "compile_error"},
		{name: "missing int print separator", source: "a = 18\nprint(\"Age:\" a)\n", wantStatus: "compile_error"},
		{name: "missing float print separator", source: "a = 12.5\nprint(\"Price:\" a)\n", wantStatus: "compile_error"},
		{name: "broken exact format", source: "a = input()\nprint(f\"Word:\", {a}!)\n", wantStatus: "compile_error"},
		{name: "broken multiline print", source: "a = 18\nprint('Age:'\n a)\n", wantStatus: "compile_error"},
		{name: "policy violation", source: "open(\"/etc/passwd\").read()\n", wantStatus: "policy_error"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			if err := os.WriteFile(filepath.Join(dir, "main.py"), []byte(tc.source), 0o600); err != nil {
				t.Fatal(err)
			}
			result := validatePythonSourceContext(context.Background(), dir, policyPath, "standard", 2_000)
			if tc.wantStatus == "" {
				if result != nil {
					t.Fatalf("expected valid source, got %#v", result)
				}
				return
			}
			if result == nil {
				t.Fatalf("expected %s, got success", tc.wantStatus)
			}
			if result.Status != tc.wantStatus {
				t.Fatalf("expected status %q, got %#v", tc.wantStatus, result)
			}
			if tc.wantStatus == "compile_error" {
				if result.CompileStderr == nil || !strings.Contains(*result.CompileStderr, "SyntaxError") {
					t.Fatalf("expected Python syntax diagnostic, got %#v", result)
				}
				if strings.Contains(result.Stderr, "системой безопасности") {
					t.Fatalf("syntax error was mislabeled as security: %#v", result)
				}
			}
		})
	}
}
