package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
)

const maxOutLen = 1_000_000

type runRequest struct {
	Code          string  `json:"code"`
	Input         *string `json:"input"`
	TimeLimitMs   *int    `json:"timeLimitMs"`
	MemoryLimitMb *int    `json:"memoryLimitMb"`
}

type testCase struct {
	Input          *string `json:"input"`
	ExpectedOutput *string `json:"expectedOutput"`
	IsHidden       bool    `json:"isHidden"`
}

type testsRequest struct {
	Code          string     `json:"code"`
	Tests         []testCase `json:"tests"`
	TimeLimitMs   *int       `json:"timeLimitMs"`
	MemoryLimitMb *int       `json:"memoryLimitMb"`
}

type processResult struct {
	Status        string  `json:"status,omitempty"`
	ExitCode      int     `json:"exitCode"`
	Stdout        string  `json:"stdout"`
	Stderr        string  `json:"stderr"`
	CompileStderr *string `json:"compileStderr"`
}

type testResult struct {
	Input          string  `json:"input"`
	ExpectedOutput string  `json:"expectedOutput"`
	ActualOutput   string  `json:"actualOutput"`
	Passed         bool    `json:"passed"`
	Status         string  `json:"status,omitempty"`
	ExitCode       int     `json:"exitCode,omitempty"`
	Stderr         string  `json:"stderr"`
	CompileStderr  *string `json:"compileStderr,omitempty"`
	Hidden         bool    `json:"hidden"`
}

type limitedBuffer struct {
	buf bytes.Buffer
	max int
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	if b.max <= 0 {
		return len(p), nil
	}
	left := b.max - b.buf.Len()
	if left > 0 {
		if len(p) > left {
			_, _ = b.buf.Write(p[:left])
		} else {
			_, _ = b.buf.Write(p)
		}
	}
	return len(p), nil
}

func (b *limitedBuffer) String() string {
	return strings.ReplaceAll(b.buf.String(), "\r\n", "\n")
}

type commandResult struct {
	ExitCode int
	Stdout   string
	Stderr   string
	TimedOut bool
}

func ptr[T any](v T) *T { return &v }

func env(name, fallback string) string {
	v := strings.TrimSpace(os.Getenv(name))
	if v == "" {
		return fallback
	}
	return v
}

func intValue(v *int, fallback int) int {
	if v == nil || *v <= 0 {
		return fallback
	}
	return *v
}

func strValue(v *string) string {
	if v == nil {
		return ""
	}
	return *v
}

func defaultTimeMs(kind string) int {
	switch kind {
	case "cpp":
		return 3000
	case "java":
		return 12000
	case "javascript", "pascal":
		return 8000
	default:
		return 8000
	}
}

func defaultMemoryMb(kind string) int {
	switch kind {
	case "java":
		return 768
	case "javascript":
		return 512
	case "cpp", "pascal":
		return 256
	default:
		return 256
	}
}

func timeoutDuration(ms int) time.Duration {
	if ms <= 0 {
		ms = 1000
	}
	return time.Duration(ms)*time.Millisecond + 2*time.Second
}

func runCommand(name string, args []string, cwd string, input string, timeout time.Duration) commandResult {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Stdin = strings.NewReader(input)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}

	stdout := &limitedBuffer{max: maxOutLen}
	stderr := &limitedBuffer{max: maxOutLen}
	cmd.Stdout = stdout
	cmd.Stderr = stderr

	err := cmd.Start()
	if err != nil {
		return commandResult{ExitCode: 127, Stderr: err.Error()}
	}

	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()

	var waitErr error
	select {
	case waitErr = <-done:
	case <-ctx.Done():
		if cmd.Process != nil {
			_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
			_ = cmd.Process.Kill()
		}
		waitErr = <-done
		_ = waitErr
		return commandResult{ExitCode: 124, Stdout: stdout.String(), Stderr: "Time limit exceeded", TimedOut: true}
	}

	exitCode := 0
	if waitErr != nil {
		var exitErr *exec.ExitError
		if errors.As(waitErr, &exitErr) {
			exitCode = exitErr.ExitCode()
		} else {
			exitCode = 1
			if stderr.buf.Len() == 0 {
				_, _ = io.WriteString(stderr, waitErr.Error())
			}
		}
	}

	return commandResult{ExitCode: exitCode, Stdout: stdout.String(), Stderr: stderr.String()}
}

type preparedProgram struct {
	Cwd  string
	Cmd  string
	Args []string
}

func javaHeapMb(memMb int) int {
	if memMb < 128 {
		memMb = 128
	}
	heap := memMb - 128
	if heap < 64 {
		heap = 64
	}
	if heap > 512 {
		heap = 512
	}
	return heap
}

func compileProgram(kind, code, cwd string, timeMs, memMb int) (*preparedProgram, *processResult) {
	switch kind {
	case "cpp":
		src := filepath.Join(cwd, "main.cpp")
		bin := filepath.Join(cwd, "a.out")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: err.Error(), CompileStderr: nil}
		}
		res := runCommand("g++", []string{"-std=c++17", "-O2", "-pipe", "-static-libgcc", "-static-libstdc++", src, "-o", bin}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compilation error"
			}
			return nil, &processResult{Status: "compile_error", ExitCode: res.ExitCode, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}
		return &preparedProgram{Cwd: cwd, Cmd: bin}, nil

	case "java":
		src := filepath.Join(cwd, "Main.java")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: err.Error(), CompileStderr: nil}
		}
		res := runCommand("javac", []string{"Main.java"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compilation error"
			}
			return nil, &processResult{Status: "compile_error", ExitCode: 2, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}
		heap := javaHeapMb(memMb)
		args := []string{"-Xms16m", fmt.Sprintf("-Xmx%dm", heap), "-XX:+UseSerialGC", "-XX:ReservedCodeCacheSize=16m", "-XX:InitialCodeCacheSize=8m", "-XX:MaxMetaspaceSize=64m", "-XX:CompressedClassSpaceSize=32m", "-XX:+ExitOnOutOfMemoryError", "Main"}
		return &preparedProgram{Cwd: cwd, Cmd: "java", Args: args}, nil

	case "pascal":
		src := filepath.Join(cwd, "main.pas")
		bin := filepath.Join(cwd, "main")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: err.Error(), CompileStderr: nil}
		}
		res := runCommand("fpc", []string{"main.pas", "-O2", "-vw", "-o" + bin}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compiler produced no output"
			}
			// Keep the old pascal contract: compiler details are placed to stderr.
			return nil, &processResult{Status: "compile_error", ExitCode: 2, Stdout: "", Stderr: "Compilation error:\n" + msg + "\n", CompileStderr: ptr(msg + "\n")}
		}
		return &preparedProgram{Cwd: cwd, Cmd: bin}, nil
	default:
		return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: "Unsupported runner kind: " + kind}
	}
}

func executeProgram(kind string, p *preparedProgram, input string, timeMs int) processResult {
	res := runCommand(p.Cmd, p.Args, p.Cwd, input, timeoutDuration(timeMs))
	status := "ok"
	if res.TimedOut || res.ExitCode == 124 {
		status = "time_limit"
	} else if res.ExitCode != 0 {
		status = "runtime_error"
	}
	return processResult{Status: status, ExitCode: res.ExitCode, Stdout: res.Stdout, Stderr: res.Stderr, CompileStderr: nil}
}

func sendJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func decodeJSON(r *http.Request, v any) error {
	defer r.Body.Close()
	dec := json.NewDecoder(io.LimitReader(r.Body, 16<<20))
	dec.DisallowUnknownFields()
	return dec.Decode(v)
}

func main() {
	kind := env("RUNNER_KIND", "cpp")
	port := env("PORT", "8080")

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		sendJSON(w, http.StatusOK, map[string]any{"ok": true, "service": kind + "-runner", "runtime": "go", "go": runtime.Version()})
	})
	mux.HandleFunc("/ready", func(w http.ResponseWriter, r *http.Request) {
		sendJSON(w, http.StatusOK, map[string]any{"ok": true})
	})
	mux.HandleFunc("/run", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		var req runRequest
		if err := decodeJSON(r, &req); err != nil {
			sendJSON(w, http.StatusBadRequest, map[string]any{"message": err.Error()})
			return
		}
		timeMs := intValue(req.TimeLimitMs, defaultTimeMs(kind))
		memMb := intValue(req.MemoryLimitMb, defaultMemoryMb(kind))
		dir, err := os.MkdirTemp("", "taskforge-"+kind+"-")
		if err != nil {
			sendJSON(w, http.StatusInternalServerError, map[string]any{"message": err.Error()})
			return
		}
		defer os.RemoveAll(dir)
		program, compileErr := compileProgram(kind, req.Code, dir, timeMs, memMb)
		if compileErr != nil {
			sendJSON(w, http.StatusOK, compileErr)
			return
		}
		sendJSON(w, http.StatusOK, executeProgram(kind, program, strValue(req.Input), timeMs))
	})

	testHandler := func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		var req testsRequest
		if err := decodeJSON(r, &req); err != nil {
			sendJSON(w, http.StatusBadRequest, map[string]any{"message": err.Error()})
			return
		}
		timeMs := intValue(req.TimeLimitMs, defaultTimeMs(kind))
		memMb := intValue(req.MemoryLimitMb, defaultMemoryMb(kind))
		dir, err := os.MkdirTemp("", "taskforge-"+kind+"-tests-")
		if err != nil {
			sendJSON(w, http.StatusInternalServerError, map[string]any{"message": err.Error()})
			return
		}
		defer os.RemoveAll(dir)
		program, compileErr := compileProgram(kind, req.Code, dir, timeMs, memMb)
		results := make([]testResult, 0, len(req.Tests))
		if compileErr != nil {
			given, expected, hidden := "", "", false
			if len(req.Tests) > 0 {
				given = strValue(req.Tests[0].Input)
				expected = strValue(req.Tests[0].ExpectedOutput)
				hidden = req.Tests[0].IsHidden
			}
			results = append(results, testResult{Input: given, ExpectedOutput: expected, ActualOutput: compileErr.Stdout, Passed: false, Status: compileErr.Status, ExitCode: compileErr.ExitCode, Stderr: compileErr.Stderr, CompileStderr: compileErr.CompileStderr, Hidden: hidden})
			sendJSON(w, http.StatusOK, map[string]any{"results": results})
			return
		}
		for _, t := range req.Tests {
			given := strValue(t.Input)
			expected := strValue(t.ExpectedOutput)
			run := executeProgram(kind, program, given, timeMs)
			passed := run.ExitCode == 0 && strings.TrimRight(run.Stdout, "\r\n") == strings.TrimRight(expected, "\r\n")
			results = append(results, testResult{Input: given, ExpectedOutput: expected, ActualOutput: run.Stdout, Passed: passed, Status: run.Status, ExitCode: run.ExitCode, Stderr: run.Stderr, CompileStderr: run.CompileStderr, Hidden: t.IsHidden})
		}
		sendJSON(w, http.StatusOK, map[string]any{"results": results})
	}
	mux.HandleFunc("/run/tests", testHandler)
	mux.HandleFunc("/run-tests", testHandler)

	server := &http.Server{Addr: ":" + port, Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	log.Printf("%s-runner listening on :%s", kind, port)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}

func _unused() {
	_, _ = strconv.Atoi("0")
}
