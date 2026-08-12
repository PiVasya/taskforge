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

func normalizeOutputForComparison(value string) string {
	value = strings.ReplaceAll(value, "\r\n", "\n")
	value = strings.ReplaceAll(value, "\r", "\n")
	lines := strings.Split(value, "\n")
	for i := range lines {
		// A trailing ASCII space at the end of an output line is intentionally
		// ignored. Leading spaces and whitespace inside the line stay significant.
		lines[i] = strings.TrimRight(lines[i], " ")
	}
	return strings.TrimRight(strings.Join(lines, "\n"), "\n")
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

var forbiddenCppExternalSymbols = map[string]struct{}{
	"system": {}, "__libc_system": {}, "popen": {}, "pclose": {}, "wordexp": {},
	"fork": {}, "vfork": {}, "clone": {}, "clone3": {},
	"execl": {}, "execlp": {}, "execle": {}, "execv": {}, "execvp": {},
	"execvpe": {}, "execve": {}, "execveat": {}, "fexecve": {},
	"posix_spawn": {}, "posix_spawnp": {},
	"dlopen": {}, "dlmopen": {}, "dlsym": {}, "dlvsym": {},
	"syscall": {}, "prctl": {}, "seccomp": {}, "ptrace": {}, "unshare": {}, "setns": {},
	"mount": {}, "umount": {}, "umount2": {}, "chroot": {}, "pivot_root": {},
	"socket": {}, "socketpair": {}, "connect": {}, "bind": {}, "listen": {},
	"accept": {}, "accept4": {}, "send": {}, "sendto": {}, "sendmsg": {}, "sendmmsg": {},
	"recv": {}, "recvfrom": {}, "recvmsg": {}, "recvmmsg": {}, "shutdown": {},
	"getaddrinfo": {}, "gethostbyname": {}, "gethostbyname2": {},
	"kill": {}, "tkill": {}, "tgkill": {}, "pidfd_open": {}, "pidfd_getfd": {}, "pidfd_send_signal": {},
	"process_vm_readv": {}, "process_vm_writev": {}, "process_madvise": {}, "process_mrelease": {}, "kcmp": {},
	"getenv": {}, "secure_getenv": {}, "setenv": {}, "putenv": {}, "unsetenv": {},
	"open": {}, "open64": {}, "openat": {}, "openat64": {}, "creat": {}, "creat64": {},

	"read": {}, "pread": {}, "pread64": {}, "readv": {}, "preadv": {}, "preadv2": {},
	"write": {}, "pwrite": {}, "pwrite64": {}, "writev": {}, "pwritev": {}, "pwritev2": {},
	"close": {}, "dup": {}, "dup2": {}, "dup3": {}, "fcntl": {}, "ioctl": {},
	"stat": {}, "stat64": {}, "lstat": {}, "lstat64": {}, "fstat": {}, "fstat64": {}, "fstatat": {}, "statx": {},
	"access": {}, "faccessat": {}, "faccessat2": {}, "getcwd": {}, "chdir": {}, "fchdir": {},
	"unlink": {}, "unlinkat": {}, "remove": {}, "rename": {}, "renameat": {}, "renameat2": {},
	"mkdir": {}, "mkdirat": {}, "rmdir": {}, "link": {}, "linkat": {}, "symlink": {}, "symlinkat": {},
	"chmod": {}, "fchmod": {}, "fchmodat": {}, "chown": {}, "fchown": {}, "fchownat": {}, "lchown": {},
	"truncate": {}, "truncate64": {}, "ftruncate": {}, "ftruncate64": {},
	"fopen": {}, "fopen64": {}, "freopen": {}, "freopen64": {}, "tmpfile": {}, "tmpfile64": {}, "tmpnam": {},
	"opendir": {}, "fdopendir": {}, "readdir": {}, "readdir64": {}, "scandir": {}, "scandir64": {},
	"ftw": {}, "ftw64": {}, "nftw": {}, "nftw64": {}, "glob": {}, "glob64": {},
	"readlink": {}, "readlinkat": {}, "realpath": {}, "open_by_handle_at": {}, "name_to_handle_at": {},
	"mmap": {}, "mmap64": {}, "mprotect": {}, "pkey_mprotect": {}, "memfd_create": {}, "userfaultfd": {},
	"bpf": {}, "perf_event_open": {}, "fanotify_init": {},
	"keyctl": {}, "add_key": {}, "request_key": {},
	"io_uring_setup": {}, "io_uring_enter": {}, "io_uring_register": {},
	"init_module": {}, "finit_module": {}, "delete_module": {},
	"kexec_load": {}, "kexec_file_load": {}, "reboot": {}, "swapon": {}, "swapoff": {},
	"acct": {}, "iopl": {}, "ioperm": {}, "quotactl": {}, "quotactl_fd": {},
}

func normalizeELFSymbol(name string) string {
	name = strings.TrimSpace(name)
	if i := strings.IndexByte(name, '@'); i >= 0 {
		name = name[:i]
	}
	return strings.TrimPrefix(name, "__GI_")
}

func isForbiddenCppExternalSymbol(name string) bool {
	if _, forbidden := forbiddenCppExternalSymbols[name]; forbidden {
		return true
	}
	for _, prefix := range []string{
		"__open", "__read", "__write", "__pread", "__pwrite", "__xstat", "__fxstat", "__lxstat",
		"__libc_open", "__libc_read", "__libc_write", "__libc_system", "__GI_open", "__GI_read", "__GI_write",
	} {
		if strings.HasPrefix(name, prefix) {
			return true
		}
	}
	return false
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
		if isForbiddenCppExternalSymbol(name) {
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
	file, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	blocked := make(map[string]struct{})
	for _, section := range file.Sections {
		if section.Flags&elf.SHF_EXECINSTR == 0 || section.Size == 0 {
			continue
		}
		data, readErr := section.Data()
		if readErr != nil {
			return nil, readErr
		}
		switch file.Machine {
		case elf.EM_X86_64:
			for _, pattern := range []struct {
				name  string
				bytes []byte
			}{
				{name: "machine.syscall", bytes: []byte{0x0f, 0x05}},
				{name: "machine.sysenter", bytes: []byte{0x0f, 0x34}},
				{name: "machine.int80", bytes: []byte{0xcd, 0x80}},
			} {
				if bytes.Contains(data, pattern.bytes) {
					blocked[pattern.name] = struct{}{}
				}
			}
		case elf.EM_AARCH64:
			for offset := 0; offset+4 <= len(data); offset += 4 {
				instruction := file.ByteOrder.Uint32(data[offset : offset+4])
				if instruction&0xffe0001f == 0xd4000001 {
					blocked["machine.svc"] = struct{}{}
					break
				}
			}
		default:
			blocked["elf.unsupported_machine"] = struct{}{}
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
	file, err := elf.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	blocked := make(map[string]struct{})
	for _, section := range file.Sections {
		if section.Size == 0 {
			continue
		}
		if strings.HasPrefix(section.Name, ".preinit_array") {
			blocked["elf.preinit_array"] = struct{}{}
		}
		if (section.Name == ".init" || strings.HasPrefix(section.Name, ".init.")) && section.Flags&elf.SHF_EXECINSTR != 0 {
			blocked["elf.init_section"] = struct{}{}
		}
		if section.Name == ".interp" || section.Name == ".dynamic" || section.Name == ".dynsym" || section.Name == ".dynstr" {
			blocked["elf.loader_section"] = struct{}{}
		}
	}
	if symbols, symbolErr := file.Symbols(); symbolErr == nil {
		for _, symbol := range symbols {
			if elf.ST_TYPE(symbol.Info) == elf.STT_GNU_IFUNC {
				blocked["elf.ifunc"] = struct{}{}
			}
		}
	} else if !errors.Is(symbolErr, elf.ErrNoSymbols) {
		return nil, symbolErr
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
	return compileProgramContext(context.Background(), kind, code, cwd, timeMs, memMb)
}

func compileProgramContext(parent context.Context, kind, code, cwd string, timeMs, memMb int) (*preparedProgram, *processResult) {
	switch kind {
	case "cpp":
		src := filepath.Join(cwd, "main.cpp")
		obj := filepath.Join(cwd, "main.o")
		bin := filepath.Join(cwd, "a.out")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{Status: "runtime_error", ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}

		compile := runCommandContext(parent, "g++", []string{"-std=c++17", "-O2", "-pipe", "-fno-asm", "-fPIE", "-fstack-protector-strong", "-fstack-clash-protection", "-D_FORTIFY_SOURCE=2", "-c", "main.cpp", "-o", "main.o"}, cwd, "", timeoutDuration(timeMs))
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
		link := runCommandContext(parent, "g++", []string{"main.o", guardObject, "-pie", "-static-libgcc", "-static-libstdc++", "-Wl,-init,taskforge_sandbox_init,-z,relro,-z,now,-z,noexecstack,-z,defs,--as-needed,--fatal-warnings", "-o", "a.out"}, cwd, "", timeoutDuration(timeMs))
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
		src := filepath.Join(cwd, "main.pas")
		bin := filepath.Join(cwd, "main")
		if err := os.WriteFile(src, []byte(code), 0o600); err != nil {
			return nil, &processResult{ExitCode: 1, Stderr: sanitizeRunnerText(err.Error()), CompileStderr: nil}
		}
		res := runCommandContext(parent, "fpc", []string{"main.pas", "-O2", "-vw", "-omain"}, cwd, "", timeoutDuration(timeMs))
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
	return executeProgramContext(context.Background(), kind, p, input, timeMs)
}

func executeProgramContext(parent context.Context, kind string, p *preparedProgram, input string, timeMs int) processResult {
	res := runCommandWithEnvContext(parent, p.Cmd, p.Args, p.Cwd, input, timeoutDuration(timeMs), p.Env)
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
	if _, err := cppSandboxGuardObject(); err != nil {
		log.Fatalf("%s-runner sandbox guard is unavailable: %v", kind, err)
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
		if _, err := cppSandboxGuardObject(); err != nil {
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
				passed := run.ExitCode == 0 && normalizeOutputForComparison(run.Stdout) == normalizeOutputForComparison(expected)
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
