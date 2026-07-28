package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func compileJavaForPolicyTest(t *testing.T, source string) string {
	t.Helper()
	javac, err := exec.LookPath("javac")
	if err != nil {
		t.Skip("javac is not installed")
	}
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "Main.java"), []byte(source), 0o600); err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command(javac, "-encoding", "UTF-8", "-g:none", "-proc:none", "Main.java")
	cmd.Dir = dir
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("javac failed: %v\n%s", err, output)
	}
	return dir
}

func TestJavaClassPolicyAllowsNormalConsoleProgram(t *testing.T) {
	dir := compileJavaForPolicyTest(t, `
public class Main {
    public static void main(String[] args) {
        int sum = 0;
        for (int i = 0; i < 10; i++) sum += i;
        System.out.println(sum);
    }
}`)
	if err := verifyJavaClassFiles(dir); err != nil {
		t.Fatalf("safe class was rejected: %v", err)
	}
}

func TestJavaClassPolicyRejectsProcessBuilder(t *testing.T) {
	dir := compileJavaForPolicyTest(t, `
public class Main {
    public static void main(String[] args) throws Exception {
        new ProcessBuilder("id").start();
    }
}`)
	if err := verifyJavaClassFiles(dir); err == nil {
		t.Fatal("ProcessBuilder class reference was not rejected")
	}
}

func TestJavaClassPolicyRejectsReflection(t *testing.T) {
	dir := compileJavaForPolicyTest(t, `
public class Main {
    public static void main(String[] args) throws Exception {
        System.out.println(Class.forName("java.lang.String"));
    }
}`)
	if err := verifyJavaClassFiles(dir); err == nil {
		t.Fatal("Class.forName method reference was not rejected")
	}
}
