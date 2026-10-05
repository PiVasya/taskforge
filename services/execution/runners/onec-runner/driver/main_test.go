package main

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestDecodeEnvelopeRejectsWrongSchema(t *testing.T) {
	_, err := decodeEnvelope(strings.NewReader(`{"schema":"wrong","mode":"run","code":"x=1;"}`))
	if err == nil {
		t.Fatal("expected schema error")
	}
}

func TestCopyTreeRejectsSymlink(t *testing.T) {
	src := t.TempDir()
	dst := t.TempDir()
	if err := os.WriteFile(filepath.Join(src, "1Cv8.1CD"), []byte("db"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("1Cv8.1CD", filepath.Join(src, "link")); err != nil {
		t.Fatal(err)
	}
	if err := copyTree(src, filepath.Join(dst, "copy")); err == nil {
		t.Fatal("expected symlink rejection")
	}
}

func TestBslStringExprEscapesQuotesAndNewlines(t *testing.T) {
	got := bslStringExpr("a\"b\nc")
	want := `"a""b" + Символы.ПС + "c"`
	if got != want {
		t.Fatalf("got %q want %q", got, want)
	}
}

func TestRenderFormModuleUsesExportedSolutionAndHidesTestsInTrustedModule(t *testing.T) {
	in := "abc"
	out := "ABC"
	request := envelope{Mode: "tests", Tests: []testCase{{Input: &in, ExpectedOutput: &out, IsHidden: true}}}
	module := renderFormModule(request, "/work/job/result.json")
	for _, needle := range []string{"РеквизитФормыВЗначение(\"Объект\")", "ОбъектОбработки.Решение(\"abc\")", `"expectedOutput", "ABC"`, `"hidden", Истина`, "ЗаписатьJSON"} {
		if !strings.Contains(module, needle) {
			t.Fatalf("module misses %q:\n%s", needle, module)
		}
	}
}

func makeJudgeSkeleton(t *testing.T, root string) string {
	t.Helper()
	source := filepath.Join(root, "judge-src")
	files := map[string]string{
		"TaskForgeJudge.xml":                     "<root/>",
		"TaskForgeJudge/Forms/Form.xml":          "<form/>",
		"TaskForgeJudge/Forms/Form/Ext/Form.xml": "<form-ext/>",
	}
	for rel, body := range files {
		path := filepath.Join(source, rel)
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	return source
}

func TestExecuteJobCompilesEphemeralJudgeAndReturnsResult(t *testing.T) {
	root := t.TempDir()
	template := filepath.Join(root, "template")
	if err := os.MkdirAll(template, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(template, "1Cv8.1CD"), []byte("db"), 0o400); err != nil {
		t.Fatal(err)
	}
	judgeSource := makeJudgeSkeleton(t, root)
	fake := filepath.Join(root, "fake-1cv8")
	script := `#!/bin/sh
set -eu
mode="$1"
shift
for arg in "$@"; do [ "$arg" != "/L" ] || exit 17; done
printf '%s\n' "$@" | grep -qx '/Lru' || exit 18
if [ "$mode" = "DESIGNER" ]; then
  epf=""
  rootxml=""
  while [ "$#" -gt 0 ]; do
    if [ "$1" = "/LoadExternalDataProcessorOrReportFromFiles" ]; then
      shift; rootxml="$1"; shift; epf="$1"; break
    fi
    shift
  done
  [ -s "$rootxml" ]
  job=$(dirname "$(dirname "$rootxml")")
  [ -s "$job/judge-src/TaskForgeJudge/Ext/ObjectModule.bsl" ]
  [ -s "$job/judge-src/TaskForgeJudge/Forms/Form/Ext/Form/Module.bsl" ]
  printf 'epf' > "$epf"
  exit 0
fi
if [ "$mode" = "ENTERPRISE" ]; then
  epf=""
  while [ "$#" -gt 0 ]; do
    if [ "$1" = "/Execute" ]; then shift; epf="$1"; break; fi
    shift
  done
  [ -s "$epf" ]
  dir=$(dirname "$epf")
  [ -w "$dir/infobase/1Cv8.1CD" ]
  printf '%s\n' '{"results":[{"input":"1","expectedOutput":"2","actualOutput":"2","passed":true,"status":"ok","exitCode":0,"stderr":"","compileStderr":"","hidden":false}]}' > "$dir/result.json"
  exit 0
fi
exit 9
`
	if err := os.WriteFile(fake, []byte(script), 0o700); err != nil {
		t.Fatal(err)
	}
	cfg := config{executable: fake, templateDir: template, judgeSourceDir: judgeSource, workRoot: filepath.Join(root, "work"), useXvfb: false, language: "ru"}
	limit := 1000
	code := "Функция Решение(Вход) Экспорт\nВозврат Вход;\nКонецФункции"
	in := "1"
	out := "2"
	request := envelope{Schema: driverSchema, Mode: "tests", Code: code, TimeLimitMs: &limit, Tests: []testCase{{Input: &in, ExpectedOutput: &out}}}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	result, err := executeJob(ctx, cfg, request)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(result), `"passed":true`) {
		t.Fatalf("unexpected result: %s", result)
	}
	entries, err := os.ReadDir(cfg.workRoot)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 0 {
		t.Fatalf("job cleanup failed: %d entries remain", len(entries))
	}
}

func TestCompileFailureReturnsCompileErrorJSON(t *testing.T) {
	root := t.TempDir()
	template := filepath.Join(root, "template")
	if err := os.MkdirAll(template, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(template, "1Cv8.1CD"), []byte("db"), 0o600); err != nil {
		t.Fatal(err)
	}
	judgeSource := makeJudgeSkeleton(t, root)
	fake := filepath.Join(root, "fake-1cv8")
	script := `#!/bin/sh
set -eu
if [ "$1" = "DESIGNER" ]; then
  while [ "$#" -gt 0 ]; do
    if [ "$1" = "/Out" ]; then shift; printf 'syntax error' > "$1"; fi
    shift || true
  done
  exit 1
fi
exit 1
`
	if err := os.WriteFile(fake, []byte(script), 0o700); err != nil {
		t.Fatal(err)
	}
	cfg := config{executable: fake, templateDir: template, judgeSourceDir: judgeSource, workRoot: filepath.Join(root, "work"), useXvfb: false, language: "ru"}
	req := envelope{Schema: driverSchema, Mode: "run", Code: "broken"}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	result, err := executeJob(ctx, cfg, req)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(result), `"status":"compile_error"`) || !strings.Contains(string(result), "syntax error") {
		t.Fatalf("unexpected compile result: %s", result)
	}
}

func TestBoundedTimeout(t *testing.T) {
	huge := 999999
	if got := boundedTimeout(envelope{TimeLimitMs: &huge}); got != 90*time.Second {
		t.Fatalf("got %v", got)
	}
}

func TestCleanupStaleJobs(t *testing.T) {
	root := t.TempDir()
	stale := filepath.Join(root, "job-stale")
	fresh := filepath.Join(root, "job-fresh")
	other := filepath.Join(root, "keep-me")
	for _, path := range []string{stale, fresh, other} {
		if err := os.Mkdir(path, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	old := time.Now().Add(-20 * time.Minute)
	if err := os.Chtimes(stale, old, old); err != nil {
		t.Fatal(err)
	}
	cleanupStaleJobs(root, 10*time.Minute)
	if _, err := os.Stat(stale); !os.IsNotExist(err) {
		t.Fatalf("stale job was not removed: %v", err)
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Fatalf("fresh job was removed: %v", err)
	}
	if _, err := os.Stat(other); err != nil {
		t.Fatalf("non-job directory was removed: %v", err)
	}
}
