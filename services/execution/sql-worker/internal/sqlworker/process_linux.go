//go:build linux

package sqlworker

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

type cappedBuffer struct {
	mu       sync.Mutex
	data     bytes.Buffer
	cap      int
	exceeded bool
}

func (b *cappedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if len(p) > b.cap-b.data.Len() {
		b.exceeded = true
		return 0, OutputLimit()
	}
	return b.data.Write(p)
}
func (b *cappedBuffer) Bytes() []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]byte(nil), b.data.Bytes()...)
}
func (b *cappedBuffer) Exceeded() bool { b.mu.Lock(); defer b.mu.Unlock(); return b.exceeded }

type ProcessRunner struct {
	Executable  string
	MemoryBytes int64
	Metrics     *Metrics
}

func NewProcessRunner(metrics *Metrics) (*ProcessRunner, error) {
	path, e := os.Executable()
	if e != nil {
		return nil, e
	}
	return &ProcessRunner{Executable: path, MemoryBytes: 256 << 20, Metrics: metrics}, nil
}
func childEnvironment() []string {
	return []string{"PATH=/usr/bin:/bin", "LANG=C.UTF-8", "LC_ALL=C.UTF-8", "TZ=UTC", "GOMAXPROCS=1", "GOMEMLIMIT=64MiB", "GOTRACEBACK=none"}
}
func residentBytes(pid int) int64 {
	data, e := os.ReadFile(fmt.Sprintf("/proc/%d/status", pid))
	if e != nil {
		return 0
	}
	var total int64
	for _, line := range strings.Split(string(data), "\n") {
		if strings.HasPrefix(line, "VmRSS:") || strings.HasPrefix(line, "VmSwap:") {
			parts := strings.Fields(line)
			if len(parts) >= 2 {
				n, e := strconv.ParseInt(parts[1], 10, 64)
				if e == nil {
					total += n * 1024
				}
			}
		}
	}
	return total
}
func (r *ProcessRunner) Run(ctx context.Context, lease *Lease, p Payload, source string) (Snapshot, error) {
	if !filepath.IsAbs(r.Executable) {
		return EmptySnapshot(), Unavailable("SQL helper executable must be absolute.")
	}
	req := ChildRequest{EngineVersion: p.Profile.EngineVersion, Engine: lease.Sandbox.Connection.Engine, Connection: lease.Sandbox.Connection, Source: source, Mode: p.Mode, AllowMultipleStatements: p.AllowMultipleStatements, Limits: p.Limits, StateCheck: p.StateCheck}
	raw, e := json.Marshal(req)
	if e != nil || len(raw) > 5_000_000 {
		return EmptySnapshot(), OutputLimit()
	}
	childCtx, cancel := context.WithTimeout(ctx, time.Duration(p.Limits.TimeoutMS+2000)*time.Millisecond)
	defer cancel()
	cmd := exec.Command(r.Executable, "child")
	cmd.Env = childEnvironment()
	cmd.Dir = "/"
	cmd.Stdin = bytes.NewReader(raw)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true, Pdeathsig: syscall.SIGKILL}
	out, stderr := &cappedBuffer{cap: 7_000_000}, &cappedBuffer{cap: 8192}
	cmd.Stdout = out
	cmd.Stderr = stderr
	start := time.Now()
	if e = cmd.Start(); e != nil {
		return EmptySnapshot(), Unavailable("Unable to start the isolated SQL helper.")
	}
	r.Metrics.Inc("sql_helper_inflight", req.Engine, 1)
	defer r.Metrics.Inc("sql_helper_inflight", req.Engine, -1)
	wait := make(chan error, 1)
	go func() { wait <- cmd.Wait() }()
	tick := time.NewTicker(10 * time.Millisecond)
	defer tick.Stop()
	var interrupted error
	var peak int64
	memory := r.MemoryBytes
	if memory < 64<<20 || memory > 512<<20 {
		memory = 256 << 20
	}
	kill := func() { _ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL) }
	finished := false
	for !finished {
		select {
		case e = <-wait:
			finished = true
		case <-childCtx.Done():
			interrupted = childCtx.Err()
			kill()
			e = <-wait
			finished = true
		case <-tick.C:
			usage := residentBytes(cmd.Process.Pid)
			if usage > peak {
				peak = usage
			}
			if usage > memory || out.Exceeded() || stderr.Exceeded() {
				interrupted = OutputLimit()
				kill()
				e = <-wait
				finished = true
			}
		}
	}
	engine := req.Engine
	r.Metrics.Inc("sql_helper_wall_seconds_sum", engine, time.Since(start).Seconds())
	r.Metrics.Inc("sql_helper_wall_seconds_count", engine, 1)
	r.Metrics.Set("sql_helper_last_peak_rss_bytes", engine, float64(peak))
	if cmd.ProcessState != nil {
		r.Metrics.Inc("sql_helper_cpu_seconds_sum", engine, (cmd.ProcessState.UserTime() + cmd.ProcessState.SystemTime()).Seconds())
	}
	if interrupted != nil || e != nil {
		killCtx, stop := context.WithTimeout(context.Background(), 5*time.Second)
		_ = lease.Kill(killCtx)
		stop()
		if ctx.Err() != nil {
			return EmptySnapshot(), ctx.Err()
		}
		if interrupted != nil {
			return EmptySnapshot(), NormalizeFailure(interrupted)
		}
		// Do not expose child stderr, driver traces, environment or credentials.
		var exit *exec.ExitError
		if errors.As(e, &exit) {
			if status, ok := exit.Sys().(syscall.WaitStatus); ok && status.Signal() == syscall.SIGXCPU {
				return EmptySnapshot(), Timeout()
			}
		}
		return EmptySnapshot(), Unavailable("The isolated SQL helper terminated unexpectedly.")
	}
	if out.Exceeded() || stderr.Exceeded() {
		return EmptySnapshot(), OutputLimit()
	}
	var snapshot Snapshot
	if e = DecodeStrict(out.Bytes(), &snapshot); e != nil {
		return EmptySnapshot(), Unavailable("The isolated SQL helper returned an invalid result contract.")
	}
	return snapshot, nil
}

// IsolationProbe is executed in a fresh process by the startup gate and tests.
// The second goroutine pins an already-existing OS thread BEFORE installation.
// A successful probe proves that the filter is not merely thread-local.
func IsolationProbe() error {
	ready, proceed := make(chan struct{}), make(chan struct{})
	other := make(chan error, 1)
	go func() {
		runtime.LockOSThread()
		defer runtime.UnlockOSThread()
		close(ready)
		<-proceed
		fd, e := syscall.Open("/etc/passwd", syscall.O_RDONLY, 0)
		if e == nil {
			syscall.Close(fd)
			other <- fmt.Errorf("file open allowed on another thread")
			return
		}
		if e != syscall.EPERM {
			other <- fmt.Errorf("unexpected open error")
			return
		}
		other <- nil
	}()
	<-ready
	if e := InstallIsolation(5000); e != nil {
		close(proceed)
		return e
	}
	close(proceed)
	if e := <-other; e != nil {
		return e
	}
	fd, e := syscall.Socket(syscall.AF_INET, syscall.SOCK_STREAM, 0)
	if e == nil {
		syscall.Close(fd)
		return fmt.Errorf("new network socket allowed")
	}
	if e != syscall.EPERM {
		return fmt.Errorf("unexpected socket error")
	}
	if _, e = syscall.Open("/etc/passwd", syscall.O_RDONLY, 0); e != syscall.EPERM {
		return fmt.Errorf("new file open allowed")
	}
	// Runtime still has to be functional after sealing all threads.
	done := make(chan struct{})
	go func() { time.Sleep(time.Millisecond); close(done) }()
	<-done
	return nil
}
