package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unicode/utf8"
)

const (
	interactiveCompileTimeout  = 30 * time.Second
	interactiveMaxRuntime      = 120 * time.Second
	interactiveMaxOutputBytes  = 2 * 1024 * 1024
	interactiveMaxInputBytes   = 1024 * 1024
	interactiveMaxMessage      = 128 * 1024
	interactiveMaxStartMessage = 1536 * 1024
)

var runnerJobSlots = make(chan struct{}, 1)

type interactiveMessage struct {
	Type          string             `json:"type"`
	Code          string             `json:"code,omitempty"`
	Data          string             `json:"data,omitempty"`
	Columns       int                `json:"columns,omitempty"`
	Rows          int                `json:"rows,omitempty"`
	TimeLimitMs   int                `json:"timeLimitMs,omitempty"`
	MemoryLimitMb int                `json:"memoryLimitMb,omitempty"`
	Attestation   *policyAttestation `json:"attestation,omitempty"`
}

type interactiveWriter struct {
	mutex sync.Mutex
	enc   *json.Encoder
}

func (w *interactiveWriter) send(value any) error {
	w.mutex.Lock()
	defer w.mutex.Unlock()
	return w.enc.Encode(value)
}

func startInteractiveServer(kind string) {
	port := env("INTERACTIVE_PORT", "9090")
	listener, err := net.Listen("tcp", ":"+port)
	if err != nil {
		log.Fatalf("%s-runner interactive listener failed: %v", kind, err)
	}
	log.Printf("%s-runner interactive console listening on :%s", kind, port)
	go func() {
		for {
			conn, acceptErr := listener.Accept()
			if acceptErr != nil {
				log.Printf("%s-runner interactive accept failed: %v", kind, acceptErr)
				time.Sleep(250 * time.Millisecond)
				continue
			}
			go handleInteractiveConnection(kind, conn)
		}
	}()
}

func handleInteractiveConnection(kind string, conn net.Conn) {
	defer conn.Close()
	_ = conn.SetReadDeadline(time.Now().Add(15 * time.Second))
	reader := bufio.NewReaderSize(conn, 64*1024)
	firstLine, err := reader.ReadBytes('\n')
	if err != nil || len(firstLine) > interactiveMaxStartMessage {
		return
	}
	var start interactiveMessage
	if err := json.Unmarshal(firstLine, &start); err != nil || start.Type != "start" {
		return
	}
	_ = conn.SetReadDeadline(time.Time{})
	writer := &interactiveWriter{enc: json.NewEncoder(conn)}

	if err := validateRunRequest(start.Code, nil); err != nil {
		_ = writer.send(map[string]any{"type": "error", "message": err.Error()})
		return
	}
	if err := verifyPolicyAttestation(kind, "standard", start.Code, start.Attestation); err != nil {
		log.Printf("%s-runner rejected interactive unattested source: %v", kind, err)
		_ = writer.send(map[string]any{"type": "error", "message": "Код не прошёл обязательную проверку безопасности."})
		return
	}

	select {
	case runnerJobSlots <- struct{}{}:
		defer func() { <-runnerJobSlots }()
	case <-time.After(10 * time.Second):
		_ = writer.send(map[string]any{"type": "error", "message": "Раннер занят. Повторите запуск через несколько секунд."})
		return
	}

	columns := clampInteractive(start.Columns, 40, 240, 110)
	rows := clampInteractive(start.Rows, 10, 80, 30)
	runtimeMs := clampInteractive(start.TimeLimitMs, 1_000, int(interactiveMaxRuntime/time.Millisecond), int(interactiveMaxRuntime/time.Millisecond))
	memoryMb := clampInteractive(start.MemoryLimitMb, minMemoryLimitMb, maxMemoryLimitMb, defaultMemoryMb(kind))

	_ = writer.send(map[string]any{"type": "status", "phase": "compiling", "message": "Компиляция..."})
	dir, err := os.MkdirTemp("", "taskforge-"+kind+"-interactive-")
	if err != nil {
		_ = writer.send(map[string]any{"type": "error", "message": "Не удалось создать временную среду запуска."})
		return
	}
	defer os.RemoveAll(dir)

	compileCtx, cancelCompile := context.WithTimeout(context.Background(), interactiveCompileTimeout)
	program, compileErr := compileProgramContext(compileCtx, kind, start.Code, dir, int(interactiveCompileTimeout/time.Millisecond), memoryMb)
	cancelCompile()
	if compileErr != nil {
		compileErr = sanitizeProcessResult(compileErr)
		text := ""
		if compileErr.CompileStderr != nil {
			text = *compileErr.CompileStderr
		}
		if strings.TrimSpace(text) == "" {
			text = compileErr.Stderr
		}
		if strings.TrimSpace(text) == "" {
			text = "Компиляция завершилась с ошибкой.\r\n"
		}
		_ = writer.send(map[string]any{"type": "output", "stream": "stderr", "data": normalizeTerminalText(text)})
		_ = writer.send(map[string]any{"type": "exit", "exitCode": compileErr.ExitCode, "reason": firstNonEmpty(compileErr.Status, "compile_error"), "durationMs": 0})
		return
	}

	commandLine, baseEnv, sandboxExports, err := interactiveCommand(kind, program, columns, rows, runtimeMs)
	if err != nil {
		log.Printf("%s-runner interactive sandbox unavailable: %v", kind, err)
		_ = writer.send(map[string]any{"type": "error", "message": "Защищённая среда запуска временно недоступна."})
		return
	}

	runtimeCtx, cancelRuntime := context.WithTimeout(context.Background(), time.Duration(runtimeMs)*time.Millisecond+3*time.Second)
	defer cancelRuntime()
	cmd := exec.CommandContext(runtimeCtx, "script", "-qefc", commandLine, "/dev/null")
	cmd.Dir = program.Cwd
	cmd.Env = append(baseEnv, sandboxExports...)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}

	stdin, err := cmd.StdinPipe()
	if err != nil {
		_ = writer.send(map[string]any{"type": "error", "message": "Не удалось открыть ввод программы."})
		return
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		_ = writer.send(map[string]any{"type": "error", "message": "Не удалось открыть вывод программы."})
		return
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		_ = writer.send(map[string]any{"type": "error", "message": "Не удалось открыть поток ошибок программы."})
		return
	}

	startedAt := time.Now()
	if err := cmd.Start(); err != nil {
		_ = writer.send(map[string]any{"type": "error", "message": "Не удалось запустить программу."})
		return
	}
	_ = writer.send(map[string]any{"type": "status", "phase": "running", "message": "Программа запущена", "pid": cmd.Process.Pid})

	var totalOutput int64
	var outputMutex sync.Mutex
	outputLimitHit := false
	stopOnce := sync.Once{}
	stopProcess := func() {
		stopOnce.Do(func() {
			if cmd.Process != nil {
				_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
				_ = cmd.Process.Kill()
			}
		})
	}

	outputDone := make(chan struct{}, 2)
	copyOutput := func(stream string, source io.Reader) {
		defer func() { outputDone <- struct{}{} }()
		buffer := make([]byte, 4096)
		pendingUTF8 := make([]byte, 0, utf8.UTFMax)
		for {
			n, readErr := source.Read(buffer)
			if n > 0 {
				outputMutex.Lock()
				left := interactiveMaxOutputBytes - totalOutput
				if left > 0 {
					take := n
					if int64(take) > left {
						take = int(left)
					}
					totalOutput += int64(take)
					raw := append(pendingUTF8, buffer[:take]...)
					complete, pending := splitUTF8Prefix(raw)
					completeText := string(complete)
					pendingUTF8 = append(pendingUTF8[:0], pending...)
					if completeText != "" {
						_ = writer.send(map[string]any{"type": "output", "stream": stream, "data": completeText})
					}
				}
				if totalOutput >= interactiveMaxOutputBytes && !outputLimitHit {
					outputLimitHit = true
					if len(pendingUTF8) > 0 {
						_ = writer.send(map[string]any{"type": "output", "stream": stream, "data": strings.ToValidUTF8(string(pendingUTF8), "�")})
						pendingUTF8 = pendingUTF8[:0]
					}
					_ = writer.send(map[string]any{"type": "output", "stream": "system", "data": "\r\n[TaskForge] Вывод остановлен: превышено ограничение 2 МБ.\r\n"})
					stopProcess()
				}
				outputMutex.Unlock()
			}
			if readErr != nil {
				if len(pendingUTF8) > 0 {
					outputMutex.Lock()
					_ = writer.send(map[string]any{"type": "output", "stream": stream, "data": strings.ToValidUTF8(string(pendingUTF8), "�")})
					outputMutex.Unlock()
				}
				return
			}
		}
	}
	go copyOutput("stdout", stdout)
	go copyOutput("stderr", stderr)

	clientClosed := make(chan struct{})
	go func() {
		defer close(clientClosed)
		totalInput := 0
		for {
			line, readErr := reader.ReadBytes('\n')
			if readErr != nil || len(line) > interactiveMaxMessage {
				stopProcess()
				return
			}
			var message interactiveMessage
			if json.Unmarshal(line, &message) != nil {
				continue
			}
			switch message.Type {
			case "input":
				totalInput += len(message.Data)
				if totalInput > interactiveMaxInputBytes {
					_ = writer.send(map[string]any{"type": "output", "stream": "system", "data": "\r\n[TaskForge] Ввод остановлен: превышено ограничение 1 МБ.\r\n"})
					stopProcess()
					return
				}
				if _, writeErr := io.WriteString(stdin, message.Data); writeErr != nil {
					return
				}
			case "interrupt":
				_, _ = io.WriteString(stdin, "\x03")
			case "stop":
				stopProcess()
				return
			case "resize":
				// xterm.js immediately resizes the browser viewport. The process
				// keeps the initial PTY geometry because util-linux script does not
				// expose its master descriptor to this service.
			}
		}
	}()

	waitErr := cmd.Wait()
	stopProcess()
	<-outputDone
	<-outputDone
	_ = stdin.Close()

	exitCode := 0
	reason := "completed"
	if waitErr != nil {
		reason = "runtime_error"
		if exitError, ok := waitErr.(*exec.ExitError); ok {
			exitCode = exitError.ExitCode()
		} else {
			exitCode = 1
		}
	}
	if runtimeCtx.Err() == context.DeadlineExceeded {
		reason = "time_limit"
		exitCode = 124
		_ = writer.send(map[string]any{"type": "output", "stream": "system", "data": "\r\n[TaskForge] Процесс завершён по тайм-ауту.\r\n"})
	} else if outputLimitHit {
		reason = "output_limit"
		exitCode = 125
	}

	_ = writer.send(map[string]any{
		"type":       "exit",
		"exitCode":   exitCode,
		"reason":     reason,
		"durationMs": time.Since(startedAt).Milliseconds(),
	})
}

func interactiveCommand(kind string, program *preparedProgram, columns, rows, runtimeMs int) (string, []string, []string, error) {
	baseEnv := runnerChildEnvironment()
	baseEnv = append(baseEnv, "TERM=xterm-256color", "COLORTERM=truecolor")
	sandbox := []string{}
	if kind == "cpp" {
		sandbox = append(sandbox, "TASKFORGE_LIMIT_CPU_SECONDS="+strconv.Itoa((runtimeMs+999)/1000+2))
	} else {
		extra, err := sandboxRuntimeEnvironment(runtimeMs)
		if err != nil {
			return "", nil, nil, err
		}
		sandbox = append(sandbox, extra...)
	}
	sandbox = append(sandbox, program.Env...)

	exports := make([]string, 0, len(sandbox))
	for _, item := range sandbox {
		key, value, ok := strings.Cut(item, "=")
		if !ok || strings.TrimSpace(key) == "" {
			continue
		}
		exports = append(exports, "export "+key+"="+shellQuote(value))
	}

	args := []string{shellQuote(program.Cmd)}
	for _, arg := range program.Args {
		args = append(args, shellQuote(arg))
	}
	parts := []string{fmt.Sprintf("stty cols %d rows %d", columns, rows)}
	parts = append(parts, exports...)
	parts = append(parts, "exec "+strings.Join(args, " "))
	return strings.Join(parts, "; "), baseEnv, nil, nil
}

func shellQuote(value string) string {
	return "'" + strings.ReplaceAll(value, "'", "'\\''") + "'"
}

func splitUTF8Prefix(data []byte) ([]byte, []byte) {
	index := 0
	for index < len(data) {
		if data[index] < utf8.RuneSelf {
			index++
			continue
		}
		if !utf8.FullRune(data[index:]) {
			return data[:index], data[index:]
		}
		_, size := utf8.DecodeRune(data[index:])
		if size <= 0 {
			size = 1
		}
		index += size
	}
	return data, nil
}

func normalizeTerminalText(value string) string {
	value = sanitizeRunnerText(value)
	value = strings.ReplaceAll(value, "\r\n", "\n")
	return strings.ReplaceAll(value, "\n", "\r\n")
}

func clampInteractive(value, minimum, maximum, fallback int) int {
	if value <= 0 {
		value = fallback
	}
	if value < minimum {
		return minimum
	}
	if value > maximum {
		return maximum
	}
	return value
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return value
		}
	}
	return ""
}

func interactiveWorkFile(dir, name string) string {
	return filepath.Join(dir, name)
}

func sandboxRuntimeEnvironment(timeMs int) ([]string, error) {
	return []string{"TASKFORGE_LIMIT_CPU_SECONDS=" + strconv.Itoa((timeMs+999)/1000+2)}, nil
}
