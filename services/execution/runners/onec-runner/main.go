package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
	"time"
)

const (
	maxRequestBytes      = 16 << 20
	maxCodeBytes         = 1 << 20
	maxTestsPerRequest   = 128
	maxDriverOutputBytes = 4 << 20
	defaultTimeout       = 40 * time.Second
	maxTimeout           = 100 * time.Second
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

type driverEnvelope struct {
	Schema        string     `json:"schema"`
	Mode          string     `json:"mode"`
	Code          string     `json:"code"`
	Input         *string    `json:"input,omitempty"`
	Tests         []testCase `json:"tests,omitempty"`
	TimeLimitMs   *int       `json:"timeLimitMs,omitempty"`
	MemoryLimitMb *int       `json:"memoryLimitMb,omitempty"`
}

type limitedBuffer struct {
	buf bytes.Buffer
	max int
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	remaining := b.max - b.buf.Len()
	if remaining > 0 {
		if len(p) > remaining {
			_, _ = b.buf.Write(p[:remaining])
		} else {
			_, _ = b.buf.Write(p)
		}
	}
	return len(p), nil
}

func (b *limitedBuffer) String() string { return b.buf.String() }

func driverPath() string {
	if value := strings.TrimSpace(os.Getenv("ONEC_DRIVER")); value != "" {
		return value
	}
	return "/opt/taskforge/onec/driver"
}

func driverEnvironment() []string {
	keys := []string{
		"ONEC_EXECUTABLE",
		"ONEC_TEMPLATE_DIR",
		"ONEC_JUDGE_SOURCE_DIR",
		"ONEC_WORK_ROOT",
		"ONEC_USE_XVFB",
		"ONEC_UI_LANGUAGE",
	}
	env := []string{
		"HOME=/tmp",
		"TMPDIR=/tmp",
		"PATH=" + os.Getenv("PATH"),
		"LANG=C.UTF-8",
		"LC_ALL=C.UTF-8",
		"TASKFORGE_ONEC_JOB=1",
	}
	for _, key := range keys {
		if value, ok := os.LookupEnv(key); ok {
			env = append(env, key+"="+value)
		}
	}
	return env
}

func runtimeReadiness() error {
	if err := policyAttestationReady(); err != nil {
		return fmt.Errorf("code analyzer public key: %w", err)
	}
	driver := driverPath()
	info, err := os.Stat(driver)
	if err != nil {
		return fmt.Errorf("1C execution driver is unavailable: %w", err)
	}
	if info.IsDir() || info.Mode()&0o111 == 0 {
		return errors.New("1C execution driver is not executable")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	command := exec.CommandContext(ctx, driver, "--probe")
	command.Env = driverEnvironment()
	output, err := command.CombinedOutput()
	if ctx.Err() == context.DeadlineExceeded {
		return errors.New("1C execution driver probe timed out")
	}
	if err != nil {
		reason := strings.TrimSpace(string(output))
		if reason == "" {
			reason = err.Error()
		}
		return fmt.Errorf("1C runtime probe failed: %s", reason)
	}
	return nil
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func decodeJSON(w http.ResponseWriter, r *http.Request, target any) bool {
	r.Body = http.MaxBytesReader(w, r.Body, maxRequestBytes)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"status": "BadRequest", "error": "invalid request"})
		return false
	}
	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		writeJSON(w, http.StatusBadRequest, map[string]any{"status": "BadRequest", "error": "request must contain one JSON object"})
		return false
	}
	return true
}

func validateCode(code string) error {
	if strings.TrimSpace(code) == "" {
		return errors.New("code is required")
	}
	if len([]byte(code)) > maxCodeBytes {
		return errors.New("code is too large")
	}
	return nil
}

func requestTimeout(timeLimitMs *int) time.Duration {
	timeout := defaultTimeout
	if timeLimitMs != nil && *timeLimitMs > 0 {
		timeout = time.Duration(*timeLimitMs)*time.Millisecond + 30*time.Second
	}
	if timeout > maxTimeout {
		timeout = maxTimeout
	}
	if timeout < 5*time.Second {
		timeout = 5 * time.Second
	}
	return timeout
}

func configuredRunnerSlots() int {
	raw := strings.TrimSpace(os.Getenv("ONEC_RUNNER_SLOTS"))
	if raw == "" {
		return 1
	}
	value, err := strconv.Atoi(raw)
	if err != nil || value < 1 {
		return 1
	}
	if value > 4 {
		return 4
	}
	return value
}

var runnerJobSlots = make(chan struct{}, configuredRunnerSlots())

func runDriver(r *http.Request, envelope driverEnvelope, timeout time.Duration) (int, []byte, string) {
	if err := runtimeReadiness(); err != nil {
		return http.StatusServiceUnavailable, nil, "1C runtime is not configured"
	}

	select {
	case runnerJobSlots <- struct{}{}:
		defer func() { <-runnerJobSlots }()
	case <-r.Context().Done():
		return http.StatusRequestTimeout, nil, "1C execution request cancelled"
	}

	payload, err := json.Marshal(envelope)
	if err != nil {
		return http.StatusInternalServerError, nil, "failed to prepare 1C request"
	}

	ctx, cancel := context.WithTimeout(r.Context(), timeout)
	defer cancel()

	command := exec.CommandContext(ctx, driverPath())
	command.Stdin = bytes.NewReader(payload)
	command.Env = driverEnvironment()
	command.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	stdout := &limitedBuffer{max: maxDriverOutputBytes}
	stderr := &limitedBuffer{max: 64 << 10}
	command.Stdout = stdout
	command.Stderr = stderr

	if err := command.Start(); err != nil {
		return http.StatusServiceUnavailable, nil, "failed to start 1C execution driver"
	}
	err = command.Wait()
	// The driver is a dedicated process-group leader. Always kill the group after
	// it exits so a timed-out/crashed xvfb-run/1cv8 descendant cannot survive in
	// the long-lived runner container. ESRCH is expected when the group is empty.
	if command.Process != nil {
		_ = syscall.Kill(-command.Process.Pid, syscall.SIGKILL)
	}
	if ctx.Err() == context.DeadlineExceeded {
		return http.StatusGatewayTimeout, nil, "1C execution timed out"
	}
	if err != nil {
		log.Printf("onec driver failed: %v; stderr=%s", err, strings.TrimSpace(stderr.String()))
		return http.StatusServiceUnavailable, nil, "1C execution driver failed"
	}

	body := bytes.TrimSpace([]byte(stdout.String()))
	if len(body) == 0 || !json.Valid(body) {
		return http.StatusServiceUnavailable, nil, "1C execution driver returned an invalid result"
	}
	return http.StatusOK, body, ""
}

func runHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	var request runRequest
	if !decodeJSON(w, r, &request) {
		return
	}
	if err := validateCode(request.Code); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"status": "BadRequest", "error": err.Error()})
		return
	}
	if err := verifyPolicyAttestation("onec", "standard", request.Code, request.Attestation); err != nil {
		writeJSON(w, http.StatusForbidden, map[string]any{"status": "PolicyFailed", "error": "invalid code analyzer attestation"})
		return
	}

	status, body, message := runDriver(r, driverEnvelope{
		Schema: "taskforge-onec-driver-v1", Mode: "run", Code: request.Code, Input: request.Input,
		TimeLimitMs: request.TimeLimitMs, MemoryLimitMb: request.MemoryLimitMb,
	}, requestTimeout(request.TimeLimitMs))
	if status != http.StatusOK {
		writeJSON(w, status, map[string]any{"status": "JudgeUnavailable", "error": message})
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(body)
}

func runTestsHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	var request testsRequest
	if !decodeJSON(w, r, &request) {
		return
	}
	if err := validateCode(request.Code); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"status": "BadRequest", "error": err.Error()})
		return
	}
	if len(request.Tests) == 0 || len(request.Tests) > maxTestsPerRequest {
		writeJSON(w, http.StatusBadRequest, map[string]any{"status": "BadRequest", "error": "tests count is invalid"})
		return
	}
	if err := verifyPolicyAttestation("onec", "standard", request.Code, request.Attestation); err != nil {
		writeJSON(w, http.StatusForbidden, map[string]any{"status": "PolicyFailed", "error": "invalid code analyzer attestation"})
		return
	}

	status, body, message := runDriver(r, driverEnvelope{
		Schema: "taskforge-onec-driver-v1", Mode: "tests", Code: request.Code, Tests: request.Tests,
		TimeLimitMs: request.TimeLimitMs, MemoryLimitMb: request.MemoryLimitMb,
	}, requestTimeout(request.TimeLimitMs))
	if status != http.StatusOK {
		writeJSON(w, status, map[string]any{"status": "JudgeUnavailable", "error": message})
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(body)
}

func main() {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "runner": "onec", "runtime": "external-driver"})
	})
	mux.HandleFunc("/ready", func(w http.ResponseWriter, _ *http.Request) {
		if err := runtimeReadiness(); err != nil {
			writeJSON(w, http.StatusServiceUnavailable, map[string]any{"status": "not_ready", "runner": "onec", "reason": err.Error()})
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"status": "ready", "runner": "onec"})
	})
	mux.HandleFunc("/run", runHandler)
	mux.HandleFunc("/run-tests", runTestsHandler)
	mux.HandleFunc("/run/tests", runTestsHandler)

	port := 8080
	if raw := strings.TrimSpace(os.Getenv("PORT")); raw != "" {
		if parsed, err := strconv.Atoi(raw); err == nil && parsed > 0 && parsed < 65536 {
			port = parsed
		}
	}
	address := fmt.Sprintf(":%d", port)
	log.Printf("onec-runner listening on %s; driver=%s", address, driverPath())
	server := &http.Server{Addr: address, Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	log.Fatal(server.ListenAndServe())
}
