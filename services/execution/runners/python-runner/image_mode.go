package main

import (
	"bytes"
	"context"
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

	"sort"
	"strconv"
	"strings"
	"syscall"
	"time"
)

const (
	imageMode_maxTextLen               = 1_000_000
	imageMode_maxRequestBytes          = 16 << 20
	imageMode_maxSourceBytes           = 1 << 20
	imageMode_maxStdinBytes            = 1 << 20
	imageMode_maxImageBytes            = 16 << 20
	imageMode_maxRenderRequestDuration = 34 * time.Second
	imageMode_prSetDumpable            = 4
	imageMode_pythonPolicySyntaxExit   = 87
)

type imageMode_renderRequest struct {
	Source          string             `json:"source"`
	Stdin           *string            `json:"stdin"`
	TimeoutSeconds  *int               `json:"timeoutSeconds"`
	TimeoutSeconds2 *int               `json:"timeout_seconds"`
	Debug           bool               `json:"debug"`
	Attestation     *policyAttestation `json:"attestation"`
}

type imageMode_renderDebugResponse struct {
	PngBase64 string `json:"pngBase64,omitempty"`
	Stdout    string `json:"stdout"`
	Stderr    string `json:"stderr"`
}

type imageMode_execOutput struct {
	ExitCode int
	Stdout   string
	Stderr   string
	TimedOut bool
}

type imageMode_captureOutput struct {
	PNG    []byte
	Stdout string
	Stderr string
	Err    string
}

type imageMode_limitedBuffer struct {
	buf       bytes.Buffer
	max       int
	truncated bool
}

func (b *imageMode_limitedBuffer) Write(p []byte) (int, error) {
	left := b.max - b.buf.Len()
	if left > 0 {
		if len(p) > left {
			_, _ = b.buf.Write(p[:left])
			b.truncated = true
		} else {
			_, _ = b.buf.Write(p)
		}
	} else if len(p) > 0 {
		b.truncated = true
	}
	return len(p), nil
}

func (b *imageMode_limitedBuffer) String() string {
	return strings.ReplaceAll(b.buf.String(), "\r\n", "\n")
}

func (b *imageMode_limitedBuffer) Bytes() []byte {
	return append([]byte(nil), b.buf.Bytes()...)
}

var imageMode_tmpTaskforgePathPattern = regexp.MustCompile(`/tmp/taskforge-[^\s:]+`)
var imageMode_tmpGoBuildPathPattern = regexp.MustCompile(`/tmp/go-build[^\s:]+`)
var imageMode_appPathPattern = regexp.MustCompile(`/app/[^\s:]+`)

func imageMode_sanitizeRunnerText(value string) string {
	if strings.TrimSpace(value) == "" {
		return value
	}
	s := imageMode_tmpTaskforgePathPattern.ReplaceAllString(value, "[временный файл]")
	s = imageMode_tmpGoBuildPathPattern.ReplaceAllString(s, "[временный файл]")
	s = imageMode_appPathPattern.ReplaceAllString(s, "[внутренний файл]")
	return s
}

func imageMode_friendlyRunnerError(value string) string {
	lower := strings.ToLower(value)
	if strings.Contains(lower, "fork/exec") && strings.Contains(lower, "permission denied") {
		return "Не удалось запустить программу: нет прав на выполнение файла проверки."
	}
	return imageMode_sanitizeRunnerText(value)
}

func imageMode_env(name, fallback string) string {
	v := strings.TrimSpace(os.Getenv(name))
	if v == "" {
		return fallback
	}
	return v
}

func imageMode_runnerChildEnvironment() []string {
	result := []string{
		"HOME=/tmp",
		"TMPDIR=/tmp",
	}
	for _, key := range []string{
		"PATH", "LANG", "LC_ALL", "LC_CTYPE", "TZ",
		"MPLBACKEND", "MPLCONFIGDIR", "PYTHONPYCACHEPREFIX",
		"MONO_REGISTRY_PATH", "PABCNETC",
	} {
		if value := strings.TrimSpace(os.Getenv(key)); value != "" {
			result = append(result, key+"="+value)
		}
	}
	return result
}

func imageMode_sandboxPreloadPath() (string, error) {
	path := strings.TrimSpace(os.Getenv("TASKFORGE_SANDBOX_PRELOAD"))
	if path == "" {
		path = "/app/libtaskforge_sandbox.so"
	}
	info, err := os.Stat(path)
	if err != nil {
		return "", err
	}
	if !info.Mode().IsRegular() {
		return "", fmt.Errorf("sandbox preload is not a regular file")
	}
	return path, nil
}

func imageMode_sandboxRuntimeEnvironment(seconds int) ([]string, error) {
	preload, err := imageMode_sandboxPreloadPath()
	if err != nil {
		return nil, err
	}
	cpuSeconds := imageMode_clamp(seconds+2, 2, 40)
	return []string{
		"LD_PRELOAD=" + preload,
		"TASKFORGE_SUBMISSION=1",
		"TASKFORGE_SANDBOX_PROFILE=image",
		"TASKFORGE_LIMIT_CPU_SECONDS=" + strconv.Itoa(cpuSeconds),
		"TASKFORGE_LIMIT_FSIZE_MB=32",
		"TASKFORGE_LIMIT_NOFILE=128",
		"DOTNET_EnableDiagnostics=0",
		"COMPlus_EnableDiagnostics=0",
		"PYTHONDONTWRITEBYTECODE=1",
	}, nil
}

func imageMode_helperSandboxEnvironment(seconds int) ([]string, error) {
	return imageMode_sandboxRuntimeEnvironment(seconds)
}

func imageMode_xserverSandboxEnvironment(seconds int) ([]string, error) {
	preload, err := imageMode_sandboxPreloadPath()
	if err != nil {
		return nil, err
	}
	return []string{
		"LD_PRELOAD=" + preload,
		"TASKFORGE_SUBMISSION=1",
		"TASKFORGE_SANDBOX_PROFILE=xserver",
		"TASKFORGE_LIMIT_CPU_SECONDS=" + strconv.Itoa(imageMode_clamp(seconds+4, 4, 45)),
		"TASKFORGE_LIMIT_FSIZE_MB=32",
		"TASKFORGE_LIMIT_NOFILE=128",
	}, nil
}

func imageMode_hardenRunnerProcess() error {
	_, _, errno := syscall.Syscall6(syscall.SYS_PRCTL, uintptr(imageMode_prSetDumpable), 0, 0, 0, 0, 0)
	if errno != 0 {
		return errno
	}
	return nil
}

func imageMode_timeout(req imageMode_renderRequest) int {
	if req.TimeoutSeconds2 != nil && *req.TimeoutSeconds2 > 0 {
		return imageMode_clamp(*req.TimeoutSeconds2, 1, 30)
	}
	if req.TimeoutSeconds != nil && *req.TimeoutSeconds > 0 {
		return imageMode_clamp(*req.TimeoutSeconds, 1, 30)
	}
	return 20
}

func imageMode_clamp(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func imageMode_secondsRemaining(deadline time.Time) int {
	remaining := time.Until(deadline)
	if remaining <= 0 {
		return 0
	}
	return int((remaining + time.Second - 1) / time.Second)
}

func imageMode_tail(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return "..." + s[len(s)-n:]
}

func imageMode_runCommand(name string, args []string, cwd string, input string, seconds int, extraEnv []string) imageMode_execOutput {
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(seconds)*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Env = append(imageMode_runnerChildEnvironment(), extraEnv...)
	cmd.Stdin = strings.NewReader(input)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true, Pdeathsig: syscall.SIGKILL}
	stdout := &imageMode_limitedBuffer{max: imageMode_maxTextLen}
	stderr := &imageMode_limitedBuffer{max: imageMode_maxTextLen}
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	if err := cmd.Start(); err != nil {
		return imageMode_execOutput{ExitCode: 127, Stderr: imageMode_friendlyRunnerError(err.Error())}
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
		return imageMode_execOutput{ExitCode: 124, Stdout: stdout.String(), Stderr: "Time limit exceeded", TimedOut: true}
	}
	code := 0
	if err != nil {
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) {
			code = exitErr.ExitCode()
		} else {
			code = 1
			if stderr.buf.Len() == 0 {
				_, _ = io.WriteString(stderr, imageMode_friendlyRunnerError(err.Error()))
			}
		}
	}
	return imageMode_execOutput{ExitCode: code, Stdout: stdout.String(), Stderr: imageMode_sanitizeRunnerText(stderr.String())}
}

func imageMode_sendJSON(w http.ResponseWriter, code int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(value)
}

func imageMode_sendError(w http.ResponseWriter, code int, message string, stdout string, stderr string) {
	imageMode_sendJSON(w, code, map[string]any{"detail": map[string]string{"message": imageMode_friendlyRunnerError(message), "stdout": imageMode_tail(stdout, 8000), "stderr": imageMode_tail(imageMode_friendlyRunnerError(stderr), 8000)}})
}

func imageMode_decodeJSON(r *http.Request, out any) error {
	defer r.Body.Close()
	data, err := io.ReadAll(io.LimitReader(r.Body, imageMode_maxRequestBytes+1))
	if err != nil {
		return err
	}
	if len(data) > imageMode_maxRequestBytes {
		return fmt.Errorf("request body is too large")
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	if err := dec.Decode(out); err != nil {
		return err
	}
	if err := dec.Decode(&struct{}{}); err != io.EOF {
		if err == nil {
			return fmt.Errorf("request body contains multiple JSON values")
		}
		return err
	}
	return nil
}

func imageMode_validateRenderRequest(req imageMode_renderRequest) error {
	if strings.TrimSpace(req.Source) == "" {
		return fmt.Errorf("source is required")
	}
	if len(req.Source) > imageMode_maxSourceBytes {
		return fmt.Errorf("source is too large")
	}
	if req.Stdin != nil && len(*req.Stdin) > imageMode_maxStdinBytes {
		return fmt.Errorf("stdin is too large")
	}
	return nil
}

func imageMode_readFileLimited(path string, maximum int64) ([]byte, error) {
	file, err := os.OpenFile(path, os.O_RDONLY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC, 0)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() || info.Size() <= 0 {
		return nil, fmt.Errorf("rendered image is not a regular file")
	}
	if info.Size() > maximum {
		return nil, fmt.Errorf("rendered image is too large")
	}
	data, err := io.ReadAll(io.LimitReader(file, maximum+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) > maximum {
		return nil, fmt.Errorf("rendered image is too large")
	}
	return data, nil
}

func imageMode_compileCpp(source string, dir string, timeoutSec int) (string, string, int) {
	src := filepath.Join(dir, "main.cpp")
	exe := filepath.Join(dir, "main")
	if err := os.WriteFile(src, []byte(source), 0o600); err != nil {
		return imageMode_sanitizeRunnerText(err.Error()), "", 1
	}
	res := imageMode_runCommand("g++", []string{"main.cpp", "-O2", "-std=c++17", "-I/opt/taskforge/include", "-lglut", "-lGL", "-lGLU", "-o", exe}, dir, "", imageMode_clamp(timeoutSec, 1, 30), nil)
	if res.ExitCode == 0 {
		if err := os.Chmod(exe, 0o700); err != nil {
			return "Не удалось подготовить программу к запуску.", "", 1
		}
	}
	return imageMode_sanitizeRunnerText(res.Stdout + res.Stderr), exe, res.ExitCode
}

func imageMode_preparePython(source string, dir string, timeoutSec int) (string, string, int) {
	// Python image runner supports standard turtle, matplotlib, Pillow and any code
	// that writes out.png/out.ppm/out.jpg. GUI code runs under Xvfb and can also be
	// captured from the visible window.
	src := filepath.Join(dir, "main.py")
	if err := os.WriteFile(src, []byte(source), 0o600); err != nil {
		return imageMode_sanitizeRunnerText(err.Error()), "", 1
	}
	policy := imageMode_runCommand("/usr/bin/python3", []string{"-I", "-B", "/opt/taskforge/python_policy.py", "main.py", "image"}, dir, "", imageMode_clamp(timeoutSec, 1, 20), nil)
	if policy.ExitCode != 0 && policy.ExitCode != imageMode_pythonPolicySyntaxExit {
		return "Решение отклонено системой безопасности.", "", 86
	}
	compile := imageMode_runCommand("/usr/bin/python3", []string{"-I", "-B", "-m", "py_compile", "main.py"}, dir, "", imageMode_clamp(timeoutSec, 1, 20), nil)
	if compile.ExitCode != 0 {
		return imageMode_sanitizeRunnerText(compile.Stdout + compile.Stderr), "", compile.ExitCode
	}
	if policy.ExitCode == imageMode_pythonPolicySyntaxExit {
		// Keep the image runner fail-closed if AST parsing and py_compile ever
		// disagree: source that was not policy-inspected is never executed.
		return "Python syntax error", "", 2
	}
	return "", src, 0
}

func imageMode_compilePascal(source string, dir string, timeoutSec int) (string, string, int) {
	if strings.Contains(strings.ToLower(source), "drawman") {
		return "DrawMan is not supported in pascal image runner. Use GraphABC.", "", 2
	}
	src := filepath.Join(dir, "main.pas")
	if err := os.WriteFile(src, []byte(source), 0o600); err != nil {
		return imageMode_sanitizeRunnerText(err.Error()), "", 1
	}
	compiler := imageMode_env("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")
	res := imageMode_runCommand("mono", []string{compiler, src}, dir, "", imageMode_clamp(timeoutSec, 1, 30), nil)
	if res.ExitCode != 0 {
		return imageMode_sanitizeRunnerText(res.Stdout + res.Stderr), "", res.ExitCode
	}
	candidate := filepath.Join(dir, "main.exe")
	if _, err := os.Stat(candidate); err == nil {
		if err := os.Chmod(candidate, 0o700); err != nil {
			return "Не удалось подготовить программу к запуску.", "", 1
		}
		return imageMode_sanitizeRunnerText(res.Stdout + res.Stderr), candidate, 0
	}
	matches, _ := filepath.Glob(filepath.Join(dir, "*.exe"))
	if len(matches) == 0 {
		return imageMode_sanitizeRunnerText(res.Stdout + res.Stderr + "\ncompile succeeded but no .exe produced"), "", 1
	}
	if err := os.Chmod(matches[0], 0o700); err != nil {
		return "Не удалось подготовить программу к запуску.", "", 1
	}
	return imageMode_sanitizeRunnerText(res.Stdout + res.Stderr), matches[0], 0
}

func imageMode_fileMTime(path string) time.Time {
	st, err := os.Lstat(path)
	if err != nil {
		return time.Time{}
	}
	return st.ModTime()
}

func imageMode_findOutputFile(dir string) string {
	for _, n := range []string{"out.png", "out.ppm", "out.bmp", "out.jpg", "out.jpeg"} {
		p := filepath.Join(dir, n)
		if st, err := os.Lstat(p); err == nil && st.Mode().IsRegular() && st.Size() > 0 {
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
			if st, err := os.Lstat(p); err == nil && st.Mode().IsRegular() && st.Size() > 0 {
				files = append(files, p)
			}
		}
	}
	sort.Slice(files, func(i, j int) bool { return imageMode_fileMTime(files[i]).After(imageMode_fileMTime(files[j])) })
	if len(files) == 0 {
		return ""
	}
	return files[0]
}

func imageMode_runBinaryCommand(name string, args []string, cwd string, seconds int, extraEnv []string) ([]byte, string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(imageMode_clamp(seconds, 1, 10))*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Env = append(imageMode_runnerChildEnvironment(), extraEnv...)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true, Pdeathsig: syscall.SIGKILL}
	stdout := &imageMode_limitedBuffer{max: imageMode_maxImageBytes + 1}
	stderr := &imageMode_limitedBuffer{max: 64 * 1024}
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	err := cmd.Run()
	if ctx.Err() != nil {
		return nil, stderr.String(), ctx.Err()
	}
	if err != nil {
		return nil, stderr.String(), err
	}
	if stdout.truncated || len(stdout.Bytes()) > imageMode_maxImageBytes {
		return nil, stderr.String(), fmt.Errorf("rendered image is too large")
	}
	if len(stdout.Bytes()) == 0 {
		return nil, stderr.String(), fmt.Errorf("renderer produced an empty image")
	}
	return stdout.Bytes(), stderr.String(), nil
}

func imageMode_convertToPNG(path string, dir string, seconds int, helperEnv []string) ([]byte, error) {
	if strings.EqualFold(filepath.Ext(path), ".png") {
		return imageMode_readFileLimited(path, imageMode_maxImageBytes)
	}
	png, stderr, err := imageMode_runBinaryCommand("convert", []string{path, "png:-"}, dir, seconds, helperEnv)
	if err != nil {
		return nil, fmt.Errorf("image conversion failed: %s", imageMode_tail(stderr, 4000))
	}
	return png, nil
}

func imageMode_captureWindowPNG(display string, windowID string, seconds int, helperEnv []string) ([]byte, error) {
	png, stderr, err := imageMode_runBinaryCommand(
		"import",
		[]string{"-display", display, "-window", windowID, "png:-"},
		"/tmp",
		seconds,
		append([]string{"DISPLAY=" + display}, helperEnv...),
	)
	if err != nil {
		return nil, fmt.Errorf("window capture failed: %s", imageMode_tail(stderr, 4000))
	}
	return png, nil
}

type imageMode_longProcess struct {
	cmd  *exec.Cmd
	done chan error
}

func imageMode_startLongProcess(name string, args []string, cwd string, input string, envs []string) (*imageMode_longProcess, *imageMode_limitedBuffer, *imageMode_limitedBuffer, error) {
	cmd := exec.Command(name, args...)
	cmd.Dir = cwd
	cmd.Env = append(imageMode_runnerChildEnvironment(), envs...)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true, Pdeathsig: syscall.SIGKILL}
	stdout := &imageMode_limitedBuffer{max: imageMode_maxTextLen}
	stderr := &imageMode_limitedBuffer{max: imageMode_maxTextLen}
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	var inputPipe io.WriteCloser
	if input != "" {
		pipe, err := cmd.StdinPipe()
		if err != nil {
			return nil, stdout, stderr, err
		}
		inputPipe = pipe
	}
	if err := cmd.Start(); err != nil {
		if inputPipe != nil {
			_ = inputPipe.Close()
		}
		return nil, stdout, stderr, err
	}
	process := &imageMode_longProcess{cmd: cmd, done: make(chan error, 1)}
	go func() {
		process.done <- cmd.Wait()
		close(process.done)
	}()
	if inputPipe != nil {
		go func() {
			_, _ = io.WriteString(inputPipe, input)
			if !strings.HasSuffix(input, "\n") {
				_, _ = io.WriteString(inputPipe, "\n")
			}
			_ = inputPipe.Close()
		}()
	}
	return process, stdout, stderr, nil
}

func imageMode_processExited(process *imageMode_longProcess) bool {
	if process == nil {
		return true
	}
	select {
	case <-process.done:
		return true
	default:
		return false
	}
}

func imageMode_stopProc(process *imageMode_longProcess) {
	if process == nil || process.cmd == nil || process.cmd.Process == nil {
		return
	}
	if imageMode_processExited(process) {
		return
	}
	_ = syscall.Kill(-process.cmd.Process.Pid, syscall.SIGTERM)
	select {
	case <-process.done:
		return
	case <-time.After(1500 * time.Millisecond):
	}
	_ = syscall.Kill(-process.cmd.Process.Pid, syscall.SIGKILL)
	_ = process.cmd.Process.Kill()
	select {
	case <-process.done:
	case <-time.After(1500 * time.Millisecond):
	}
}

func imageMode_firstWindowID(pid int, display string, helperEnv []string) string {
	ctx, cancel := context.WithTimeout(context.Background(), 1200*time.Millisecond)
	defer cancel()
	cmd := exec.CommandContext(ctx, "xdotool", "search", "--onlyvisible", "--pid", strconv.Itoa(pid))
	cmd.Env = append(imageMode_runnerChildEnvironment(), append([]string{"DISPLAY=" + display}, helperEnv...)...)
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

func imageMode_captureExecutable(exe string, dir string, stdin string, timeoutSec int, programArgs ...string) imageMode_captureOutput {
	helperEnv, helperErr := imageMode_helperSandboxEnvironment(timeoutSec)
	if helperErr != nil {
		log.Printf("image runner helper sandbox is unavailable: %v", helperErr)
		return imageMode_captureOutput{Err: "Решение отклонено системой безопасности."}
	}
	xserverEnv, xserverErr := imageMode_xserverSandboxEnvironment(timeoutSec)
	if xserverErr != nil {
		log.Printf("image runner X server sandbox is unavailable: %v", xserverErr)
		return imageMode_captureOutput{Err: "Решение отклонено системой безопасности."}
	}

	display := fmt.Sprintf(":%d", 90+rand.Intn(1000))
	xvfbArgs := []string{display, "-nolisten", "tcp", "-extension", "XTEST", "-screen", "0", imageMode_env("TF_XVFB_SCREEN", "1280x1024x24")}
	xvfb, _, xvfbErr, err := imageMode_startLongProcess("Xvfb", xvfbArgs, dir, "", xserverEnv)
	if err != nil {
		return imageMode_captureOutput{Err: "failed to start Xvfb: " + err.Error() + " " + xvfbErr.String()}
	}
	defer imageMode_stopProc(xvfb)
	time.Sleep(500 * time.Millisecond)

	openboxEnv := append([]string{"DISPLAY=" + display}, helperEnv...)
	wm, _, _, _ := imageMode_startLongProcess("openbox", []string{"--config-file", "/etc/xdg/openbox/rc.xml"}, dir, "", openboxEnv)
	defer imageMode_stopProc(wm)
	time.Sleep(350 * time.Millisecond)

	sandboxEnv, sandboxErr := imageMode_sandboxRuntimeEnvironment(timeoutSec)
	if sandboxErr != nil {
		log.Printf("image runner sandbox preload is unavailable: %v", sandboxErr)
		return imageMode_captureOutput{Err: "Решение отклонено системой безопасности."}
	}
	programEnv := append([]string{"DISPLAY=" + display}, sandboxEnv...)

	proc, stdout, stderr, err := imageMode_startLongProcess(exe, programArgs, dir, stdin, programEnv)
	if err != nil {
		return imageMode_captureOutput{Err: "failed to start program: " + err.Error()}
	}
	defer func() { imageMode_stopProc(proc) }()

	deadline := time.Now().Add(time.Duration(timeoutSec) * time.Second)
	var windowID string
	for time.Now().Before(deadline) {
		if out := imageMode_findOutputFile(dir); out != "" {
			// Stop untrusted writers before opening or converting their output.
			imageMode_stopProc(proc)
			proc = nil
			remaining := imageMode_secondsRemaining(deadline)
			if remaining <= 0 {
				break
			}
			png, convErr := imageMode_convertToPNG(out, dir, imageMode_clamp(remaining, 1, 10), helperEnv)
			if convErr == nil {
				return imageMode_captureOutput{PNG: png, Stdout: stdout.String(), Stderr: stderr.String()}
			}
			return imageMode_captureOutput{Stdout: stdout.String(), Stderr: stderr.String(), Err: convErr.Error()}
		}
		if imageMode_processExited(proc) {
			break
		}
		if windowID == "" {
			windowID = imageMode_firstWindowID(proc.cmd.Process.Pid, display, helperEnv)
		}
		if windowID != "" {
			remaining := imageMode_secondsRemaining(deadline)
			if remaining <= 0 {
				break
			}
			if png, captureErr := imageMode_captureWindowPNG(display, windowID, imageMode_clamp(remaining, 1, 4), helperEnv); captureErr == nil {
				return imageMode_captureOutput{PNG: png, Stdout: stdout.String(), Stderr: stderr.String()}
			}
		}
		// Some GUI stacks create windows that cannot be associated with the
		// submission pid. Capture the X root directly without a shared output file.
		remaining := imageMode_secondsRemaining(deadline)
		if remaining > 0 {
			if png, captureErr := imageMode_captureWindowPNG(display, "root", imageMode_clamp(remaining, 1, 4), helperEnv); captureErr == nil {
				return imageMode_captureOutput{PNG: png, Stdout: stdout.String(), Stderr: stderr.String()}
			}
		}

		time.Sleep(250 * time.Millisecond)
	}
	return imageMode_captureOutput{Stdout: stdout.String(), Stderr: stderr.String(), Err: "Rendering failed or timed out"}
}

func imageMode_handleRender(kind string, debug bool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		var req imageMode_renderRequest
		if err := imageMode_decodeJSON(r, &req); err != nil {
			imageMode_sendError(w, http.StatusBadRequest, err.Error(), "", "")
			return
		}
		if err := imageMode_validateRenderRequest(req); err != nil {
			imageMode_sendError(w, http.StatusBadRequest, err.Error(), "", "")
			return
		}
		if err := verifyPolicyAttestation(kind, "image", req.Source, req.Attestation); err != nil {
			log.Printf("image-%s-runner rejected unattested source: %v", kind, err)
			imageMode_sendError(w, http.StatusForbidden, "Решение не прошло обязательную проверку безопасности.", "", "")
			return
		}
		timeoutSec := imageMode_timeout(req)
		requestDeadline := time.Now().Add(imageMode_maxRenderRequestDuration)
		dir, err := os.MkdirTemp("", "taskforge-image-"+kind+"-")
		if err != nil {
			imageMode_sendError(w, http.StatusInternalServerError, err.Error(), "", "")
			return
		}
		defer os.RemoveAll(dir)

		remaining := imageMode_secondsRemaining(requestDeadline)
		if remaining <= 0 {
			imageMode_sendError(w, http.StatusRequestTimeout, "Rendering timed out", "", "")
			return
		}
		compileBudget := imageMode_clamp(remaining, 1, 20)
		var compileOut, exe string
		var code int
		if kind == "pascal" {
			compileOut, exe, code = imageMode_compilePascal(req.Source, dir, compileBudget)
		} else if kind == "python" {
			compileOut, exe, code = imageMode_preparePython(req.Source, dir, compileBudget)
		} else {
			compileOut, exe, code = imageMode_compileCpp(req.Source, dir, imageMode_clamp(compileBudget, 1, 15))
		}
		if code != 0 {
			imageMode_sendError(w, http.StatusBadRequest, "Compilation failed", "", compileOut)
			return
		}
		remaining = imageMode_secondsRemaining(requestDeadline)
		if remaining <= 5 {
			imageMode_sendError(w, http.StatusRequestTimeout, "Rendering timed out", "", "")
			return
		}
		renderBudget := imageMode_clamp(timeoutSec, 1, remaining-5)
		var cap imageMode_captureOutput
		if kind == "python" {
			cap = imageMode_captureExecutable("/usr/bin/python3", dir, imageMode_value(req.Stdin), renderBudget, "/opt/taskforge/python_image_entry.py", exe)
		} else {
			cap = imageMode_captureExecutable(exe, dir, imageMode_value(req.Stdin), renderBudget)
		}
		if len(cap.PNG) == 0 {
			imageMode_sendError(w, http.StatusBadRequest, cap.Err, cap.Stdout, cap.Stderr)
			return
		}
		if debug {
			imageMode_sendJSON(w, http.StatusOK, imageMode_renderDebugResponse{PngBase64: base64.StdEncoding.EncodeToString(cap.PNG), Stdout: cap.Stdout, Stderr: cap.Stderr})
			return
		}
		w.Header().Set("Content-Type", "image/png")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(cap.PNG)
	}
}

func imageMode_value(v *string) string {
	if v == nil {
		return ""
	}
	return *v
}

func imageMode_taskforgeDebugLogsEnabled() bool {
	v := strings.ToLower(strings.TrimSpace(os.Getenv("TASKFORGE_DEBUG_LOGS")))
	return v == "1" || v == "true" || v == "yes" || v == "on" || v == "debug"
}

type imageMode_taskforgeStatusWriter struct {
	http.ResponseWriter
	status int
	bytes  int
}

func (w *imageMode_taskforgeStatusWriter) WriteHeader(code int) {
	if w.status != 0 {
		return
	}
	w.status = code
	w.ResponseWriter.WriteHeader(code)
}

func (w *imageMode_taskforgeStatusWriter) Write(p []byte) (int, error) {
	if w.status == 0 {
		w.WriteHeader(http.StatusOK)
	}
	n, err := w.ResponseWriter.Write(p)
	w.bytes += n
	return n, err
}

func imageMode_taskforgeDebugMiddleware(service string, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started := time.Now()
		traceID := r.Header.Get("X-TaskForge-Trace-Id")
		if traceID == "" {
			traceID = fmt.Sprintf("%s-%d", service, time.Now().UnixNano())
		}
		log.Printf("[TFDBG RUNNER IN START] trace=%s service=%s method=%s path=%s query=%s remote=%s content_length=%d content_type=%q", traceID, service, r.Method, r.URL.Path, r.URL.RawQuery, r.RemoteAddr, r.ContentLength, r.Header.Get("Content-Type"))
		writer := &imageMode_taskforgeStatusWriter{ResponseWriter: w}
		next.ServeHTTP(writer, r)
		if writer.status == 0 {
			writer.status = http.StatusOK
		}
		log.Printf("[TFDBG RUNNER IN END] trace=%s service=%s method=%s path=%s status=%d duration=%s response_bytes=%d", traceID, service, r.Method, r.URL.Path, writer.status, time.Since(started), writer.bytes)
	})
}

func imageMode_jobLimitMiddleware(slots chan struct{}, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case slots <- struct{}{}:
			defer func() { <-slots }()
			next.ServeHTTP(w, r)
		case <-r.Context().Done():
			imageMode_sendError(w, http.StatusRequestTimeout, "request cancelled", "", "")
		}
	})
}

// One active submission per container prevents cross-submission /proc and /tmp interference.

func imageModeReady() error {
	_, err := imageMode_sandboxPreloadPath()
	return err
}

func registerImageRoutes(mux *http.ServeMux, kind string) {
	mux.Handle("/render", imageMode_jobLimitMiddleware(runnerJobSlots, imageMode_handleRender(kind, false)))
	mux.Handle("/render/debug", imageMode_jobLimitMiddleware(runnerJobSlots, imageMode_handleRender(kind, true)))
}
