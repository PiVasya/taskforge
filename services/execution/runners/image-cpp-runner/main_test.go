package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func requireImageCppToolchain(t *testing.T) {
	t.Helper()
	for _, tool := range []string{"gcc", "g++"} {
		if _, err := exec.LookPath(tool); err != nil {
			t.Skip(tool + " is not available")
		}
	}
}

func buildImageCppGuard(t *testing.T) string {
	t.Helper()
	requireImageCppToolchain(t)
	output := filepath.Join(t.TempDir(), "taskforge_image_cpp_sandbox_guard.o")
	cmd := exec.Command(
		"gcc", "-c", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror",
		"-fvisibility=hidden", "-fno-stack-protector", "-fno-builtin",
		"-o", output, "security/sandbox_guard.c",
	)
	if combined, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to compile image C++ sandbox guard: %v\n%s", err, combined)
	}
	t.Setenv("TASKFORGE_IMAGE_CPP_SANDBOX_GUARD", output)
	return output
}

func compileImageCppObject(t *testing.T, source string) string {
	t.Helper()
	requireImageCppToolchain(t)
	dir := t.TempDir()
	sourcePath := filepath.Join(dir, "main.cpp")
	objectPath := filepath.Join(dir, "main.o")
	if err := os.WriteFile(sourcePath, []byte(source), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command(
		"g++", "-std=c++17", "-O2", "-fno-asm", "-fPIE",
		"-fstack-protector-strong", "-fstack-clash-protection", "-D_FORTIFY_SOURCE=2",
		"-c", sourcePath, "-o", objectPath,
	)
	if combined, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to compile fixture: %v\n%s", err, combined)
	}
	return objectPath
}

func TestImageCppObjectInspectionRejectsTokenPasteSystemCall(t *testing.T) {
	object := compileImageCppObject(t, `
#include <cstdlib>
#define RUN(a, b) a##b
int main() { return RUN(sys, tem)("id"); }
`)
	markers, err := forbiddenUndefinedELFSymbols(object)
	if err != nil {
		t.Fatal(err)
	}
	if !containsString(markers, "system") {
		t.Fatalf("expected system marker, got %#v", markers)
	}
}

func TestImageCppObjectInspectionRejectsDirectFileSyscalls(t *testing.T) {
	object := compileImageCppObject(t, `
extern "C" int open(const char*, int, ...);
extern "C" long read(int, void*, unsigned long);
int main() {
    char byte = 0;
    const int fd = open("/etc/passwd", 0);
    return fd < 0 ? 0 : int(read(fd, &byte, 1));
}
`)
	markers, err := forbiddenUndefinedELFSymbols(object)
	if err != nil {
		t.Fatal(err)
	}
	if !containsString(markers, "open") || !containsString(markers, "read") {
		t.Fatalf("expected open/read markers, got %#v", markers)
	}
}

func TestImageCppObjectInspectionRejectsInlineSyscall(t *testing.T) {
	object := compileImageCppObject(t, `
int main() {
    __asm__ volatile("syscall");
    return 0;
}
`)
	markers, err := forbiddenExecutableInstructions(object)
	if err != nil {
		t.Fatal(err)
	}
	if !containsString(markers, "machine.syscall") {
		t.Fatalf("expected syscall marker, got %#v", markers)
	}
}

func TestImageCppObjectInspectionRejectsInitSection(t *testing.T) {
	object := compileImageCppObject(t, `
__attribute__((section(".init"))) void before_main() {}
int main() { return 0; }
`)
	markers, err := forbiddenELFMetadata(object)
	if err != nil {
		t.Fatal(err)
	}
	if !containsString(markers, "elf.init_section") {
		t.Fatalf("expected init-section marker, got %#v", markers)
	}
}

func TestImageCppObjectInspectionAllowsHarmlessCode(t *testing.T) {
	object := compileImageCppObject(t, `
#include <iostream>
int main() { std::cout << "ok"; return 0; }
`)
	checks := []func(string) ([]string, error){
		forbiddenDefinedELFSymbols,
		forbiddenUndefinedELFSymbols,
		forbiddenExecutableInstructions,
		forbiddenELFMetadata,
		forbiddenELFRelocations,
	}
	for _, check := range checks {
		markers, err := check(object)
		if err != nil {
			t.Fatal(err)
		}
		if len(markers) != 0 {
			t.Fatalf("harmless object was rejected: %#v", markers)
		}
	}
}

func TestImageCppSandboxGuardBlocksForkAndInternet(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("seccomp test requires Linux")
	}
	guard := buildImageCppGuard(t)
	dir := t.TempDir()
	source := filepath.Join(dir, "guard_test.cpp")
	binary := filepath.Join(dir, "guard_test")
	code := `
#include <cerrno>
#include <iostream>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
int main() {
    errno = 0;
    const pid_t child = fork();
    if (child != -1 || errno != EPERM) return 2;

    const int unix_socket = socket(AF_UNIX, SOCK_STREAM, 0);
    if (unix_socket < 0) return 3;
    close(unix_socket);

    errno = 0;
    const int internet_socket = socket(AF_INET, SOCK_STREAM, 0);
    if (internet_socket != -1 || errno != EPERM) return 4;

    std::cout << "blocked";
    return 0;
}
`
	if err := os.WriteFile(source, []byte(code), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command(
		"g++", source, guard,
		"-Wl,-init,taskforge_image_sandbox_init,-z,relro,-z,now,-z,noexecstack",
		"-o", binary,
	)
	if combined, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to link guard fixture: %v\n%s", err, combined)
	}
	combined, err := exec.Command(binary).CombinedOutput()
	if err != nil {
		t.Fatalf("guard fixture failed: %v\n%s", err, combined)
	}
	if strings.TrimSpace(string(combined)) != "blocked" {
		t.Fatalf("unexpected guard result: %q", combined)
	}
}

func TestReadFileLimitedRejectsSymlink(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "target.png")
	link := filepath.Join(dir, "out.png")
	if err := os.WriteFile(target, []byte("not an image"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, link); err != nil {
		t.Fatal(err)
	}
	if _, err := readFileLimited(link, maxImageBytes); err == nil {
		t.Fatal("symlink output was accepted")
	}
}

func TestFindOutputFileIgnoresSymlink(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "target")
	if err := os.WriteFile(target, []byte("secret"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, filepath.Join(dir, "out.png")); err != nil {
		t.Fatal(err)
	}
	if found := findOutputFile(dir); found != "" {
		t.Fatalf("symlink output was discovered: %s", found)
	}
}

func TestLongProcessIsReaped(t *testing.T) {
	process, _, _, err := startLongProcess("/bin/sh", []string{"-c", "exit 0"}, t.TempDir(), "", nil)
	if err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(2 * time.Second)
	for !processExited(process) && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if !processExited(process) {
		stopProc(process)
		t.Fatal("exited process was not reaped")
	}
}

func containsString(values []string, expected string) bool {
	for _, value := range values {
		if value == expected {
			return true
		}
	}
	return false
}
