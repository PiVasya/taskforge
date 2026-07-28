package main

import (
	"bytes"
	"context"
	"debug/elf"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"math/rand"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"syscall"
	"time"
)

const maxTextLen = 1_000_000

type renderRequest struct {
	Source          string  `json:"source"`
	Stdin           *string `json:"stdin"`
	TimeoutSeconds  *int    `json:"timeoutSeconds"`
	TimeoutSeconds2 *int    `json:"timeout_seconds"`
	Debug           bool    `json:"debug"`
}

type renderDebugResponse struct {
	PngBase64 string `json:"pngBase64,omitempty"`
	Stdout    string `json:"stdout"`
	Stderr    string `json:"stderr"`
}

type execOutput struct {
	ExitCode int
	Stdout   string
	Stderr   string
	TimedOut bool
}

type captureOutput struct {
	PNG    []byte
	Stdout string
	Stderr string
	Err    string
}

type limitedBuffer struct {
	buf bytes.Buffer
	max int
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
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

func env(name, fallback string) string {
	v := strings.TrimSpace(os.Getenv(name))
	if v == "" {
		return fallback
	}
	return v
}

func timeout(req renderRequest) int {
	if req.TimeoutSeconds2 != nil && *req.TimeoutSeconds2 > 0 {
		return clamp(*req.TimeoutSeconds2, 1, 120)
	}
	if req.TimeoutSeconds != nil && *req.TimeoutSeconds > 0 {
		return clamp(*req.TimeoutSeconds, 1, 120)
	}
	return 20
}

func clamp(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func tail(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return "..." + s[len(s)-n:]
}

func runCommand(name string, args []string, cwd string, input string, seconds int, extraEnv []string) execOutput {
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(seconds)*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Env = append(os.Environ(), extraEnv...)
	cmd.Stdin = strings.NewReader(input)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	stdout := &limitedBuffer{max: maxTextLen}
	stderr := &limitedBuffer{max: maxTextLen}
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	if err := cmd.Start(); err != nil {
		return execOutput{ExitCode: 127, Stderr: friendlyRunnerError(err.Error())}
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	var err error
	select {
	case err = <-done:
	case <-ctx.Done():
		if cmd.Process != nil {
			_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
			_ = cmd.Process.Kill()
		}
		_ = <-done
		return execOutput{ExitCode: 124, Stdout: stdout.String(), Stderr: "Time limit exceeded", TimedOut: true}
	}
	code := 0
	if err != nil {
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) {
			code = exitErr.ExitCode()
		} else {
			code = 1
			if stderr.buf.Len() == 0 {
				_, _ = io.WriteString(stderr, friendlyRunnerError(err.Error()))
			}
		}
	}
	return execOutput{ExitCode: code, Stdout: stdout.String(), Stderr: sanitizeRunnerText(stderr.String())}
}

func sendJSON(w http.ResponseWriter, code int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(value)
}

func sendError(w http.ResponseWriter, code int, message string, stdout string, stderr string) {
	sendJSON(w, code, map[string]any{"detail": map[string]string{"message": friendlyRunnerError(message), "stdout": tail(stdout, 8000), "stderr": tail(friendlyRunnerError(stderr), 8000)}})
}

func decodeJSON(r *http.Request, out any) error {
	defer r.Body.Close()
	dec := json.NewDecoder(io.LimitReader(r.Body, 32<<20))
	return dec.Decode(out)
}

var forbiddenCppExternalSymbols = map[string]struct{}{
	"system": {}, "__libc_system": {}, "popen": {}, "pclose": {},
	"fork": {}, "vfork": {}, "clone": {}, "clone3": {},
	"execl": {}, "execlp": {}, "execle": {}, "execv": {}, "execvp": {},
	"execvpe": {}, "execve": {}, "execveat": {}, "fexecve": {},
	"posix_spawn": {}, "posix_spawnp": {},
	"dlopen": {}, "dlmopen": {}, "dlsym": {}, "dlvsym": {},
	"syscall": {}, "ptrace": {}, "unshare": {}, "setns": {},
	"mount": {}, "umount": {}, "umount2": {}, "chroot": {}, "pivot_root": {},
	"socket": {}, "socketpair": {}, "connect": {}, "bind": {}, "listen": {},
	"accept": {}, "accept4": {}, "send": {}, "sendto": {}, "sendmsg": {},
	"recv": {}, "recvfrom": {}, "recvmsg": {}, "getaddrinfo": {},
	"kill": {}, "tkill": {}, "tgkill": {},
	"mmap": {}, "mmap64": {}, "mprotect": {}, "memfd_create": {},
}

func normalizeELFSymbol(name string) string {
	name = strings.TrimSpace(name)
	if i := strings.IndexByte(name, '@'); i >= 0 {
		name = name[:i]
	}
	return strings.TrimPrefix(name, "__GI_")
}

func forbiddenUndefinedELFSymbols(path string) ([]string, error) {
	f, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	all := make([]elf.Symbol, 0, 64)
	if symbols, symErr := f.Symbols(); symErr == nil {
		all = append(all, symbols...)
	} else if !errors.Is(symErr, elf.ErrNoSymbols) {
		return nil, symErr
	}
	if symbols, symErr := f.DynamicSymbols(); symErr == nil {
		all = append(all, symbols...)
	} else if !errors.Is(symErr, elf.ErrNoSymbols) {
		return nil, symErr
	}

	blocked := make(map[string]struct{})
	for _, symbol := range all {
		if symbol.Section != elf.SHN_UNDEF {
			continue
		}
		name := normalizeELFSymbol(symbol.Name)
		if _, forbidden := forbiddenCppExternalSymbols[name]; forbidden {
			blocked[name] = struct{}{}
		}
	}

	result := make([]string, 0, len(blocked))
	for name := range blocked {
		result = append(result, name)
	}
	sort.Strings(result)
	return result, nil
}

func forbiddenExecutableInstructions(path string) ([]string, error) {
	f, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	patterns := []struct {
		name  string
		bytes []byte
	}{
		{name: "machine.syscall", bytes: []byte{0x0f, 0x05}},
		{name: "machine.sysenter", bytes: []byte{0x0f, 0x34}},
		{name: "machine.int80", bytes: []byte{0xcd, 0x80}},
	}
	blocked := make(map[string]struct{})
	for _, section := range f.Sections {
		if section.Flags&elf.SHF_EXECINSTR == 0 || section.Size == 0 {
			continue
		}
		data, readErr := section.Data()
		if readErr != nil {
			return nil, readErr
		}
		for _, pattern := range patterns {
			if bytes.Contains(data, pattern.bytes) {
				blocked[pattern.name] = struct{}{}
			}
		}
	}

	result := make([]string, 0, len(blocked))
	for name := range blocked {
		result = append(result, name)
	}
	sort.Strings(result)
	return result, nil
}

func compileCpp(source string, dir string, timeoutSec int) (string, string, int) {
	src := filepath.Join(dir, "main.cpp")
	obj := filepath.Join(dir, "main.o")
	exe := filepath.Join(dir, "main")
	if err := os.WriteFile(src, []byte(source), 0o600); err != nil {
		return sanitizeRunnerText(err.Error()), "", 1
	}
	compile := runCommand("g++", []string{"main.cpp", "-O2", "-std=c++17", "-fno-asm", "-I/opt/taskforge/include", "-c", "-o", obj}, dir, "", clamp(timeoutSec, 1, 30), nil)
	if compile.ExitCode != 0 {
		return sanitizeRunnerText(compile.Stdout + compile.Stderr), "", compile.ExitCode
	}
	blocked, scanErr := forbiddenUndefinedELFSymbols(obj)
	if scanErr != nil {
		log.Printf("image-cpp-runner failed to inspect compiled object: %v", scanErr)
		return "Решение отклонено системой безопасности.", "", 126
	}
	if len(blocked) > 0 {
		log.Printf("image-cpp-runner blocked forbidden external symbols: %s", strings.Join(blocked, ","))
		return "Решение отклонено системой безопасности.", "", 126
	}
	blockedInstructions, instructionScanErr := forbiddenExecutableInstructions(obj)
	if instructionScanErr != nil {
		log.Printf("image-cpp-runner failed to inspect executable instructions: %v", instructionScanErr)
		return "Решение отклонено системой безопасности.", "", 126
	}
	if len(blockedInstructions) > 0 {
		log.Printf("image-cpp-runner blocked forbidden machine instructions: %s", strings.Join(blockedInstructions, ","))
		return "Решение отклонено системой безопасности.", "", 126
	}
	link := runCommand("g++", []string{obj, "-Wl,-z,relro,-z,now,-z,noexecstack", "-lglut", "-lGL", "-lGLU", "-o", exe}, dir, "", clamp(timeoutSec, 1, 30), nil)
	if link.ExitCode == 0 {
		if err := os.Chmod(exe, 0o500); err != nil {
			return "Не удалось подготовить программу к запуску.", "", 1
		}
	}
	return sanitizeRunnerText(link.Stdout + link.Stderr), exe, link.ExitCode
}

func preparePython(source string, dir string, timeoutSec int) (string, string, int) {
	// Python image runner supports standard turtle, matplotlib, Pillow and any code
	// that writes out.png/out.ppm/out.jpg. GUI code runs under Xvfb and can also be
	// captured from the visible window.
	src := filepath.Join(dir, "main.py")
	if err := os.WriteFile(src, []byte(source), 0o600); err != nil {
		return sanitizeRunnerText(err.Error()), "", 1
	}
	return "", src, 0
}

func compilePascal(source string, dir string, timeoutSec int) (string, string, int) {
	if strings.Contains(strings.ToLower(source), "drawman") {
		return "DrawMan is not supported in pascal image runner. Use GraphABC.", "", 2
	}
	src := filepath.Join(dir, "main.pas")
	if err := os.WriteFile(src, []byte(source), 0o600); err != nil {
		return sanitizeRunnerText(err.Error()), "", 1
	}
	compiler := env("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")
	res := runCommand("mono", []string{compiler, src}, dir, "", clamp(timeoutSec, 6, 60), nil)
	if res.ExitCode != 0 {
		return sanitizeRunnerText(res.Stdout + res.Stderr), "", res.ExitCode
	}
	candidate := filepath.Join(dir, "main.exe")
	if _, err := os.Stat(candidate); err == nil {
		if err := os.Chmod(candidate, 0o700); err != nil {
			return "Не удалось подготовить программу к запуску.", "", 1
		}
		return sanitizeRunnerText(res.Stdout + res.Stderr), candidate, 0
	}
	matches, _ := filepath.Glob(filepath.Join(dir, "*.exe"))
	if len(matches) == 0 {
		return sanitizeRunnerText(res.Stdout + res.Stderr + "\ncompile succeeded but no .exe produced"), "", 1
	}
	if err := os.Chmod(matches[0], 0o700); err != nil {
		return "Не удалось подготовить программу к запуску.", "", 1
	}
	return sanitizeRunnerText(res.Stdout + res.Stderr), matches[0], 0
}

func fileMTime(path string) time.Time {
	st, err := os.Stat(path)
	if err != nil {
		return time.Time{}
	}
	return st.ModTime()
}

func findOutputFile(dir string) string {
	for _, n := range []string{"out.png", "out.ppm", "out.bmp", "out.jpg", "out.jpeg"} {
		p := filepath.Join(dir, n)
		if st, err := os.Stat(p); err == nil && st.Size() > 0 {
			return p
		}
	}
	entries, _ := os.ReadDir(dir)
	var files []string
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		p := filepath.Join(dir, e.Name())
		ext := strings.ToLower(filepath.Ext(e.Name()))
		if ext == ".png" || ext == ".ppm" || ext == ".bmp" || ext == ".jpg" || ext == ".jpeg" {
			if st, err := os.Stat(p); err == nil && st.Size() > 0 {
				files = append(files, p)
			}
		}
	}
	sort.Slice(files, func(i, j int) bool { return fileMTime(files[i]).After(fileMTime(files[j])) })
	if len(files) == 0 {
		return ""
	}
	return files[0]
}

func convertToPNG(path string, dir string) ([]byte, error) {
	if strings.EqualFold(filepath.Ext(path), ".png") {
		return os.ReadFile(path)
	}
	out := filepath.Join(dir, "converted.png")
	res := runCommand("convert", []string{path, out}, dir, "", 10, nil)
	if res.ExitCode != 0 {
		return nil, fmt.Errorf("image conversion failed: %s", tail(res.Stdout+res.Stderr, 4000))
	}
	return os.ReadFile(out)
}

func startLongProcess(name string, args []string, cwd string, input string, envs []string) (*exec.Cmd, *limitedBuffer, *limitedBuffer, error) {
	cmd := exec.Command(name, args...)
	cmd.Dir = cwd
	cmd.Env = append(os.Environ(), envs...)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	stdout := &limitedBuffer{max: maxTextLen}
	stderr := &limitedBuffer{max: maxTextLen}
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	if input != "" {
		pipe, err := cmd.StdinPipe()
		if err == nil {
			go func() {
				_, _ = io.WriteString(pipe, input)
				if !strings.HasSuffix(input, "\n") {
					_, _ = io.WriteString(pipe, "\n")
				}
				_ = pipe.Close()
			}()
		}
	}
	if err := cmd.Start(); err != nil {
		return nil, stdout, stderr, err
	}
	return cmd, stdout, stderr, nil
}

func stopProc(cmd *exec.Cmd) {
	if cmd == nil || cmd.Process == nil {
		return
	}
	_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGTERM)
	done := make(chan struct{})
	go func() { _, _ = cmd.Process.Wait(); close(done) }()
	select {
	case <-done:
	case <-time.After(1500 * time.Millisecond):
		_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
		_ = cmd.Process.Kill()
	}
}

func firstWindowID(pid int, display string) string {
	ctx, cancel := context.WithTimeout(context.Background(), 1200*time.Millisecond)
	defer cancel()
	cmd := exec.CommandContext(ctx, "xdotool", "search", "--onlyvisible", "--pid", strconv.Itoa(pid))
	cmd.Env = append(os.Environ(), "DISPLAY="+display)
	out, err := cmd.Output()
	if err != nil {
		return ""
	}
	ids := strings.Fields(string(out))
	if len(ids) == 0 {
		return ""
	}
	return ids[len(ids)-1]
}

func captureExecutable(exe string, dir string, stdin string, timeoutSec int, programArgs ...string) captureOutput {
	display := fmt.Sprintf(":%d", 90+rand.Intn(1000))
	xvfb, _, xvfbErr, err := startLongProcess("Xvfb", []string{display, "-screen", "0", env("TF_XVFB_SCREEN", "1280x1024x24")}, dir, "", nil)
	if err != nil {
		return captureOutput{Err: "failed to start Xvfb: " + err.Error() + " " + xvfbErr.String()}
	}
	defer stopProc(xvfb)
	time.Sleep(500 * time.Millisecond)

	wm, _, _, _ := startLongProcess("openbox", nil, dir, "", []string{"DISPLAY=" + display})
	defer stopProc(wm)
	time.Sleep(350 * time.Millisecond)

	proc, stdout, stderr, err := startLongProcess(exe, programArgs, dir, stdin, []string{"DISPLAY=" + display})
	if err != nil {
		return captureOutput{Err: "failed to start program: " + err.Error()}
	}
	defer stopProc(proc)

	deadline := time.Now().Add(time.Duration(timeoutSec) * time.Second)
	screenshot := filepath.Join(dir, "captured.png")
	var windowID string
	for time.Now().Before(deadline) {
		if out := findOutputFile(dir); out != "" {
			png, convErr := convertToPNG(out, dir)
			if convErr == nil {
				return captureOutput{PNG: png, Stdout: stdout.String(), Stderr: stderr.String()}
			}
		}
		if proc.ProcessState != nil && proc.ProcessState.Exited() {
			break
		}
		if windowID == "" {
			windowID = firstWindowID(proc.Process.Pid, display)
		}
		if windowID != "" {
			res := runCommand("import", []string{"-display", display, "-window", windowID, screenshot}, dir, "", 4, nil)
			if res.ExitCode == 0 {
				if st, statErr := os.Stat(screenshot); statErr == nil && st.Size() > 0 {
					png, readErr := os.ReadFile(screenshot)
					if readErr == nil {
						return captureOutput{PNG: png, Stdout: stdout.String(), Stderr: stderr.String()}
					}
				}
			}
		}
		time.Sleep(250 * time.Millisecond)
	}
	return captureOutput{Stdout: stdout.String(), Stderr: stderr.String(), Err: "Rendering failed or timed out"}
}

func handleRender(kind string, debug bool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		var req renderRequest
		if err := decodeJSON(r, &req); err != nil {
			sendError(w, http.StatusBadRequest, err.Error(), "", "")
			return
		}
		if strings.TrimSpace(req.Source) == "" {
			sendError(w, http.StatusBadRequest, "source is required", "", "")
			return
		}
		timeoutSec := timeout(req)
		dir, err := os.MkdirTemp("", "taskforge-image-"+kind+"-")
		if err != nil {
			sendError(w, http.StatusInternalServerError, err.Error(), "", "")
			return
		}
		defer os.RemoveAll(dir)

		var compileOut, exe string
		var code int
		if kind == "pascal" {
			compileOut, exe, code = compilePascal(req.Source, dir, timeoutSec)
		} else if kind == "python" {
			compileOut, exe, code = preparePython(req.Source, dir, timeoutSec)
		} else {
			compileOut, exe, code = compileCpp(req.Source, dir, timeoutSec)
		}
		if code != 0 {
			sendError(w, http.StatusBadRequest, "Compilation failed", "", compileOut)
			return
		}
		var cap captureOutput
		if kind == "python" {
			cap = captureExecutable("/usr/bin/python3", dir, value(req.Stdin), timeoutSec, exe)
		} else {
			cap = captureExecutable(exe, dir, value(req.Stdin), timeoutSec)
		}
		if len(cap.PNG) == 0 {
			sendError(w, http.StatusBadRequest, cap.Err, cap.Stdout, cap.Stderr)
			return
		}
		if debug {
			sendJSON(w, http.StatusOK, renderDebugResponse{PngBase64: base64.StdEncoding.EncodeToString(cap.PNG), Stdout: cap.Stdout, Stderr: cap.Stderr})
			return
		}
		w.Header().Set("Content-Type", "image/png")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(cap.PNG)
	}
}

func value(v *string) string {
	if v == nil {
		return ""
	}
	return *v
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
	kind := env("IMAGE_RUNNER_KIND", "cpp")
	port := env("PORT", "8000")
	rand.Seed(time.Now().UnixNano())
	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		sendJSON(w, http.StatusOK, map[string]any{"ok": true, "service": "image-" + kind + "-runner", "runtime": "go", "go": runtime.Version()})
	})
	mux.HandleFunc("/ready", func(w http.ResponseWriter, r *http.Request) {
		sendJSON(w, http.StatusOK, map[string]any{"ok": true})
	})
	mux.HandleFunc("/render", handleRender(kind, false))
	mux.HandleFunc("/render/debug", handleRender(kind, true))
	log.Printf("image-%s-runner listening on :%s", kind, port)
	handler := http.Handler(mux)
	if taskforgeDebugLogsEnabled() {
		handler = taskforgeDebugMiddleware("image-"+kind+"-runner", handler)
	}
	server := &http.Server{Addr: ":" + port, Handler: handler, ReadHeaderTimeout: 10 * time.Second}
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}
