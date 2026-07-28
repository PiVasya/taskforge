package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestPreparePascalSourceAddsGuardToUses(t *testing.T) {
	source := "program Demo;\nuses SysUtils;\nbegin\n  Writeln('ok');\nend."
	prepared, err := preparePascalSource(source)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(prepared, "uses TaskForgeSandboxGuard, SysUtils") {
		t.Fatalf("guard unit was not injected: %s", prepared)
	}
}

func TestPreparePascalSourceAddsUsesClause(t *testing.T) {
	source := "{$mode objfpc}\nprogram Demo;\nbegin\nend."
	prepared, err := preparePascalSource(source)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(prepared, "uses TaskForgeSandboxGuard;") {
		t.Fatalf("guard unit was not injected: %s", prepared)
	}
}

func TestPreparePascalSourceRejectsConditionalDirectives(t *testing.T) {
	_, err := preparePascalSource("program Demo; {$IFDEF X} begin end. {$ENDIF}")
	if err == nil {
		t.Fatal("expected conditional directive to be rejected")
	}
}

func TestPreparePascalSourceRejectsExternalFileAndMacroDirectives(t *testing.T) {
	blocked := []string{
		"program Demo; {$I payload.inc} begin end.",
		"program Demo; {$INCLUDE payload.inc} begin end.",
		"program Demo; {$L payload.o} begin end.",
		"program Demo; {$R payload.res} begin end.",
		"{$MACRO ON}{$DEFINE TaskForgeSandboxGuard:=SysUtils} program Demo; begin end.",
	}
	for _, source := range blocked {
		if _, err := preparePascalSource(source); err == nil {
			t.Fatalf("expected directive to be rejected: %s", source)
		}
	}
}

func TestPreparePascalSourceAllowsRuntimeCheckDirectivesAndDirectiveTextInStrings(t *testing.T) {
	source := "{$I-}{$R+} program Demo; begin Writeln('{$I payload.inc}'); end."
	prepared, err := preparePascalSource(source)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(prepared, "uses TaskForgeSandboxGuard;") {
		t.Fatalf("guard unit was not injected: %s", prepared)
	}
}

func TestPreparePascalSourceRejectsUnit(t *testing.T) {
	_, err := preparePascalSource("unit Evil; interface implementation end.")
	if err == nil {
		t.Fatal("expected unit source to be rejected")
	}
}

func TestPascalSandboxGuardBlocksDangerousSyscalls(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("seccomp test requires Linux")
	}
	for _, tool := range []string{"gcc", "g++"} {
		if _, err := exec.LookPath(tool); err != nil {
			t.Skip(tool + " is not available")
		}
	}
	dir := t.TempDir()
	guard := filepath.Join(dir, "taskforge_pascal_sandbox_guard.o")
	compileGuard := exec.Command(
		"gcc", "-c", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror",
		"-fvisibility=hidden", "-fno-stack-protector", "-fno-builtin",
		"-o", guard, "security/sandbox_guard.c",
	)
	if output, err := compileGuard.CombinedOutput(); err != nil {
		t.Fatalf("failed to compile Pascal sandbox guard: %v\n%s", err, output)
	}

	source := filepath.Join(dir, "guard_test.cpp")
	binary := filepath.Join(dir, "guard_test")
	code := `
#include <cerrno>
#include <iostream>
#include <sys/mman.h>
#include <sys/socket.h>
#include <thread>
#include <unistd.h>
int main() {
    int value = 0;
    std::thread worker([&]() { value = 1; });
    worker.join();
    if (value != 1) return 2;

    errno = 0;
    if (fork() != -1 || errno != EPERM) return 3;

    errno = 0;
    if (socket(AF_UNIX, SOCK_STREAM, 0) != -1 || errno != EPERM) return 4;

    errno = 0;
    void* memory = mmap(nullptr, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                        MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (memory != MAP_FAILED || errno != EPERM) return 5;

    std::cout << "blocked";
    return 0;
}
`
	if err := os.WriteFile(source, []byte(code), 0o600); err != nil {
		t.Fatal(err)
	}
	link := exec.Command(
		"g++", source, guard, "-pthread",
		"-Wl,-init,taskforge_pascal_sandbox_init,-z,relro,-z,now,-z,noexecstack",
		"-o", binary,
	)
	if output, err := link.CombinedOutput(); err != nil {
		t.Fatalf("failed to link Pascal sandbox fixture: %v\n%s", err, output)
	}
	output, err := exec.Command(binary).CombinedOutput()
	if err != nil {
		t.Fatalf("Pascal sandbox fixture failed: %v\n%s", err, output)
	}
	if strings.TrimSpace(string(output)) != "blocked" {
		t.Fatalf("unexpected Pascal sandbox result: %q", output)
	}
}
