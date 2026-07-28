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
	"sort"
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

var forbiddenCppExternalSymbols = map[string]struct{}{
	"system": {}, "__libc_system": {}, "popen": {}, "pclose": {},
	"fork": {}, "vfork": {}, "clone": {}, "clone3": {},
	"execl": {}, "execlp": {}, "execle": {}, "execv": {}, "execvp": {},
	"execvpe": {}, "execve": {}, "execveat": {}, "fexecve": {},
	"posix_spawn": {}, "posix_spawnp": {},
	"dlopen": {}, "dlmopen": {}, "dlsym": {}, "dlvsym": {},
	"syscall": {}, "prctl": {}, "seccomp": {}, "ptrace": {}, "unshare": {}, "setns": {},
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

var forbiddenCppDefinedSymbols = map[string]struct{}{
	"__libc_start_main": {}, "__libc_start_call_main": {},
	"_start": {}, "_init": {}, "_fini": {},
	"taskforge_sandbox_init": {},
}

func isForbiddenCppDefinedSymbol(name string) bool {
	if _, forbidden := forbiddenCppDefinedSymbols[name]; forbidden {
		return true
	}
	return strings.HasPrefix(name, "_dl_") ||
		strings.HasPrefix(name, "__libc_start_") ||
		strings.HasPrefix(name, "taskforge_sandbox_")
}

func forbiddenDefinedELFSymbols(path string) ([]string, error) {
	f, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	symbols, symErr := f.Symbols()
	if symErr != nil {
		if errors.Is(symErr, elf.ErrNoSymbols) {
			return nil, nil
		}
		return nil, symErr
	}

	blocked := make(map[string]struct{})
	for _, symbol := range symbols {
		if symbol.Section == elf.SHN_UNDEF {
			continue
		}
		binding := elf.ST_BIND(symbol.Info)
		if binding != elf.STB_GLOBAL && binding != elf.STB_WEAK {
			continue
		}
		name := normalizeELFSymbol(symbol.Name)
		if isForbiddenCppDefinedSymbol(name) {
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

func forbiddenELFMetadata(path string) ([]string, error) {
	f, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	blocked := make(map[string]struct{})
	if section := f.Section(".preinit_array"); section != nil && section.Size > 0 {
		blocked["elf.preinit_array"] = struct{}{}
	}
	if symbols, symErr := f.Symbols(); symErr == nil {
		for _, symbol := range symbols {
			if elf.ST_TYPE(symbol.Info) == elf.STT_GNU_IFUNC {
				blocked["elf.ifunc"] = struct{}{}
			}
		}
	} else if !errors.Is(symErr, elf.ErrNoSymbols) {
		return nil, symErr
	}

	result := make([]string, 0, len(blocked))
	for name := range blocked {
		result = append(result, name)
	}
	sort.Strings(result)
	return result, nil
}

func forbiddenELFRelocations(path string) ([]string, error) {
	f, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	if f.Class != elf.ELFCLASS64 {
		return []string{"elf.unsupported_class"}, nil
	}

	var forbiddenType uint32
	switch f.Machine {
	case elf.EM_X86_64:
		forbiddenType = uint32(elf.R_X86_64_IRELATIVE)
	case elf.EM_AARCH64:
		forbiddenType = uint32(elf.R_AARCH64_IRELATIVE)
	default:
		return []string{"elf.unsupported_machine"}, nil
	}

	for _, section := range f.Sections {
		if section.Type != elf.SHT_RELA && section.Type != elf.SHT_REL {
			continue
		}
		data, readErr := section.Data()
		if readErr != nil {
			return nil, readErr
		}
		entrySize := int(section.Entsize)
		if entrySize == 0 {
			if section.Type == elf.SHT_RELA {
				entrySize = 24
			} else {
				entrySize = 16
			}
		}
		if entrySize < 16 {
			return nil, fmt.Errorf("invalid relocation entry size %d", entrySize)
		}
		for offset := 0; offset+entrySize <= len(data); offset += entrySize {
			info := f.ByteOrder.Uint64(data[offset+8 : offset+16])
			if uint32(info) == forbiddenType {
				return []string{"elf.irelative"}, nil
			}
		}
	}
	return nil, nil
}

func cppSandboxGuardObject() (string, error) {
	path := strings.TrimSpace(os.Getenv("TASKFORGE_CPP_SANDBOX_GUARD"))
	if path == "" {
		path = "/app/taskforge_cpp_sandbox_guard.o"
	}
	info, err := os.Stat(path)
	if err != nil {
		return "", err
	}
	if !info.Mode().IsRegular() {
		return "", fmt.Errorf("sandbox guard is not a regular file")
	}
	return path, nil
}

func cppSecurityPolicyResult(blocked []string) *processResult {
	if len(blocked) > 0 {
		log.Printf("cpp-runner blocked security-policy markers: %s", strings.Join(blocked, ","))
	}
	return &processResult{
		Status:        "policy_error",
		ExitCode:      126,
		Stdout:        "",
		Stderr:        "Решение отклонено системой безопасности.",
		CompileStderr: nil,
	}
}

func compileProgram(kind, code, cwd string, timeMs, memMb int) (*preparedProgram, *processResult) {
	switch kind {
	case "cpp":
		src := filepath.Join(cwd, "main.cpp")
		obj := filepath.Join(cwd, "main.o")
		bin := filepath.Join(cwd, "a.out")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}

		compile := runCommand("g++", []string{"-std=c++17", "-O2", "-pipe", "-fno-asm", "-c", "main.cpp", "-o", "main.o"}, cwd, "", timeoutDuration(timeMs))
		if compile.ExitCode != 0 {
			msg := strings.TrimSpace(compile.Stdout + compile.Stderr)
			if msg == "" {
				msg = "Compilation error"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: compile.ExitCode, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}

		blockedDefinitions, definitionScanErr := forbiddenDefinedELFSymbols(obj)
		if definitionScanErr != nil {
			log.Printf("cpp-runner failed to inspect defined object symbols: %v", definitionScanErr)
			return nil, cppSecurityPolicyResult(nil)
		}
		if len(blockedDefinitions) > 0 {
			return nil, cppSecurityPolicyResult(blockedDefinitions)
		}

		blocked, scanErr := forbiddenUndefinedELFSymbols(obj)
		if scanErr != nil {
			log.Printf("cpp-runner failed to inspect compiled object: %v", scanErr)
			return nil, cppSecurityPolicyResult(nil)
		}
		if len(blocked) > 0 {
			return nil, cppSecurityPolicyResult(blocked)
		}
		blockedInstructions, instructionScanErr := forbiddenExecutableInstructions(obj)
		if instructionScanErr != nil {
			log.Printf("cpp-runner failed to inspect executable instructions: %v", instructionScanErr)
			return nil, cppSecurityPolicyResult(nil)
		}
		if len(blockedInstructions) > 0 {
			return nil, cppSecurityPolicyResult(blockedInstructions)
		}
		blockedMetadata, metadataScanErr := forbiddenELFMetadata(obj)
		if metadataScanErr != nil {
			log.Printf("cpp-runner failed to inspect ELF metadata: %v", metadataScanErr)
			return nil, cppSecurityPolicyResult(nil)
		}
		if len(blockedMetadata) > 0 {
			return nil, cppSecurityPolicyResult(blockedMetadata)
		}
		blockedRelocations, relocationScanErr := forbiddenELFRelocations(obj)
		if relocationScanErr != nil {
			log.Printf("cpp-runner failed to inspect ELF relocations: %v", relocationScanErr)
			return nil, cppSecurityPolicyResult(nil)
		}
		if len(blockedRelocations) > 0 {
			return nil, cppSecurityPolicyResult(blockedRelocations)
		}

		guardObject, guardErr := cppSandboxGuardObject()
		if guardErr != nil {
			log.Printf("cpp-runner sandbox guard is unavailable: %v", guardErr)
			return nil, cppSecurityPolicyResult(nil)
		}
		link := runCommand("g++", []string{"main.o", guardObject, "-static-libgcc", "-static-libstdc++", "-Wl,-init,taskforge_sandbox_init,-z,relro,-z,now,-z,noexecstack", "-o", "a.out"}, cwd, "", timeoutDuration(timeMs))
		if link.ExitCode != 0 {
			msg := strings.TrimSpace(link.Stdout + link.Stderr)
			if msg == "" {
				msg = "Linking error"
			}
			msg = sanitizeRunnerText(msg)
			return nil, &processResult{Status: "compile_error", ExitCode: link.ExitCode, Stdout: "", Stderr: "", CompileStderr: ptr(msg + "\n")}
		}

		if err := os.Chmod(bin, 0o500); err != nil {
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
