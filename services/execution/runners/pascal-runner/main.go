package main

import (
	"bytes"
	"context"
	"debug/elf"
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

const (
	maxOutLen              = 1_000_000
	maxRequestBytes        = 16 << 20
	maxCodeBytes           = 1 << 20
	maxInputBytes          = 1 << 20
	maxExpectedOutputBytes = 1 << 20
	maxTestsPerRequest     = 128
	maxTotalTestDataBytes  = 12 << 20
	minTimeLimitMs         = 100
	maxTimeLimitMs         = 30_000
	minMemoryLimitMb       = 32
	maxMemoryLimitMb       = 512
	maxBatchDuration       = 40 * time.Second
	prSetDumpable          = 4
)

type runRequest struct {
	Code          string             `json:"code"`
	Input         *string            `json:"input"`
	TimeLimitMs   *int               `json:"timeLimitMs"`
	MemoryLimitMb *int               `json:"memoryLimitMb"`
	Attestation   *policyAttestation `json:"attestation"`
}

type testCase struct {
	Input          *string `json:"input"`
	ExpectedOutput *string `json:"expectedOutput"`
	IsHidden       bool    `json:"isHidden"`
}

type testsRequest struct {
	Code          string             `json:"code"`
	Tests         []testCase         `json:"tests"`
	TimeLimitMs   *int               `json:"timeLimitMs"`
	MemoryLimitMb *int               `json:"memoryLimitMb"`
	Attestation   *policyAttestation `json:"attestation"`
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
	Env  []string
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

func boundedIntValue(v *int, fallback, minimum, maximum int) int {
	value := fallback
	if v != nil && *v > 0 {
		value = *v
	}
	if value < minimum {
		return minimum
	}
	if value > maximum {
		return maximum
	}
	return value
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

func pascalSandboxAssets() error {
	guardInfo, err := os.Stat("/app/taskforge_pascal_sandbox_guard.o")
	if err != nil {
		return err
	}
	if !guardInfo.Mode().IsRegular() {
		return fmt.Errorf("sandbox guard is not a regular file")
	}
	entries, err := os.ReadDir("/opt/taskforge/pascal-guard")
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if !entry.IsDir() && strings.EqualFold(entry.Name(), "taskforgesandboxguard.ppu") {
			return nil
		}
	}
	return fmt.Errorf("sandbox unit is missing")
}

func sandboxRuntimeEnvironment(timeMs int) ([]string, error) {
	if err := pascalSandboxAssets(); err != nil {
		return nil, err
	}
	cpuSeconds := (timeMs+999)/1000 + 2
	if cpuSeconds < 2 {
		cpuSeconds = 2
	}
	return []string{
		"TASKFORGE_SUBMISSION=1",
		"TASKFORGE_SANDBOX_PROFILE=managed",
		"TASKFORGE_LIMIT_CPU_SECONDS=" + strconv.Itoa(cpuSeconds),
		"TASKFORGE_LIMIT_FSIZE_MB=16",
		"TASKFORGE_LIMIT_NOFILE=128",
	}, nil
}

func hardenRunnerProcess() error {
	_, _, errno := syscall.Syscall6(syscall.SYS_PRCTL, uintptr(prSetDumpable), 0, 0, 0, 0, 0)
	if errno != 0 {
		return errno
	}
	return nil
}

func validateRunRequest(code string, input *string) error {
	if len(code) == 0 {
		return fmt.Errorf("code is empty")
	}
	if len(code) > maxCodeBytes {
		return fmt.Errorf("code is too large")
	}
	if input != nil && len(*input) > maxInputBytes {
		return fmt.Errorf("input is too large")
	}
	return nil
}

func validateTestsRequest(req *testsRequest) error {
	if err := validateRunRequest(req.Code, nil); err != nil {
		return err
	}
	if len(req.Tests) > maxTestsPerRequest {
		return fmt.Errorf("too many tests")
	}
	total := 0
	for _, test := range req.Tests {
		if test.Input != nil {
			if len(*test.Input) > maxInputBytes {
				return fmt.Errorf("test input is too large")
			}
			total += len(*test.Input)
		}
		if test.ExpectedOutput != nil {
			if len(*test.ExpectedOutput) > maxExpectedOutputBytes {
				return fmt.Errorf("expected output is too large")
			}
			total += len(*test.ExpectedOutput)
		}
		if total > maxTotalTestDataBytes {
			return fmt.Errorf("test data is too large")
		}
	}
	return nil
}

var pascalTokenPattern = regexp.MustCompile(`(?i)\b(program|uses|unit|library)\b|;`)

var safePascalDirectives = map[string]struct{}{
	"apptype": {}, "asmmode": {}, "calling": {}, "codepage": {},
	"debuginfo": {}, "denormals": {}, "extendedsyntax": {}, "excessprecision": {},
	"fputype": {}, "hint": {}, "ieeeerrors": {}, "inline": {},
	"interfaces": {}, "legacyifend": {}, "longstrings": {}, "maxfpuregisters": {},
	"minenumsize": {}, "mode": {}, "modeswitch": {}, "note": {},
	"objectivec1": {}, "openstrings": {}, "optimization": {}, "optimize": {},
	"packenum": {}, "packrecords": {}, "packset": {}, "pointermath": {},
	"rtti": {}, "scopedenums": {}, "smartlink": {}, "typedaddress": {},
	"typeinfo": {}, "warn": {}, "warning": {}, "warnings": {}, "writableconst": {},
}

var safePascalToggleDirectives = map[string]struct{}{
	"a": {}, "b": {}, "c": {}, "d": {}, "g": {}, "h": {},
	"i": {}, "j": {}, "m": {}, "p": {}, "q": {}, "r": {},
	"s": {}, "t": {}, "v": {}, "x": {},
}

func preparePascalSource(source string) (string, error) {
	if containsBlockedPascalDirective(source) {
		return "", fmt.Errorf("unsupported Pascal compiler directive")
	}
	clean := maskPascalCommentsAndStrings(source)
	tokens := pascalTokenPattern.FindAllStringIndex(clean, -1)
	if len(tokens) == 0 {
		return "uses TaskForgeSandboxGuard;\n" + source, nil
	}

	first := strings.ToLower(clean[tokens[0][0]:tokens[0][1]])
	if first == "unit" || first == "library" {
		return "", fmt.Errorf("only Pascal programs are supported")
	}

	insertionPoint := 0
	nextToken := 0
	if first == "program" {
		semicolon := -1
		for index := 1; index < len(tokens); index++ {
			if clean[tokens[index][0]:tokens[index][1]] == ";" {
				semicolon = index
				break
			}
		}
		if semicolon < 0 {
			return "", fmt.Errorf("invalid Pascal program header")
		}
		insertionPoint = tokens[semicolon][1]
		nextToken = semicolon + 1
	}

	if nextToken < len(tokens) {
		token := strings.ToLower(clean[tokens[nextToken][0]:tokens[nextToken][1]])
		if token == "unit" || token == "library" {
			return "", fmt.Errorf("only Pascal programs are supported")
		}
		if token == "uses" {
			position := tokens[nextToken][1]
			return source[:position] + " TaskForgeSandboxGuard," + source[position:], nil
		}
	}
	return source[:insertionPoint] + "\nuses TaskForgeSandboxGuard;\n" + source[insertionPoint:], nil
}

func containsBlockedPascalDirective(source string) bool {
	for _, directive := range pascalCompilerDirectives(source) {
		text := strings.TrimSpace(strings.ToLower(directive))
		if text == "" {
			continue
		}
		end := 0
		for end < len(text) {
			ch := text[end]
			if (ch < 'a' || ch > 'z') && ch != '_' {
				break
			}
			end++
		}
		if end == 0 {
			continue
		}
		command := text[:end]
		remainder := strings.TrimSpace(text[end:])
		if _, toggle := safePascalToggleDirectives[command]; toggle {
			// Single-letter compiler switches are accepted only in their +/- form.
			// File-loading forms such as {$I file}, {$L object.o}, and {$R resource}
			// never reach the compiler.
			if remainder != "+" && remainder != "-" {
				return true
			}
			continue
		}
		if _, safe := safePascalDirectives[command]; safe {
			continue
		}
		// Unknown directives are rejected fail-closed. The OJ supports language
		// and diagnostic switches, not source inclusion or linker manipulation.
		return true
	}
	return false
}

func pascalCompilerDirectives(source string) []string {
	var directives []string
	for index := 0; index < len(source); {
		switch {
		case source[index] == '\'':
			index++
			for index < len(source) {
				if source[index] != '\'' {
					index++
					continue
				}
				if index+1 < len(source) && source[index+1] == '\'' {
					index += 2
					continue
				}
				index++
				break
			}
		case index+1 < len(source) && source[index] == '/' && source[index+1] == '/':
			index += 2
			for index < len(source) && source[index] != '\n' {
				index++
			}
		case source[index] == '{':
			isDirective := index+1 < len(source) && source[index+1] == '$'
			start := index + 1
			if isDirective {
				start++
			}
			index++
			for index < len(source) && source[index] != '}' {
				index++
			}
			if isDirective && index <= len(source) {
				directives = append(directives, source[start:index])
			}
			if index < len(source) {
				index++
			}
		case index+1 < len(source) && source[index] == '(' && source[index+1] == '*':
			isDirective := index+2 < len(source) && source[index+2] == '$'
			start := index + 2
			if isDirective {
				start++
			}
			index += 2
			for index+1 < len(source) && !(source[index] == '*' && source[index+1] == ')') {
				index++
			}
			if isDirective {
				end := index
				if end > len(source) {
					end = len(source)
				}
				directives = append(directives, source[start:end])
			}
			if index+1 < len(source) {
				index += 2
			} else {
				index = len(source)
			}
		default:
			index++
		}
	}
	return directives
}

func maskPascalCommentsAndStrings(source string) string {
	data := []byte(source)
	masked := append([]byte(nil), data...)
	for index := 0; index < len(data); {
		switch {
		case data[index] == '\'':
			masked[index] = ' '
			index++
			for index < len(data) {
				masked[index] = ' '
				if data[index] == '\'' {
					if index+1 < len(data) && data[index+1] == '\'' {
						masked[index+1] = ' '
						index += 2
						continue
					}
					index++
					break
				}
				index++
			}
		case data[index] == '{':
			for index < len(data) {
				masked[index] = ' '
				if data[index] == '}' {
					index++
					break
				}
				index++
			}
		case index+1 < len(data) && data[index] == '(' && data[index+1] == '*':
			masked[index], masked[index+1] = ' ', ' '
			index += 2
			for index < len(data) {
				masked[index] = ' '
				if index+1 < len(data) && data[index] == '*' && data[index+1] == ')' {
					masked[index+1] = ' '
					index += 2
					break
				}
				index++
			}
		case index+1 < len(data) && data[index] == '/' && data[index+1] == '/':
			for index < len(data) && data[index] != '\n' {
				masked[index] = ' '
				index++
			}
		default:
			index++
		}
	}
	return string(masked)
}

func verifyPascalSandbox(path string) error {
	file, err := elf.Open(path)
	if err != nil {
		return err
	}
	defer file.Close()
	all := make([]elf.Symbol, 0, 64)
	if symbols, symbolErr := file.Symbols(); symbolErr == nil {
		all = append(all, symbols...)
	} else if !errors.Is(symbolErr, elf.ErrNoSymbols) {
		return symbolErr
	}
	if symbols, symbolErr := file.DynamicSymbols(); symbolErr == nil {
		all = append(all, symbols...)
	} else if !errors.Is(symbolErr, elf.ErrNoSymbols) {
		return symbolErr
	}
	for _, symbol := range all {
		if symbol.Section != elf.SHN_UNDEF && normalizePascalSymbol(symbol.Name) == "taskforge_pascal_sandbox_init" {
			return nil
		}
	}
	return fmt.Errorf("sandbox guard symbol is missing")
}

func normalizePascalSymbol(name string) string {
	if index := strings.IndexByte(name, '@'); index >= 0 {
		name = name[:index]
	}
	return strings.TrimSpace(name)
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
		return 384
	case "javascript":
		return 384
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
	return runCommandContext(context.Background(), name, args, cwd, input, timeout)
}

func runCommandContext(parent context.Context, name string, args []string, cwd string, input string, timeout time.Duration) commandResult {
	return runCommandWithEnvContext(parent, name, args, cwd, input, timeout, nil)
}

func runCommandWithEnv(name string, args []string, cwd string, input string, timeout time.Duration, extraEnv []string) commandResult {
	return runCommandWithEnvContext(context.Background(), name, args, cwd, input, timeout, extraEnv)
}

func runCommandWithEnvContext(parent context.Context, name string, args []string, cwd string, input string, timeout time.Duration, extraEnv []string) commandResult {
	if parent == nil {
		parent = context.Background()
	}
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()

	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Env = append(runnerChildEnvironment(), extraEnv...)
	cmd.Stdin = strings.NewReader(input)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true, Pdeathsig: syscall.SIGKILL}

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
	return compileProgramContext(context.Background(), kind, code, cwd, timeMs, memMb)
}

func compileProgramContext(parent context.Context, kind, code, cwd string, timeMs, memMb int) (*preparedProgram, *processResult) {
	switch kind {
	case "cpp":
		src := filepath.Join(cwd, "main.cpp")
		bin := filepath.Join(cwd, "a.out")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommandContext(parent, "g++", []string{"-std=c++17", "-O2", "-pipe", "-static-libgcc", "-static-libstdc++", "main.cpp", "-o", "a.out"}, cwd, "", timeoutDuration(timeMs))
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
		res := runCommandContext(parent, "javac", []string{"Main.java"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compilation error"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: 2, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}
		heap := javaHeapMb(memMb)
		args := []string{"-Xms16m", fmt.Sprintf("-Xmx%dm", heap), "-XX:+UseSerialGC", "-XX:-UsePerfData", "-Djava.io.tmpdir=/tmp", "-XX:ReservedCodeCacheSize=16m", "-XX:InitialCodeCacheSize=8m", "-XX:MaxMetaspaceSize=64m", "-XX:CompressedClassSpaceSize=32m", "-XX:+ExitOnOutOfMemoryError", "Main"}
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
		res := runCommandContext(parent, "python3", []string{"-I", "-B", "-m", "py_compile", "main.py"}, cwd, "", timeoutDuration(timeMs))
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
		preparedSource, prepareErr := preparePascalSource(code)
		if prepareErr != nil {
			message := prepareErr.Error()
			return nil, &processResult{Status: "policy_error", ExitCode: 126, Stdout: "", Stderr: "Решение отклонено системой безопасности.", CompileStderr: &message}
		}
		src := filepath.Join(cwd, "main.pas")
		bin := filepath.Join(cwd, "main")
		if err := os.WriteFile(src, []byte(preparedSource), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommandContext(parent, "fpc", []string{"main.pas", "-O2", "-vw", "-Fu/opt/taskforge/pascal-guard", "-omain"}, cwd, "", timeoutDuration(timeMs))
		if res.ExitCode != 0 {
			msg := strings.TrimSpace(res.Stdout + res.Stderr)
			if msg == "" {
				msg = "Compiler produced no output"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: 2, Stdout: "", Stderr: "Compilation error:\n" + msg + "\n", CompileStderr: ptr(msg + "\n")}
		}
		if err := verifyPascalSandbox(bin); err != nil {
			log.Printf("pascal-runner failed to verify linked sandbox: %v", err)
			return nil, &processResult{Status: "policy_error", ExitCode: 126, Stdout: "", Stderr: "Решение отклонено системой безопасности.", CompileStderr: nil}
		}
		if err := os.Chmod(bin, 0o500); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stdout: "", Stderr: "Не удалось подготовить программу к запуску.", CompileStderr: nil}
		}
		return &preparedProgram{Cwd: cwd, Cmd: "./main"}, nil
	default:
		return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: "Unsupported runner kind: " + kind}
	}
}

func executeProgram(kind string, p *preparedProgram, input string, timeMs int) processResult {
	return executeProgramContext(context.Background(), kind, p, input, timeMs)
}

func executeProgramContext(parent context.Context, kind string, p *preparedProgram, input string, timeMs int) processResult {
	extraEnv, err := sandboxRuntimeEnvironment(timeMs)
	if err != nil {
		log.Printf("%s-runner sandbox assets are unavailable: %v", kind, err)
		return processResult{Status: "policy_error", ExitCode: 126, Stdout: "", Stderr: "Решение отклонено системой безопасности.", CompileStderr: nil}
	}
	extraEnv = append(extraEnv, p.Env...)
	res := runCommandWithEnvContext(parent, p.Cmd, p.Args, p.Cwd, input, timeoutDuration(timeMs), extraEnv)
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
	data, err := io.ReadAll(io.LimitReader(r.Body, maxRequestBytes+1))
	if err != nil {
		return err
	}
	if len(data) > maxRequestBytes {
		return fmt.Errorf("request body is too large")
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	if err := dec.Decode(v); err != nil {
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

func taskforgeDebugLogsEnabled() bool {
	v := strings.ToLower(strings.TrimSpace(os.Getenv("TASKFORGE_DEBUG_LOGS")))
	return v == "1" || v == "true" || v == "yes" || v == "on" || v == "debug"
}

type taskforgeStatusWriter struct {
	http.ResponseWriter
	status int
	bytes  int
}

func (w *taskforgeStatusWriter) WriteHeader(code int) {
	if w.status != 0 {
		return
	}
	w.status = code
	w.ResponseWriter.WriteHeader(code)
}

func (w *taskforgeStatusWriter) Write(p []byte) (int, error) {
	if w.status == 0 {
		w.WriteHeader(http.StatusOK)
	}
	n, err := w.ResponseWriter.Write(p)
	w.bytes += n
	return n, err
}

func taskforgeDebugMiddleware(service string, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		traceID := r.Header.Get("X-TaskForge-Trace-Id")
		if traceID == "" {
			traceID = fmt.Sprintf("%s-%d", service, time.Now().UnixNano())
		}
		log.Printf("[TFDBG RUNNER IN START] trace=%s service=%s method=%s path=%s query=%s remote=%s content_length=%d content_type=%q", traceID, service, r.Method, r.URL.Path, r.URL.RawQuery, r.RemoteAddr, r.ContentLength, r.Header.Get("Content-Type"))
		sw := &taskforgeStatusWriter{ResponseWriter: w}
		next.ServeHTTP(sw, r)
		if sw.status == 0 {
			sw.status = http.StatusOK
		}
		log.Printf("[TFDBG RUNNER IN END] trace=%s service=%s method=%s path=%s status=%d duration=%s response_bytes=%d", traceID, service, r.Method, r.URL.Path, sw.status, time.Since(start), sw.bytes)
	})
}

func main() {
	kind := env("RUNNER_KIND", "cpp")
	port := env("PORT", "8080")
	if err := hardenRunnerProcess(); err != nil {
		log.Fatalf("%s-runner failed to protect its service process: %v", kind, err)
	}
	if err := policyAttestationReady(); err != nil {
		log.Fatalf("%s-runner code analyzer verification key is unavailable: %v", kind, err)
	}
	if err := pascalSandboxAssets(); err != nil {
		log.Fatalf("%s-runner sandbox assets are unavailable: %v", kind, err)
	}
	// One active submission per container prevents cross-submission /proc and /tmp interference.
	withJobSlot := func(w http.ResponseWriter, r *http.Request, action func()) {
		select {
		case runnerJobSlots <- struct{}{}:
			defer func() { <-runnerJobSlots }()
			action()
		case <-r.Context().Done():
			sendJSON(w, http.StatusRequestTimeout, map[string]any{"message": "request cancelled"})
		}
	}

	startInteractiveServer(kind)

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		sendJSON(w, http.StatusOK, map[string]any{"ok": true, "service": kind + "-runner", "runtime": "go", "go": runtime.Version()})
	})
	mux.HandleFunc("/ready", func(w http.ResponseWriter, r *http.Request) {
		if err := policyAttestationReady(); err != nil {
			sendJSON(w, http.StatusServiceUnavailable, map[string]any{"ok": false})
			return
		}
		if err := pascalSandboxAssets(); err != nil {
			sendJSON(w, http.StatusServiceUnavailable, map[string]any{"ok": false})
			return
		}
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
		if err := validateRunRequest(req.Code, req.Input); err != nil {
			sendJSON(w, http.StatusBadRequest, map[string]any{"message": err.Error()})
			return
		}
		if err := verifyPolicyAttestation(kind, "standard", req.Code, req.Attestation); err != nil {
			log.Printf("%s-runner rejected unattested source: %v", kind, err)
			sendJSON(w, http.StatusForbidden, map[string]any{"status": "policy_error", "message": "Решение не прошло обязательную проверку безопасности."})
			return
		}
		withJobSlot(w, r, func() {
			timeMs := boundedIntValue(req.TimeLimitMs, defaultTimeMs(kind), minTimeLimitMs, maxTimeLimitMs)
			memMb := boundedIntValue(req.MemoryLimitMb, defaultMemoryMb(kind), minMemoryLimitMb, maxMemoryLimitMb)
			dir, err := os.MkdirTemp("", "taskforge-"+kind+"-")
			if err != nil {
				sendJSON(w, http.StatusInternalServerError, map[string]any{"message": sanitizeRunnerText(err.Error())})
				return
			}
			defer os.RemoveAll(dir)
			program, compileErr := compileProgramContext(r.Context(), kind, req.Code, dir, timeMs, memMb)
			if compileErr != nil {
				sendJSON(w, http.StatusOK, sanitizeProcessResult(compileErr))
				return
			}
			sendJSON(w, http.StatusOK, executeProgramContext(r.Context(), kind, program, strValue(req.Input), timeMs))
		})
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
		if err := validateTestsRequest(&req); err != nil {
			sendJSON(w, http.StatusBadRequest, map[string]any{"message": err.Error()})
			return
		}
		if err := verifyPolicyAttestation(kind, "standard", req.Code, req.Attestation); err != nil {
			log.Printf("%s-runner rejected unattested source: %v", kind, err)
			sendJSON(w, http.StatusForbidden, map[string]any{"status": "policy_error", "message": "Решение не прошло обязательную проверку безопасности."})
			return
		}
		withJobSlot(w, r, func() {
			timeMs := boundedIntValue(req.TimeLimitMs, defaultTimeMs(kind), minTimeLimitMs, maxTimeLimitMs)
			memMb := boundedIntValue(req.MemoryLimitMb, defaultMemoryMb(kind), minMemoryLimitMb, maxMemoryLimitMb)
			dir, err := os.MkdirTemp("", "taskforge-"+kind+"-tests-")
			if err != nil {
				sendJSON(w, http.StatusInternalServerError, map[string]any{"message": sanitizeRunnerText(err.Error())})
				return
			}
			defer os.RemoveAll(dir)
			program, compileErr := compileProgramContext(r.Context(), kind, req.Code, dir, timeMs, memMb)
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
			batchContext, cancelBatch := context.WithTimeout(r.Context(), maxBatchDuration)
			defer cancelBatch()
			for _, t := range req.Tests {
				given := strValue(t.Input)
				expected := strValue(t.ExpectedOutput)
				if batchContext.Err() != nil {
					results = append(results, scrubHiddenResult(testResult{Input: given, ExpectedOutput: expected, ActualOutput: "", Passed: false, Status: "time_limit", ExitCode: 124, Stderr: "Batch time limit exceeded", CompileStderr: nil, Hidden: t.IsHidden}))
					break
				}
				run := executeProgramContext(batchContext, kind, program, given, timeMs)
				passed := run.ExitCode == 0 && strings.TrimRight(run.Stdout, "\r\n") == strings.TrimRight(expected, "\r\n")
				results = append(results, scrubHiddenResult(testResult{Input: given, ExpectedOutput: expected, ActualOutput: run.Stdout, Passed: passed, Status: run.Status, ExitCode: run.ExitCode, Stderr: run.Stderr, CompileStderr: run.CompileStderr, Hidden: t.IsHidden}))
				if batchContext.Err() != nil {
					break
				}
			}
			sendJSON(w, http.StatusOK, map[string]any{"results": results})
		})
	}
	mux.HandleFunc("/run/tests", testHandler)
	mux.HandleFunc("/run-tests", testHandler)

	handler := http.Handler(mux)
	if taskforgeDebugLogsEnabled() {
		handler = taskforgeDebugMiddleware(kind+"-runner", handler)
	}
	server := &http.Server{
		Addr:              ":" + port,
		Handler:           handler,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       20 * time.Second,
		WriteTimeout:      10 * time.Minute,
		IdleTimeout:       30 * time.Second,
		MaxHeaderBytes:    32 << 10,
	}
	log.Printf("%s-runner listening on :%s", kind, port)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}

func _unused() {
	_, _ = strconv.Atoi("0")
}
