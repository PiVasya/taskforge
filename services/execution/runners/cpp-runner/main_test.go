package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"testing"
)

func requireGxx(t *testing.T) {
	t.Helper()
	if _, err := exec.LookPath("g++"); err != nil {
		t.Skip("g++ is not available")
	}
}

func prepareSandboxGuard(t *testing.T) string {
	t.Helper()
	requireGxx(t)
	guard := filepath.Join(t.TempDir(), "taskforge_cpp_sandbox_guard.o")
	cmd := exec.Command("gcc", "-O2", "-fPIC", "-Wall", "-Wextra", "-Werror", "-c", "security/sandbox_guard.c", "-o", guard)
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to compile sandbox guard: %v\n%s", err, output)
	}
	t.Setenv("TASKFORGE_CPP_SANDBOX_GUARD", guard)
	return guard
}

func TestCompileProgramRejectsTokenPasteSystemCall(t *testing.T) {
	prepareSandboxGuard(t)
	dir := t.TempDir()
	code := `
#include <cstdlib>
#define RUN(a, b) a##b
int main() {
    return RUN(sys, tem)("id");
}
`
	program, result := compileProgram("cpp", code, dir, 3000, 256)
	if program != nil {
		t.Fatalf("forbidden program was prepared: %#v", program)
	}
	if result == nil || result.Status != "policy_error" {
		t.Fatalf("expected policy_error, got %#v", result)
	}
}

func TestCompileProgramRejectsLibcStartupInterposition(t *testing.T) {
	prepareSandboxGuard(t)
	dir := t.TempDir()
	code := `
extern "C" int __libc_start_main() { return 0; }
int main() { return 0; }
`
	program, result := compileProgram("cpp", code, dir, 3000, 256)
	if program != nil {
		t.Fatalf("startup-interposition program was prepared: %#v", program)
	}
	if result == nil || result.Status != "policy_error" {
		t.Fatalf("expected policy_error, got %#v", result)
	}
}

func TestForbiddenELFRelocationsRejectsIRelative(t *testing.T) {
	if runtime.GOARCH != "amd64" {
		t.Skip("x86-64 relocation fixture")
	}
	if _, err := exec.LookPath("gcc"); err != nil {
		t.Skip("gcc is not available")
	}
	dir := t.TempDir()
	source := filepath.Join(dir, "irelative.s")
	object := filepath.Join(dir, "irelative.o")
	assembly := `
.text
.globl resolver
.type resolver,@function
resolver:
  ret
.data
.globl target
target:
  .quad 0
  .reloc target, R_X86_64_IRELATIVE, resolver
`
	if err := os.WriteFile(source, []byte(assembly), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command("gcc", "-c", source, "-o", object)
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to build relocation fixture: %v\n%s", err, output)
	}
	blocked, err := forbiddenELFRelocations(object)
	if err != nil {
		t.Fatal(err)
	}
	if len(blocked) != 1 || blocked[0] != "elf.irelative" {
		t.Fatalf("expected IRELATIVE rejection, got %#v", blocked)
	}
}

func TestObjectInspectionRejectsDirectFileSyscalls(t *testing.T) {
	requireGxx(t)
	dir := t.TempDir()
	source := filepath.Join(dir, "file_api.cpp")
	object := filepath.Join(dir, "file_api.o")
	code := `
extern "C" int open(const char*, int, ...);
extern "C" long read(int, void*, unsigned long);
int main() {
    char byte = 0;
    const int fd = open("/etc/passwd", 0);
    return fd < 0 ? 0 : int(read(fd, &byte, 1));
}
`
	if err := os.WriteFile(source, []byte(code), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command("g++", "-std=c++17", "-O0", "-c", source, "-o", object)
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to build direct file API fixture: %v\n%s", err, output)
	}
	blocked, err := forbiddenUndefinedELFSymbols(object)
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Contains(blocked, "open") || !slices.Contains(blocked, "read") {
		t.Fatalf("expected open/read rejection, got %#v", blocked)
	}
}

func TestCompileProgramRejectsInlineSyscallInstruction(t *testing.T) {
	prepareSandboxGuard(t)
	dir := t.TempDir()
	code := `
int main() {
    __asm__ volatile("syscall");
    return 0;
}
`
	program, result := compileProgram("cpp", code, dir, 3000, 256)
	if program != nil {
		t.Fatalf("inline-syscall program was prepared: %#v", program)
	}
	if result == nil || result.Status != "policy_error" {
		t.Fatalf("expected policy_error, got %#v", result)
	}
}

func TestCompileProgramAllowsHarmlessCpp(t *testing.T) {
	prepareSandboxGuard(t)
	dir := t.TempDir()
	code := `
#include <iostream>
int main() {
    std::cout << "ok";
    return 0;
}
`
	program, result := compileProgram("cpp", code, dir, 3000, 256)
	if result != nil {
		t.Fatalf("harmless program was rejected: %#v", result)
	}
	if program == nil {
		t.Fatal("harmless program was not prepared")
	}
	if _, err := os.Stat(program.Cwd + "/a.out"); err != nil {
		t.Fatalf("compiled binary is missing: %v", err)
	}
	run := executeProgram("cpp", program, "", 3000)
	if run.Status != "ok" || run.ExitCode != 0 || run.Stdout != "ok" {
		t.Fatalf("guarded harmless program failed: %#v", run)
	}
}

func TestSandboxGuardCannotBeBypassedByPrctlInterposition(t *testing.T) {
	guard := prepareSandboxGuard(t)
	dir := t.TempDir()
	source := filepath.Join(dir, "prctl_interposition.cpp")
	binary := filepath.Join(dir, "prctl_interposition")
	code := `
#include <cerrno>
#include <iostream>
#include <unistd.h>
extern "C" int prctl(int, ...) { return 0; }
int main() {
    errno = 0;
    const pid_t child = fork();
    if (child == -1 && errno == EPERM) {
        std::cout << "blocked";
        return 0;
    }
    return 2;
}
`
	if err := os.WriteFile(source, []byte(code), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command("g++", source, guard, "-Wl,-init,taskforge_sandbox_init,-z,relro,-z,now,-z,noexecstack", "-o", binary)
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to link interposition test: %v\n%s", err, output)
	}
	output, err := exec.Command(binary).CombinedOutput()
	if err != nil {
		t.Fatalf("interposition test failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "blocked" {
		t.Fatalf("sandbox guard was interposed, output=%q", output)
	}
}

func TestSandboxGuardBlocksForkAtRuntime(t *testing.T) {
	guard := prepareSandboxGuard(t)
	dir := t.TempDir()
	source := filepath.Join(dir, "fork_test.cpp")
	binary := filepath.Join(dir, "fork_test")
	code := `
#include <cerrno>
#include <iostream>
#include <unistd.h>
int main() {
    errno = 0;
    const pid_t child = fork();
    if (child == -1 && errno == EPERM) {
        std::cout << "blocked";
        return 0;
    }
    return 2;
}
`
	if err := os.WriteFile(source, []byte(code), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command("g++", source, guard, "-Wl,-init,taskforge_sandbox_init,-z,relro,-z,now,-z,noexecstack", "-o", binary)
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to link guard test: %v\n%s", err, output)
	}
	output, err := exec.Command(binary).CombinedOutput()
	if err != nil {
		t.Fatalf("guarded fork test failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "blocked" {
		t.Fatalf("fork was not blocked, output=%q", output)
	}
}

func TestSandboxGuardBlocksNetworkAndExecutableMemoryButAllowsThreads(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("seccomp test requires Linux")
	}
	guard := prepareSandboxGuard(t)
	dir := t.TempDir()
	source := filepath.Join(dir, "sandbox_surface_test.cpp")
	binary := filepath.Join(dir, "sandbox_surface_test")
	code := `
#include <cerrno>
#include <iostream>
#include <sys/mman.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <thread>
int main() {
    int thread_value = 0;
    std::thread worker([&]() { thread_value = 7; });
    worker.join();
    if (thread_value != 7) return 2;

    errno = 0;
    const int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd != -1 || errno != EPERM) return 3;

    errno = 0;
    void* memory = mmap(nullptr, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                        MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (memory != MAP_FAILED || errno != EPERM) return 4;

    struct rlimit limit{};
    if (getrlimit(RLIMIT_CORE, &limit) != 0 || limit.rlim_cur != 0 || limit.rlim_max != 0) return 5;
    if (getrlimit(RLIMIT_CPU, &limit) != 0 || limit.rlim_cur != 40 || limit.rlim_max != 40) return 6;
    if (getrlimit(RLIMIT_FSIZE, &limit) != 0 || limit.rlim_cur != 16ULL * 1024ULL * 1024ULL || limit.rlim_max != limit.rlim_cur) return 7;
    if (getrlimit(RLIMIT_NOFILE, &limit) != 0 || limit.rlim_cur != 128 || limit.rlim_max != 128) return 8;

    std::cout << "blocked";
    return 0;
}
`
	if err := os.WriteFile(source, []byte(code), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command(
		"g++", source, guard, "-pthread",
		"-Wl,-init,taskforge_sandbox_init,-z,relro,-z,now,-z,noexecstack",
		"-o", binary,
	)
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to link sandbox surface test: %v\n%s", err, output)
	}
	output, err := exec.Command(binary).CombinedOutput()
	if err != nil {
		t.Fatalf("sandbox surface test failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "blocked" {
		t.Fatalf("unexpected sandbox surface result: %q", output)
	}
}
