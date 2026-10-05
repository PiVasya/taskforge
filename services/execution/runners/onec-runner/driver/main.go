package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

const (
	driverSchema = "taskforge-onec-driver-v1"
	maxInput     = 20 << 20
	maxResult    = 4 << 20
	maxDiag      = 64 << 10
)

type testCase struct {
	Input          *string `json:"input"`
	ExpectedOutput *string `json:"expectedOutput"`
	IsHidden       bool    `json:"isHidden"`
}

type envelope struct {
	Schema        string     `json:"schema"`
	Mode          string     `json:"mode"`
	Code          string     `json:"code"`
	Input         *string    `json:"input,omitempty"`
	Tests         []testCase `json:"tests,omitempty"`
	TimeLimitMs   *int       `json:"timeLimitMs,omitempty"`
	MemoryLimitMb *int       `json:"memoryLimitMb,omitempty"`
}

type config struct {
	executable     string
	templateDir    string
	judgeSourceDir string
	workRoot       string
	useXvfb        bool
	language       string
}

func getenv(name, fallback string) string {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback
	}
	return value
}

func envBool(name string, fallback bool) bool {
	value := strings.TrimSpace(strings.ToLower(os.Getenv(name)))
	if value == "" {
		return fallback
	}
	switch value {
	case "1", "true", "yes", "on":
		return true
	case "0", "false", "no", "off":
		return false
	default:
		return fallback
	}
}

func loadConfig() config {
	return config{
		executable:     getenv("ONEC_EXECUTABLE", "/opt/1cv8/taskforge/1cv8"),
		templateDir:    getenv("ONEC_TEMPLATE_DIR", "/opt/taskforge/onec/runtime/templates/code"),
		judgeSourceDir: getenv("ONEC_JUDGE_SOURCE_DIR", "/opt/taskforge/onec/judge-src"),
		workRoot:       getenv("ONEC_WORK_ROOT", "/work"),
		useXvfb:        envBool("ONEC_USE_XVFB", true),
		language:       getenv("ONEC_UI_LANGUAGE", "ru"),
	}
}

func resolveExecutable(value string) (string, error) {
	if strings.ContainsRune(value, filepath.Separator) {
		info, err := os.Stat(value)
		if err != nil {
			return "", err
		}
		if info.IsDir() || info.Mode()&0o111 == 0 {
			return "", errors.New("1C executable is not executable")
		}
		return value, nil
	}
	return exec.LookPath(value)
}

func judgeSourceFiles(cfg config) []string {
	return []string{
		filepath.Join(cfg.judgeSourceDir, "TaskForgeJudge.xml"),
		filepath.Join(cfg.judgeSourceDir, "TaskForgeJudge", "Forms", "Form.xml"),
		filepath.Join(cfg.judgeSourceDir, "TaskForgeJudge", "Forms", "Form", "Ext", "Form.xml"),
	}
}

func probe(cfg config) error {
	if _, err := resolveExecutable(cfg.executable); err != nil {
		return fmt.Errorf("1C executable unavailable: %w", err)
	}
	info, err := os.Stat(filepath.Join(cfg.templateDir, "1Cv8.1CD"))
	if err != nil || info.IsDir() {
		return errors.New("file-infobase template is unavailable")
	}
	for _, path := range judgeSourceFiles(cfg) {
		info, err := os.Stat(path)
		if err != nil || info.IsDir() || info.Size() == 0 {
			return fmt.Errorf("trusted Judge source is unavailable: %s", path)
		}
	}
	if cfg.useXvfb {
		if _, err := exec.LookPath("xvfb-run"); err != nil {
			return errors.New("xvfb-run is unavailable")
		}
	}
	if err := os.MkdirAll(cfg.workRoot, 0o700); err != nil {
		return fmt.Errorf("work root is unavailable: %w", err)
	}
	probeFile, err := os.CreateTemp(cfg.workRoot, ".probe-")
	if err != nil {
		return fmt.Errorf("work root is not writable: %w", err)
	}
	name := probeFile.Name()
	_ = probeFile.Close()
	_ = os.Remove(name)
	return nil
}

func decodeEnvelope(r io.Reader) (envelope, error) {
	limited := io.LimitReader(r, maxInput+1)
	data, err := io.ReadAll(limited)
	if err != nil {
		return envelope{}, err
	}
	if len(data) > maxInput {
		return envelope{}, errors.New("driver request is too large")
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	var request envelope
	if err := dec.Decode(&request); err != nil {
		return envelope{}, fmt.Errorf("invalid driver request: %w", err)
	}
	if err := dec.Decode(&struct{}{}); err != io.EOF {
		return envelope{}, errors.New("driver request must contain one JSON object")
	}
	if request.Schema != driverSchema {
		return envelope{}, errors.New("unsupported driver schema")
	}
	if request.Mode != "run" && request.Mode != "tests" {
		return envelope{}, errors.New("unsupported driver mode")
	}
	if strings.TrimSpace(request.Code) == "" {
		return envelope{}, errors.New("code is required")
	}
	if request.Mode == "tests" && len(request.Tests) == 0 {
		return envelope{}, errors.New("tests are required")
	}
	return request, nil
}

func copyFile(src, dst string, mode fs.FileMode) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	if err := os.MkdirAll(filepath.Dir(dst), 0o700); err != nil {
		return err
	}
	out, err := os.OpenFile(dst, os.O_CREATE|os.O_EXCL|os.O_WRONLY, mode.Perm())
	if err != nil {
		return err
	}
	_, copyErr := io.Copy(out, in)
	closeErr := out.Close()
	if copyErr != nil {
		return copyErr
	}
	return closeErr
}

func copyTree(src, dst string) error {
	root, err := filepath.Abs(src)
	if err != nil {
		return err
	}
	return filepath.WalkDir(root, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		rel, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		target := filepath.Join(dst, rel)
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if info.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("template symlinks are forbidden: %s", rel)
		}
		if entry.IsDir() {
			mode := info.Mode().Perm() | 0o700
			return os.MkdirAll(target, mode)
		}
		if !info.Mode().IsRegular() {
			return fmt.Errorf("unsupported template file type: %s", rel)
		}
		// The persistent template is immutable/read-only for the runner. 1C
		// must be able to mutate only this disposable private copy.
		mode := info.Mode().Perm() | 0o600
		return copyFile(path, target, mode)
	})
}

func cleanupStaleJobs(root string, olderThan time.Duration) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return
	}
	cutoff := time.Now().Add(-olderThan)
	for _, entry := range entries {
		if !entry.IsDir() || !strings.HasPrefix(entry.Name(), "job-") {
			continue
		}
		info, err := entry.Info()
		if err != nil || !info.ModTime().Before(cutoff) {
			continue
		}
		_ = os.RemoveAll(filepath.Join(root, entry.Name()))
	}
}

func boundedTimeout(request envelope) time.Duration {
	value := 30 * time.Second
	if request.TimeLimitMs != nil && *request.TimeLimitMs > 0 {
		value = time.Duration(*request.TimeLimitMs)*time.Millisecond + 20*time.Second
	}
	if value < 15*time.Second {
		value = 15 * time.Second
	}
	if value > 90*time.Second {
		value = 90 * time.Second
	}
	return value
}

func cleanEnvironment(home, tmp string) []string {
	path := os.Getenv("PATH")
	if strings.TrimSpace(path) == "" {
		path = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
	}
	return []string{
		"HOME=" + home,
		"TMPDIR=" + tmp,
		"PATH=" + path,
		"LANG=C.UTF-8",
		"LC_ALL=C.UTF-8",
		"TASKFORGE_ONEC_JOB=1",
	}
}

func wrap1C(cfg config, executable string, args []string) (string, []string) {
	if cfg.useXvfb {
		wrapped := append([]string{"-a", "-s", "-screen 0 1024x768x24", executable}, args...)
		return "xvfb-run", wrapped
	}
	return executable, args
}

func bslStringExpr(value string) string {
	value = strings.ReplaceAll(value, "\r\n", "\n")
	value = strings.ReplaceAll(value, "\r", "\n")
	parts := strings.Split(value, "\n")
	encoded := make([]string, 0, len(parts))
	for _, part := range parts {
		encoded = append(encoded, `"`+strings.ReplaceAll(part, `"`, `""`)+`"`)
	}
	if len(encoded) == 1 {
		return encoded[0]
	}
	return strings.Join(encoded, " + Символы.ПС + ")
}

func stringValue(value *string) string {
	if value == nil {
		return ""
	}
	return *value
}

func renderFormModule(request envelope, resultPath string) string {
	var b strings.Builder
	b.WriteString("&НаКлиенте\n")
	b.WriteString("Процедура OnOpen(Cancel)\n")
	b.WriteString("    ВыполнитьПроверкуНаСервере();\n")
	b.WriteString("    ЗавершитьРаботуСистемы(Ложь);\n")
	b.WriteString("КонецПроцедуры\n\n")
	b.WriteString("&НаСервере\n")
	b.WriteString("Процедура ЗаписатьРезультат(Данные)\n")
	b.WriteString("    Запись = Новый ЗаписьJSON;\n")
	b.WriteString("    Запись.ОткрытьФайл(" + bslStringExpr(resultPath) + ", КодировкаТекста.UTF8);\n")
	b.WriteString("    ЗаписатьJSON(Запись, Данные);\n")
	b.WriteString("    Запись.Закрыть();\n")
	b.WriteString("КонецПроцедуры\n\n")
	b.WriteString("&НаСервере\n")
	b.WriteString("Процедура ВыполнитьПроверкуНаСервере()\n")
	b.WriteString("    ОбъектОбработки = РеквизитФормыВЗначение(\"Объект\");\n")

	if request.Mode == "run" {
		b.WriteString("    Корень = Новый Структура;\n")
		b.WriteString("    Попытка\n")
		b.WriteString("        Фактическое = ОбъектОбработки.Решение(" + bslStringExpr(stringValue(request.Input)) + ");\n")
		b.WriteString("        Корень.Вставить(\"status\", \"ok\");\n")
		b.WriteString("        Корень.Вставить(\"exitCode\", 0);\n")
		b.WriteString("        Корень.Вставить(\"stdout\", Строка(Фактическое));\n")
		b.WriteString("        Корень.Вставить(\"stderr\", \"\");\n")
		b.WriteString("        Корень.Вставить(\"compileStderr\", \"\");\n")
		b.WriteString("    Исключение\n")
		b.WriteString("        Корень.Вставить(\"status\", \"runtime_error\");\n")
		b.WriteString("        Корень.Вставить(\"exitCode\", 1);\n")
		b.WriteString("        Корень.Вставить(\"stdout\", \"\");\n")
		b.WriteString("        Корень.Вставить(\"stderr\", ОписаниеОшибки());\n")
		b.WriteString("        Корень.Вставить(\"compileStderr\", \"\");\n")
		b.WriteString("    КонецПопытки;\n")
		b.WriteString("    ЗаписатьРезультат(Корень);\n")
	} else {
		b.WriteString("    Результаты = Новый Массив;\n")
		for _, test := range request.Tests {
			input := stringValue(test.Input)
			expected := stringValue(test.ExpectedOutput)
			hidden := "Ложь"
			if test.IsHidden {
				hidden = "Истина"
			}
			b.WriteString("    Элемент = Новый Структура;\n")
			b.WriteString("    Элемент.Вставить(\"input\", " + bslStringExpr(input) + ");\n")
			b.WriteString("    Элемент.Вставить(\"expectedOutput\", " + bslStringExpr(expected) + ");\n")
			b.WriteString("    Элемент.Вставить(\"hidden\", " + hidden + ");\n")
			b.WriteString("    Попытка\n")
			b.WriteString("        Фактическое = ОбъектОбработки.Решение(" + bslStringExpr(input) + ");\n")
			b.WriteString("        ФактическаяСтрока = Строка(Фактическое);\n")
			b.WriteString("        Элемент.Вставить(\"actualOutput\", ФактическаяСтрока);\n")
			b.WriteString("        Элемент.Вставить(\"passed\", ФактическаяСтрока = " + bslStringExpr(expected) + ");\n")
			b.WriteString("        Элемент.Вставить(\"status\", \"ok\");\n")
			b.WriteString("        Элемент.Вставить(\"exitCode\", 0);\n")
			b.WriteString("        Элемент.Вставить(\"stderr\", \"\");\n")
			b.WriteString("        Элемент.Вставить(\"compileStderr\", \"\");\n")
			b.WriteString("    Исключение\n")
			b.WriteString("        Элемент.Вставить(\"actualOutput\", \"\");\n")
			b.WriteString("        Элемент.Вставить(\"passed\", Ложь);\n")
			b.WriteString("        Элемент.Вставить(\"status\", \"runtime_error\");\n")
			b.WriteString("        Элемент.Вставить(\"exitCode\", 1);\n")
			b.WriteString("        Элемент.Вставить(\"stderr\", ОписаниеОшибки());\n")
			b.WriteString("        Элемент.Вставить(\"compileStderr\", \"\");\n")
			b.WriteString("    КонецПопытки;\n")
			b.WriteString("    Результаты.Добавить(Элемент);\n")
		}
		b.WriteString("    Корень = Новый Структура;\n")
		b.WriteString("    Корень.Вставить(\"results\", Результаты);\n")
		b.WriteString("    ЗаписатьРезультат(Корень);\n")
	}
	b.WriteString("КонецПроцедуры\n")
	return b.String()
}

func prepareJudgeSource(cfg config, request envelope, jobDir, resultPath string) (string, error) {
	target := filepath.Join(jobDir, "judge-src")
	if err := copyTree(cfg.judgeSourceDir, target); err != nil {
		return "", fmt.Errorf("copy Judge source: %w", err)
	}
	objectModule := filepath.Join(target, "TaskForgeJudge", "Ext", "ObjectModule.bsl")
	if err := os.MkdirAll(filepath.Dir(objectModule), 0o700); err != nil {
		return "", err
	}
	learner := strings.TrimSpace(request.Code) + "\n"
	if err := os.WriteFile(objectModule, []byte(learner), 0o600); err != nil {
		return "", err
	}
	formModule := filepath.Join(target, "TaskForgeJudge", "Forms", "Form", "Ext", "Form", "Module.bsl")
	if err := os.MkdirAll(filepath.Dir(formModule), 0o700); err != nil {
		return "", err
	}
	if err := os.WriteFile(formModule, []byte(renderFormModule(request, resultPath)), 0o600); err != nil {
		return "", err
	}
	return filepath.Join(target, "TaskForgeJudge.xml"), nil
}

func readBounded(path string, max int) string {
	file, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer file.Close()
	data, _ := io.ReadAll(io.LimitReader(file, int64(max)))
	return strings.TrimSpace(string(data))
}

func compileErrorJSON(message string) []byte {
	if len(message) > maxDiag {
		message = message[:maxDiag]
	}
	payload := map[string]any{
		"status":        "compile_error",
		"exitCode":      1,
		"stdout":        "",
		"stderr":        "",
		"compileStderr": message,
	}
	data, _ := json.Marshal(payload)
	return data
}

func runCommand(ctx context.Context, cfg config, executable, jobDir, home, tmp string, args []string) (string, string, error) {
	name, wrappedArgs := wrap1C(cfg, executable, args)
	cmd := exec.CommandContext(ctx, name, wrappedArgs...)
	cmd.Dir = jobDir
	cmd.Env = cleanEnvironment(home, tmp)
	var stdout bytes.Buffer
	var stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	out := stdout.String()
	errOut := stderr.String()
	if len(out) > maxDiag {
		out = out[:maxDiag]
	}
	if len(errOut) > maxDiag {
		errOut = errOut[:maxDiag]
	}
	return out, errOut, err
}

func readResult(path string, mode string, testCount int) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, maxResult+1))
	if err != nil {
		return nil, err
	}
	if len(data) > maxResult {
		return nil, errors.New("judge result is too large")
	}
	data = bytes.TrimSpace(data)
	if !json.Valid(data) {
		return nil, errors.New("judge result is not valid JSON")
	}
	var root map[string]json.RawMessage
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, err
	}
	if mode == "tests" {
		raw, ok := root["results"]
		if !ok {
			return nil, errors.New("judge result misses results")
		}
		var results []json.RawMessage
		if err := json.Unmarshal(raw, &results); err != nil {
			return nil, errors.New("judge results is not an array")
		}
		if len(results) != testCount {
			return nil, errors.New("judge result count is invalid")
		}
	}
	return data, nil
}

func executeJob(ctx context.Context, cfg config, request envelope) ([]byte, error) {
	executable, err := resolveExecutable(cfg.executable)
	if err != nil {
		return nil, fmt.Errorf("1C executable unavailable: %w", err)
	}
	if err := probe(cfg); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(cfg.workRoot, 0o700); err != nil {
		return nil, err
	}
	// A client disconnect can force-kill the driver before executeJob defers run.
	// Reap only jobs far older than the maximum supported execution window.
	cleanupStaleJobs(cfg.workRoot, 10*time.Minute)
	jobDir, err := os.MkdirTemp(cfg.workRoot, "job-")
	if err != nil {
		return nil, err
	}
	defer os.RemoveAll(jobDir)

	infobase := filepath.Join(jobDir, "infobase")
	if err := copyTree(cfg.templateDir, infobase); err != nil {
		return nil, fmt.Errorf("copy infobase template: %w", err)
	}
	home := filepath.Join(jobDir, "home")
	tmp := filepath.Join(jobDir, "tmp")
	if err := os.MkdirAll(home, 0o700); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(tmp, 0o700); err != nil {
		return nil, err
	}

	resultPath := filepath.Join(jobDir, "result.json")
	rootXML, err := prepareJudgeSource(cfg, request, jobDir, resultPath)
	if err != nil {
		return nil, err
	}
	epfPath := filepath.Join(jobDir, "TaskForgeJudge.epf")
	compileLog := filepath.Join(jobDir, "compile.log")
	compileResult := filepath.Join(jobDir, "compile.result")
	compileArgs := []string{
		"DESIGNER", "/F", infobase,
		"/DisableStartupMessages", "/DisableStartupDialogs",
		"/LoadExternalDataProcessorOrReportFromFiles", rootXML, epfPath,
		"/Out", compileLog, "/DumpResult", compileResult,
		"/L" + cfg.language,
	}
	compileOut, compileErrOut, compileRunErr := runCommand(ctx, cfg, executable, jobDir, home, tmp, compileArgs)
	epfInfo, epfErr := os.Stat(epfPath)
	if compileRunErr != nil || epfErr != nil || epfInfo.IsDir() || epfInfo.Size() == 0 {
		diagnostic := readBounded(compileLog, maxDiag)
		if diagnostic == "" {
			diagnostic = strings.TrimSpace(compileErrOut)
		}
		if diagnostic == "" {
			diagnostic = strings.TrimSpace(compileOut)
		}
		if diagnostic == "" && compileRunErr != nil {
			diagnostic = compileRunErr.Error()
		}
		if diagnostic == "" {
			diagnostic = "1C Designer did not produce TaskForgeJudge.epf"
		}
		return compileErrorJSON(diagnostic), nil
	}

	platformLog := filepath.Join(jobDir, "platform.log")
	executeArgs := []string{
		"ENTERPRISE", "/F", infobase,
		"/Execute", epfPath,
		"/DisableStartupMessages", "/DisableStartupDialogs",
		"/Out", platformLog,
		"/L" + cfg.language,
	}
	stdout, stderr, runErr := runCommand(ctx, cfg, executable, jobDir, home, tmp, executeArgs)
	result, resultErr := readResult(resultPath, request.Mode, len(request.Tests))
	if resultErr == nil {
		return result, nil
	}
	if errors.Is(ctx.Err(), context.DeadlineExceeded) {
		return nil, errors.New("1C execution timed out")
	}
	diagnostic := readBounded(platformLog, maxDiag)
	if diagnostic == "" {
		diagnostic = strings.TrimSpace(stderr)
	}
	if diagnostic == "" {
		diagnostic = strings.TrimSpace(stdout)
	}
	if runErr != nil {
		return nil, fmt.Errorf("1C process failed: %v; %s", runErr, diagnostic)
	}
	return nil, fmt.Errorf("Judge did not produce a valid result: %w; %s", resultErr, diagnostic)
}

func main() {
	cfg := loadConfig()
	if len(os.Args) == 2 && os.Args[1] == "--probe" {
		if err := probe(cfg); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		fmt.Println("ready")
		return
	}
	if len(os.Args) != 1 {
		fmt.Fprintln(os.Stderr, "usage: driver [--probe]")
		os.Exit(2)
	}
	request, err := decodeEnvelope(os.Stdin)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	ctx, cancel := context.WithTimeout(context.Background(), boundedTimeout(request))
	defer cancel()
	result, err := executeJob(ctx, cfg, request)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(3)
	}
	if _, err := os.Stdout.Write(append(result, '\n')); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(4)
	}
}
