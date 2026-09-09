package sqlworker

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/exec"
	"sync"
	"sync/atomic"
	"taskforge/sqlworker/internal/wakeup"
	"time"
)

type QueueClient interface {
	Capabilities(context.Context, string, []string, int) error
	Claim(context.Context, string, []string) (*Job, error)
	Renew(context.Context, string, Job) error
	Complete(context.Context, string, Job, Outcome, time.Duration) error
	Warmup(context.Context, string) ([]Payload, error)
}
type Worker struct {
	Config     Config
	Client     QueueClient
	Registry   *Registry
	Pool       *Pool
	Runner     *JobRunner
	Metrics    *Metrics
	namespace  *os.File
	wake       chan struct{}
	slots      chan struct{}
	jobs       sync.WaitGroup
	background sync.WaitGroup
	lastTick   atomic.Int64
	stopping   atomic.Bool
}

func NewWorker(cfg Config) (*Worker, error) {
	client, e := NewInternalClient(cfg.TasksURL, cfg.ExecutionURL, cfg.InternalKey)
	if e != nil {
		return nil, e
	}
	lock, e := lockNamespace(cfg.Cache, cfg.WorkerID)
	if e != nil {
		client.Close()
		return nil, e
	}
	closeResources := func() { lock.Close(); client.Close() }
	fingerprint, e := ExecutorFingerprint()
	if e != nil {
		closeResources()
		return nil, e
	}
	sqlite, e := NewSQLiteAdapter(cfg.Cache, cfg.WorkerID, fingerprint)
	if e != nil {
		closeResources()
		return nil, e
	}
	adapters := []EngineAdapter{sqlite}
	for _, engine := range cfg.Engines {
		a, e := NewServerAdapter(engine, cfg.WorkerID, fingerprint)
		if e != nil {
			closeResources()
			return nil, e
		}
		adapters = append(adapters, a)
	}
	metrics := NewMetrics()
	processes, e := NewProcessRunner(metrics)
	if e != nil {
		closeResources()
		return nil, e
	}
	// Run the all-thread policy probe before advertising ANY SQL capability.
	probeCtx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	cmd := exec.CommandContext(probeCtx, processes.Executable, "self-test-isolation")
	cmd.Env = childEnvironment()
	cmd.Stdout = &cappedBuffer{cap: 1024}
	cmd.Stderr = &cappedBuffer{cap: 8192}
	e = cmd.Run()
	cancel()
	if e != nil {
		closeResources()
		return nil, Unavailable("The mandatory Go all-thread isolation self-test failed.")
	}
	pool := NewPool(cfg.Pool, metrics)
	return &Worker{Config: cfg, Client: client, Registry: NewRegistry(adapters, client, pool, metrics), Pool: pool, Runner: &JobRunner{pool, processes, metrics}, Metrics: metrics, namespace: lock, wake: make(chan struct{}, 1), slots: make(chan struct{}, cfg.Concurrency)}, nil
}
func (w *Worker) signal() {
	select {
	case w.wake <- struct{}{}:
	default:
	}
}
func sleep(ctx context.Context, duration time.Duration) bool {
	timer := time.NewTimer(duration)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-timer.C:
		return true
	}
}
func (w *Worker) maintain(ctx context.Context) {
	defer w.background.Done()
	warmed := map[string]bool{}
	for ctx.Err() == nil {
		w.Registry.Refresh(ctx)
		targets := w.Registry.Targets()
		w.signal()
		for _, target := range targets {
			if warmed[target] {
				continue
			}
			manifests, e := w.Client.Warmup(ctx, target)
			if e != nil {
				continue
			}
			success := true
			for _, p := range manifests {
				if ctx.Err() != nil {
					return
				}
				a, profile, release, e := w.Registry.Acquire(target)
				if e != nil {
					success = false
					break
				}
				if !sameJSON(p.Profile, profile) {
					release()
					success = false
					break
				}
				e = w.Pool.Warm(ctx, a, p)
				release()
				if e != nil {
					success = false
					break
				}
			}
			if success {
				warmed[target] = true
			}
		}
		if !sleep(ctx, 10*time.Second) {
			return
		}
	}
}
func (w *Worker) heartbeat(ctx context.Context) {
	defer w.background.Done()
	for ctx.Err() == nil {
		_ = w.Client.Capabilities(ctx, w.Config.WorkerID, w.Registry.Targets(), w.Config.Concurrency)
		if !sleep(ctx, 5*time.Second) {
			return
		}
	}
}
func (w *Worker) execute(ctx context.Context, job Job) {
	defer w.jobs.Done()
	defer func() { <-w.slots; w.signal() }()
	leaseCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	started := time.Now()
	safetyUntil := time.Now().Add(28 * time.Second)
	if job.LeaseExpiresAt != nil && job.LeaseExpiresAt.Add(-5*time.Second).Before(safetyUntil) {
		safetyUntil = job.LeaseExpiresAt.Add(-5 * time.Second)
	}
	var confirmedUntil atomic.Int64
	confirmedUntil.Store(safetyUntil.UnixNano())
	var watchers sync.WaitGroup
	watchers.Add(2)
	go func() {
		defer watchers.Done()
		tick := time.NewTicker(8 * time.Second)
		defer tick.Stop()
		for {
			select {
			case <-leaseCtx.Done():
				return
			case <-tick.C:
				e := w.Client.Renew(leaseCtx, w.Config.WorkerID, job)
				if errors.Is(e, ErrLostLease) {
					cancel()
					return
				}
				if e == nil {
					confirmedUntil.Store(time.Now().Add(28 * time.Second).UnixNano())
				}
			}
		}
	}()
	go func() {
		defer watchers.Done()
		tick := time.NewTicker(250 * time.Millisecond)
		defer tick.Stop()
		for {
			select {
			case <-leaseCtx.Done():
				return
			case <-tick.C:
				if time.Now().UnixNano() >= confirmedUntil.Load() {
					cancel()
					return
				}
			}
		}
	}()
	defer func() { cancel(); watchers.Wait() }()
	a, profile, release, e := w.Registry.Acquire(job.Payload.Profile.Fingerprint)
	out := Outcome{Verdict: "JudgeUnavailable", Result: map[string]any{"error": Unavailable("The exact SQL runtime is no longer available.").PublicError}}
	if e == nil {
		out, e = w.Runner.Run(leaseCtx, job, a, profile)
		release()
	}
	if leaseCtx.Err() != nil || errors.Is(e, ErrLostLease) {
		slog.Info("sql_lease_lost", "job", job.ID, "engine", job.Payload.Profile.Engine)
		return
	}
	if e != nil {
		slog.Warn("sql_job_unacknowledged", "job", job.ID, "error_class", "execution")
		return
	}
	if value, ok := out.Result["error"]; ok && value != nil {
		raw, _ := json.Marshal(value)
		var problem PublicError
		if DecodeStrict(raw, &problem) == nil && problem.Code == "SQL_CACHE_RECOVERY_REQUIRED" {
			w.Registry.Invalidate(job.Payload.Profile.Fingerprint)
		}
	}
	duration := time.Since(started)
	until := time.Now().Add(45 * time.Second)
	for leaseCtx.Err() == nil {
		e = w.Client.Complete(leaseCtx, w.Config.WorkerID, job, out, duration)
		if e == nil {
			slog.Info("sql_attempt", "job", job.ID, "engine", job.Payload.Profile.Engine, "materialization", job.Payload.MaterializationKey, "verdict", out.Verdict, "duration_ms", duration.Milliseconds())
			return
		}
		if errors.Is(e, ErrLostLease) || time.Now().After(until) {
			break
		}
		if !sleep(leaseCtx, time.Second) {
			break
		}
	}
	// A lost completion ACK never re-executes student SQL inside this worker.
	// The API owns terminal state and expired-lease recovery.
	slog.Warn("sql_job_unacknowledged", "job", job.ID, "error_class", "completion-or-lease")
}
func (w *Worker) HealthHandler() http.Handler {
	return http.HandlerFunc(func(rw http.ResponseWriter, req *http.Request) {
		if req.Method != "GET" {
			rw.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		switch req.URL.Path {
		case "/metrics":
			rw.Header().Set("Content-Type", "text/plain; version=0.0.4")
			_, _ = rw.Write([]byte(w.Metrics.Render()))
		case "/health", "/ready":
			live := !w.stopping.Load() && time.Since(time.Unix(0, w.lastTick.Load())) < 60*time.Second
			ready := len(w.Registry.Targets()) > 0
			rw.Header().Set("Content-Type", "application/json")
			if !live || (req.URL.Path == "/ready" && !ready) {
				rw.WriteHeader(http.StatusServiceUnavailable)
			}
			_ = json.NewEncoder(rw).Encode(map[string]any{"live": live, "ready": ready, "implementation": ImplementationVersion, "engines": w.Registry.Status(), "pool": w.Pool.Stats()})
		default:
			http.NotFound(rw, req)
		}
	})
}
func (w *Worker) Run(parent context.Context) (err error) {
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	defer w.namespace.Close()
	if client, ok := w.Client.(*InternalClient); ok {
		defer client.Close()
	}
	defer func() {
		w.stopping.Store(true)
		cancel()
		w.jobs.Wait()
		w.background.Wait()
		cleanup, stop := context.WithTimeout(context.Background(), 30*time.Second)
		defer stop()
		if e := w.Pool.Close(cleanup); e != nil {
			slog.Warn("sql_shutdown_cleanup_pending", "error_class", "local-cache")
		}
	}()
	listener, e := net.Listen("tcp", fmt.Sprintf("0.0.0.0:%d", w.Config.HealthPort))
	if e != nil {
		return e
	}
	w.lastTick.Store(time.Now().UnixNano())
	server := &http.Server{Handler: w.HealthHandler(), ReadHeaderTimeout: 2 * time.Second, ReadTimeout: 5 * time.Second, WriteTimeout: 5 * time.Second, IdleTimeout: 10 * time.Second, MaxHeaderBytes: 4096}
	served := make(chan error, 1)
	go func() { served <- server.Serve(listener) }()
	defer func() {
		stopCtx, stop := context.WithTimeout(context.Background(), 5*time.Second)
		defer stop()
		_ = server.Shutdown(stopCtx)
	}()
	w.background.Add(3)
	go w.maintain(ctx)
	go w.heartbeat(ctx)
	go func() {
		defer w.background.Done()
		wakeup.Listen(ctx, w.Config.Broker, func() { w.Metrics.Inc("sql_wakeup_events", "all", 1); w.signal() }, func(connected bool) {
			v := 0.
			if connected {
				v = 1
			}
			w.Metrics.Set("sql_wakeup_connected", "all", v)
		})
	}()
	slog.Info("sql_worker_started", "worker", w.Config.WorkerID, "implementation", ImplementationVersion, "concurrency", w.Config.Concurrency)
	for ctx.Err() == nil {
		w.lastTick.Store(time.Now().UnixNano())
		targets := w.Registry.Targets()
		claimed := false
		if len(targets) > 0 {
			select {
			case w.slots <- struct{}{}:
				job, e := w.Client.Claim(ctx, w.Config.WorkerID, targets)
				if e == nil && job != nil {
					w.jobs.Add(1)
					go w.execute(ctx, *job)
					claimed = true
				} else {
					<-w.slots
					if e != nil {
						w.Metrics.Inc("sql_claim_failures", "all", 1)
						if !sleep(ctx, time.Second) {
							return nil
						}
					}
				}
			default:
			}
		}
		if claimed {
			continue
		}
		timer := time.NewTimer(500 * time.Millisecond)
		select {
		case <-ctx.Done():
			timer.Stop()
			return nil
		case e := <-served:
			timer.Stop()
			if !errors.Is(e, http.ErrServerClosed) {
				return e
			}
			return nil
		case <-w.wake:
			timer.Stop()
		case <-timer.C:
		}
	}
	return nil
}
