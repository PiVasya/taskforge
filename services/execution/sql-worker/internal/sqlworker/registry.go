package sqlworker

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"taskforge/sqlworker/internal/native"
	"time"
)

// Fingerprints change when the executable OR its loaded native runtime changes.
// This does not claim to attest a remote image: server image digests are supplied
// by the deployment preflight and server versions are checked on each connection.
func ExecutorFingerprint() (string, error) {
	path, e := os.Executable()
	if e != nil {
		return "", e
	}
	files := map[string]string{"executable": path}
	maps, e := os.ReadFile("/proc/self/maps")
	if e != nil {
		return "", e
	}
	for _, line := range strings.Split(string(maps), "\n") {
		parts := strings.Fields(line)
		if len(parts) < 6 {
			continue
		}
		p := strings.Join(parts[5:], " ")
		if strings.HasPrefix(p, "/") && strings.Contains(filepath.Base(p), ".so") {
			if strings.HasSuffix(p, " (deleted)") {
				return "", fmt.Errorf("loaded runtime library was replaced")
			}
			files["library:"+filepath.Base(p)] = p
			// MySQL authentication plugins may be loaded lazily at first connect.
			if strings.Contains(filepath.Base(p), "libmariadb") {
				for _, dir := range []string{"libmariadb3/plugin", "mariadb19/plugin", "mariadb/plugin"} {
					plugins, _ := filepath.Glob(filepath.Join(filepath.Dir(p), dir, "*.so"))
					for _, plugin := range plugins {
						files["plugin:"+filepath.Base(plugin)] = plugin
					}
				}
			}
		}
	}
	hashes := map[string]string{}
	for name, path := range files {
		f, e := os.Open(path)
		if e != nil {
			return "", e
		}
		h := sha256.New()
		_, e = io.Copy(h, f)
		f.Close()
		if e != nil {
			return "", e
		}
		hashes[name] = hex.EncodeToString(h.Sum(nil))
	}
	return ContentHash(map[string]any{"implementation": ImplementationVersion, "os": runtime.GOOS, "arch": runtime.GOARCH, "libraries": native.LibraryVersions(), "files": hashes, "goUnicodePolicy": "default-fold"})
}

type profileClient interface {
	Register(context.Context, Registration) (Profile, error)
}
type runtimeEntry struct {
	adapter            EngineAdapter
	profile            *Profile
	ready, initialized bool
	active             int
	epoch              uint64
	errorClass         string
}
type Registry struct {
	mu        sync.Mutex
	refreshMu sync.Mutex
	entries   []*runtimeEntry
	client    profileClient
	pool      *Pool
	metrics   *Metrics
}

func NewRegistry(adapters []EngineAdapter, client profileClient, pool *Pool, metrics *Metrics) *Registry {
	r := &Registry{client: client, pool: pool, metrics: metrics}
	for _, a := range adapters {
		r.entries = append(r.entries, &runtimeEntry{adapter: a, errorClass: "initializing"})
	}
	return r
}
func (r *Registry) Refresh(ctx context.Context) {
	r.refreshMu.Lock()
	defer r.refreshMu.Unlock()
	for _, entry := range r.entries {
		if ctx.Err() != nil {
			return
		}
		r.mu.Lock()
		initialized, epoch, active := entry.initialized, entry.epoch, entry.active
		r.mu.Unlock()
		if !initialized {
			if active > 0 || !r.pool.Reset(entry.adapter) {
				continue
			}
			deadline, cancel := context.WithTimeout(ctx, 30*time.Second)
			e := entry.adapter.Startup(deadline)
			cancel()
			if e != nil {
				r.setFailure(entry, epoch, "engine-bootstrap", true)
				continue
			}
			r.mu.Lock()
			if entry.epoch != epoch {
				r.mu.Unlock()
				continue
			}
			entry.initialized = true
			r.mu.Unlock()
		}
		deadline, cancel := context.WithTimeout(ctx, 20*time.Second)
		registration, e := entry.adapter.Registration(deadline)
		if e != nil && deadline.Err() == nil {
			// Registration is read-only and opens a fresh admin connection. One
			// immediate retry absorbs short PostgreSQL/MySQL connection churn without
			// throwing away a healthy materialization cache. Persistent failures still
			// take the engine out of rotation below.
			slog.Warn("sql_engine_probe_retry", "engine", entry.adapter.Engine(), "error_class", fmt.Sprintf("%T", e))
			if sleep(deadline, 200*time.Millisecond) {
				registration, e = entry.adapter.Registration(deadline)
			}
		}
		cancel()
		if e != nil {
			r.setFailure(entry, epoch, "engine-probe", true)
			continue
		}
		profile, e := r.client.Register(ctx, registration)
		if e != nil {
			r.setFailure(entry, epoch, "control-plane", false)
			continue
		}
		r.mu.Lock()
		if entry.epoch != epoch {
			r.mu.Unlock()
			continue
		}
		if entry.profile != nil && entry.profile.Fingerprint != profile.Fingerprint && initialized {
			entry.ready = false
			entry.initialized = false
			entry.epoch++
			entry.errorClass = "runtime-changed"
			r.mu.Unlock()
			continue
		}
		entry.profile = &profile
		entry.ready = true
		entry.errorClass = ""
		r.pool.Resume(entry.adapter)
		r.mu.Unlock()
		r.metrics.Set("sql_engine_available", entry.adapter.Engine(), 1)
	}
}
func (r *Registry) setFailure(entry *runtimeEntry, epoch uint64, class string, reset bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if entry.epoch != epoch {
		return
	}
	was := entry.ready
	entry.ready = false
	entry.errorClass = class
	if reset {
		entry.initialized = false
	}
	r.metrics.Set("sql_engine_available", entry.adapter.Engine(), 0)
	if was {
		slog.Warn("sql_engine_unavailable", "engine", entry.adapter.Engine(), "error_class", class)
	}
}
func (r *Registry) Invalidate(fingerprint string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, e := range r.entries {
		if e.profile != nil && e.profile.Fingerprint == fingerprint {
			e.ready = false
			e.initialized = false
			e.epoch++
			e.errorClass = "cache-recovery"
			r.metrics.Set("sql_engine_available", e.adapter.Engine(), 0)
		}
	}
}
func (r *Registry) Targets() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := []string{}
	for _, e := range r.entries {
		if e.ready && e.profile != nil {
			out = append(out, e.profile.Fingerprint)
		}
	}
	sort.Strings(out)
	return out
}
func (r *Registry) Acquire(fingerprint string) (EngineAdapter, Profile, func(), error) {
	r.mu.Lock()
	for _, e := range r.entries {
		if e.ready && e.profile != nil && e.profile.Fingerprint == fingerprint {
			e.active++
			profile := *e.profile
			adapter := e.adapter
			r.mu.Unlock()
			r.metrics.Inc("sql_worker_inflight", adapter.Engine(), 1)
			var once sync.Once
			release := func() {
				once.Do(func() {
					r.mu.Lock()
					e.active--
					r.mu.Unlock()
					r.metrics.Inc("sql_worker_inflight", adapter.Engine(), -1)
				})
			}
			return adapter, profile, release, nil
		}
	}
	r.mu.Unlock()
	return nil, Profile{}, nil, Unavailable("This exact SQL runtime profile is not currently ready.")
}
func (r *Registry) Status() []map[string]any {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := []map[string]any{}
	for _, e := range r.entries {
		var profile any
		if e.profile != nil {
			profile = e.profile.Fingerprint
		}
		out = append(out, map[string]any{"engine": e.adapter.Engine(), "ready": e.ready, "active": e.active, "profile": profile, "errorClass": e.errorClass})
	}
	return out
}

// cloneJSON prevents caller-owned maps from mutating a registered wire profile.
func cloneJSON[T any](v T) (T, error) {
	var out T
	b, e := json.Marshal(v)
	if e == nil {
		e = DecodeStrict(b, &out)
	}
	return out, e
}
