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
	"regexp"
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

func scrubHiddenResult(r testResult) testResult {
	// Runners are internal services. They must keep the full result so the
	// solutions-api can decide what to reveal based on the requester role.
	// Ordinary users never talk to runners directly and are scrubbed there.
	return r
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

type preparedProgram struct {
	Cwd  string
	Cmd  string
	Args []string
}

func ptr[T any](v T) *T { return &v }

var tmpTaskforgePathPattern = regexp.MustCompile(`/tmp/taskforge-[^\s:]+`)
var tmpGoBuildPathPattern = regexp.MustCompile(`/tmp/go-build[^\s:]+`)
var appPathPattern = regexp.MustCompile(`/app/[^\s:]+`)

func sanitizeRunnerText(value string) string {
	if strings.TrimSpace(value) == "" {
		return value
	}
	s := tmpTaskforgePathPattern.ReplaceAllString(value, "[временный файл]")
	s = tmpGoBuildPathPattern.ReplaceAllString(s, "[временный файл]")
	s = appPathPattern.ReplaceAllString(s, "[внутренний файл]")
	return s
}

func friendlyRunnerError(value string) string {
	lower := strings.ToLower(value)
	if strings.Contains(lower, "fork/exec") && strings.Contains(lower, "permission denied") {
		return "Не удалось запустить программу: нет прав на выполнение файла проверки."
	}
	return sanitizeRunnerText(value)
}

func sanitizeProcessResult(result *processResult) *processResult {
	if result == nil {
		return nil
	}
	result.Stderr = friendlyRunnerError(result.Stderr)
	if result.CompileStderr != nil {
		cleaned := sanitizeRunnerText(*result.CompileStderr)
		result.CompileStderr = &cleaned
	}
	return result
}

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

func runnerChildEnvironment() []string {
	result := []string{
		"HOME=/tmp",
		"TMPDIR=/tmp",
		"TASKFORGE_SUBMISSION=1",
	}
	for _, key := range []string{"PATH", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "JAVA_HOME", "NODE_PATH"} {
		if value := strings.TrimSpace(os.Getenv(key)); value != "" {
			result = append(result, key+"="+value)
		}
	}
	return result
}

func defaultTimeMs(kind string) int {
	switch kind {
	case "cpp":
		return 3000
	case "java":
		return 12000
	case "python", "javascript", "pascal":
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
	case "python", "cpp", "pascal":
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
	cmd.Env = runnerChildEnvironment()
	cmd.Stdin = strings.NewReader(input)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}

	stdout := &limitedBuffer{max: maxOutLen}
	stderr := &limitedBuffer{max: maxOutLen}
	cmd.Stdout = stdout
	cmd.Stderr = stderr

	if err := cmd.Start(); err != nil {
		return commandResult{ExitCode: 127, Stderr: friendlyRunnerError(err.Error())}
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
				_, _ = io.WriteString(stderr, friendlyRunnerError(waitErr.Error()))
			}
		}
	}

	return commandResult{ExitCode: exitCode, Stdout: stdout.String(), Stderr: sanitizeRunnerText(stderr.String())}
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

func nodeHeapMb(memMb int) int {
	if memMb < 64 {
		memMb = 64
	}
	heap := memMb - 96
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
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommand("g++", []string{"-std=c++17", "-O2", "-pipe", "-static-libgcc", "-static-libstdc++", "main.cpp", "-o", "a.out"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compilation error"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: res.ExitCode, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}
		if err := os.Chmod(bin, 0o700); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stdout: "", Stderr: "Не удалось подготовить программу к запуску.", CompileStderr: nil}
		}
		return &preparedProgram{Cwd: cwd, Cmd: "./a.out"}, nil

	case "java":
		src := filepath.Join(cwd, "Main.java")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommand("javac", []string{"Main.java"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compilation error"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: 2, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}
		heap := javaHeapMb(memMb)
		args := []string{"-Xms16m", fmt.Sprintf("-Xmx%dm", heap), "-XX:+UseSerialGC", "-XX:ReservedCodeCacheSize=16m", "-XX:InitialCodeCacheSize=8m", "-XX:MaxMetaspaceSize=64m", "-XX:CompressedClassSpaceSize=32m", "-XX:+ExitOnOutOfMemoryError", "Main"}
		return &preparedProgram{Cwd: cwd, Cmd: "java", Args: args}, nil

	case "javascript":
		src := filepath.Join(cwd, "main.js")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		heap := nodeHeapMb(memMb)
		return &preparedProgram{Cwd: cwd, Cmd: "node", Args: []string{fmt.Sprintf("--max-old-space-size=%d", heap), "main.js"}}, nil

	case "python":
		src := filepath.Join(cwd, "main.py")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommand("python3", []string{"-I", "-B", "-m", "py_compile", "main.py"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Python syntax error"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: res.ExitCode, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}
		return &preparedProgram{Cwd: cwd, Cmd: "python3", Args: []string{"-I", "-B", "main.py"}}, nil

	case "pascal":
		src := filepath.Join(cwd, "main.pas")
		bin := filepath.Join(cwd, "main")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommand("fpc", []string{"main.pas", "-O2", "-vw", "-omain"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compiler produced no output"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: 2, Stdout: "", Stderr: "Compilation error:\n" + msg + "\n", CompileStderr: ptr(msg + "\n")}
		}
		if err := os.Chmod(bin, 0o700); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stdout: "", Stderr: "Не удалось подготовить программу к запуску.", CompileStderr: nil}
		}
		return &preparedProgram{Cwd: cwd, Cmd: "./main"}, nil
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
	return processResult{Status: status, ExitCode: res.ExitCode, Stdout: res.Stdout, Stderr: friendlyRunnerError(res.Stderr), CompileStderr: nil}
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

func taskforgeDebugLogsEnabled() bool {
	v := strings.ToLower(strings.TrimSpace(os.Getenv("TASKFORGE_DEBUG_LOGS")))
	return v == "1" || v == "true" || v == "yes" || v == "on" || v == "debug"
}

type taskforgeStatusWriter struct {
	http.ResponseWriter
	status int
	body   bytes.Buffer
}

func (w *taskforgeStatusWriter) WriteHeader(code int) {
	w.status = code
	w.ResponseWriter.WriteHeader(code)
}

func (w *taskforgeStatusWriter) Write(p []byte) (int, error) {
	if w.body.Len() < 4096 {
		left := 4096 - w.body.Len()
		if len(p) > left {
			_, _ = w.body.Write(p[:left])
		} else {
			_, _ = w.body.Write(p)
		}
	}
	return w.ResponseWriter.Write(p)
}

func taskforgeDebugSnippet(value string) string {
	value = strings.ReplaceAll(value, "\r", " ")
	value = strings.ReplaceAll(value, "\n", " ")
	value = sanitizeRunnerText(value)
	if len(value) > 4000 {
		return value[:4000] + fmt.Sprintf("...<trimmed %d bytes>", len(value)-4000)
	}
	return value
}

func taskforgeReadRequestBody(r *http.Request) string {
	if r.Body == nil || r.ContentLength == 0 {
		return ""
	}
	data, err := io.ReadAll(r.Body)
	if err != nil {
		r.Body = io.NopCloser(bytes.NewReader(nil))
		return "<request-body-read-failed: " + err.Error() + ">"
	}
	r.Body = io.NopCloser(bytes.NewReader(data))
	return taskforgeDebugSnippet(string(data))
}

func taskforgeDebugMiddleware(service string, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		traceID := r.Header.Get("X-TaskForge-Trace-Id")
		if traceID == "" {
			traceID = fmt.Sprintf("%s-%d", service, time.Now().UnixNano())
		}
		reqBody := taskforgeReadRequestBody(r)
		log.Printf("[TFDBG RUNNER IN START] trace=%s service=%s method=%s path=%s query=%s remote=%s content_length=%d body=%s", traceID, service, r.Method, r.URL.Path, r.URL.RawQuery, r.RemoteAddr, r.ContentLength, reqBody)
		sw := &taskforgeStatusWriter{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(sw, r)
		log.Printf("[TFDBG RUNNER IN END] trace=%s service=%s method=%s path=%s status=%d duration=%s response=%s", traceID, service, r.Method, r.URL.Path, sw.status, time.Since(start), taskforgeDebugSnippet(sw.body.String()))
	})
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
			sendJSON(w, http.StatusInternalServerError, map[string]any{"message": sanitizeRunnerText(err.Error())})
			return
		}
		defer os.RemoveAll(dir)
		program, compileErr := compileProgram(kind, req.Code, dir, timeMs, memMb)
		if compileErr != nil {
			sendJSON(w, http.StatusOK, sanitizeProcessResult(compileErr))
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
			sendJSON(w, http.StatusInternalServerError, map[string]any{"message": sanitizeRunnerText(err.Error())})
			return
		}
		defer os.RemoveAll(dir)
		program, compileErr := compileProgram(kind, req.Code, dir, timeMs, memMb)
		results := make([]testResult, 0, len(req.Tests))
		if compileErr != nil {
			compileErr = sanitizeProcessResult(compileErr)
			given, expected, hidden := "", "", false
			if len(req.Tests) > 0 {
				given = strValue(req.Tests[0].Input)
				expected = strValue(req.Tests[0].ExpectedOutput)
				hidden = req.Tests[0].IsHidden
			}
			results = append(results, scrubHiddenResult(testResult{Input: given, ExpectedOutput: expected, ActualOutput: compileErr.Stdout, Passed: false, Status: compileErr.Status, ExitCode: compileErr.ExitCode, Stderr: compileErr.Stderr, CompileStderr: compileErr.CompileStderr, Hidden: hidden}))
			sendJSON(w, http.StatusOK, map[string]any{"results": results})
			return
		}
		for _, t := range req.Tests {
			given := strValue(t.Input)
			expected := strValue(t.ExpectedOutput)
			run := executeProgram(kind, program, given, timeMs)
			passed := run.ExitCode == 0 && strings.TrimRight(run.Stdout, "\r\n") == strings.TrimRight(expected, "\r\n")
			results = append(results, scrubHiddenResult(testResult{Input: given, ExpectedOutput: expected, ActualOutput: run.Stdout, Passed: passed, Status: run.Status, ExitCode: run.ExitCode, Stderr: run.Stderr, CompileStderr: run.CompileStderr, Hidden: t.IsHidden}))
		}
		sendJSON(w, http.StatusOK, map[string]any{"results": results})
	}
	mux.HandleFunc("/run/tests", testHandler)
	mux.HandleFunc("/run-tests", testHandler)

	handler := http.Handler(mux)
	if taskforgeDebugLogsEnabled() {
		handler = taskforgeDebugMiddleware(kind+"-runner", handler)
	}
	server := &http.Server{Addr: ":" + port, Handler: handler, ReadHeaderTimeout: 10 * time.Second}
	log.Printf("%s-runner listening on :%s", kind, port)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}

func _unused() {
	_, _ = strconv.Atoi("0")
}
