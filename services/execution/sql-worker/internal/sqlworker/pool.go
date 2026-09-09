package sqlworker

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

type PoolOptions struct {
	MaxReady, MaxMaterializations                        int
	IdleReady, IdleGolden, RetryDelay, AcquireWait, Tick time.Duration
}

func DefaultPoolOptions() PoolOptions {
	return PoolOptions{6, 4, 120 * time.Second, 600 * time.Second, 5 * time.Second, 20 * time.Second, time.Second}
}

type poolEntry struct {
	key, manifest                    string
	adapter                          EngineAdapter
	payload                          Payload
	ready                            []Sandbox
	preparing, retired               bool
	creating, borrowers, quarantined int
	failure                          error
	lastUsed, lastFailure            time.Time
	hits                             []time.Time
	changed                          chan struct{}
}
type quarantine struct {
	entry   *poolEntry
	sandbox Sandbox
	after   time.Time
}
type retirement struct {
	entry *poolEntry
	ready []Sandbox
	after time.Time
}
type Pool struct {
	mu         sync.Mutex
	entries    map[string]*poolEntry
	retiring   map[string]bool
	disabled   map[string]bool
	quarantine []quarantine
	retired    []retirement
	options    PoolOptions
	metrics    *Metrics
	wake       chan struct{}
	ctx        context.Context
	cancel     context.CancelFunc
	bg         sync.WaitGroup
	closed     bool
}
type Lease struct {
	pool    *Pool
	entry   *poolEntry
	Sandbox Sandbox
	done    atomic.Bool
}

func NewPool(options PoolOptions, metrics *Metrics) *Pool {
	defaults := DefaultPoolOptions()
	if options.MaxReady < 0 || options.MaxReady > 32 {
		options.MaxReady = defaults.MaxReady
	}
	if options.MaxMaterializations < 1 || options.MaxMaterializations > 16 {
		options.MaxMaterializations = defaults.MaxMaterializations
	}
	if options.IdleReady <= 0 {
		options.IdleReady = defaults.IdleReady
	}
	if options.IdleGolden <= options.IdleReady {
		options.IdleGolden = defaults.IdleGolden
	}
	if options.RetryDelay <= 0 {
		options.RetryDelay = defaults.RetryDelay
	}
	if options.AcquireWait <= 0 {
		options.AcquireWait = defaults.AcquireWait
	}
	if options.Tick <= 0 {
		options.Tick = defaults.Tick
	}
	ctx, cancel := context.WithCancel(context.Background())
	p := &Pool{entries: map[string]*poolEntry{}, retiring: map[string]bool{}, disabled: map[string]bool{}, options: options, metrics: metrics, wake: make(chan struct{}, 1), ctx: ctx, cancel: cancel}
	// Two bounded workers perform replenishment and janitor work. No unbounded
	// goroutine is launched per cold dataset or lost backend.
	for i := 0; i < 2; i++ {
		p.bg.Add(1)
		go p.background()
	}
	return p
}
func (p *Pool) notify(e *poolEntry) {
	close(e.changed)
	e.changed = make(chan struct{})
	select {
	case p.wake <- struct{}{}:
	default:
	}
}
func cachePayload(v Payload) Payload {
	return Payload{Profile: v.Profile, MaterializationKey: v.MaterializationKey, Definition: v.Definition, Seed: v.Seed, EngineOverrides: v.EngineOverrides}
}
func poolIdentity(a EngineAdapter, v Payload) (string, string, error) {
	if !hashRE.MatchString(v.MaterializationKey) || v.Profile.Engine != a.Engine() {
		return "", "", Unavailable("Invalid pool materialization identity.")
	}
	// Recheck the contents even when an upstream caller supplied an existing key.
	hash, e := ContentHash(map[string]any{"definition": v.Definition, "seed": v.Seed, "engineOverrides": v.EngineOverrides, "profile": v.Profile.Fingerprint})
	return a.Engine() + ":" + v.Profile.Fingerprint + ":" + v.MaterializationKey, hash, e
}
func (p *Pool) target(e *poolEntry, now time.Time) int {
	if now.Sub(e.lastUsed) > p.options.IdleReady {
		return 0
	}
	cut := now.Add(-time.Minute)
	i := 0
	for i < len(e.hits) && e.hits[i].Before(cut) {
		i++
	}
	e.hits = e.hits[i:]
	if len(e.hits) >= 12 {
		return 4
	}
	if len(e.hits) >= 4 {
		return 2
	}
	return 1
}
func (p *Pool) readyCount() int {
	n := 0
	for _, e := range p.entries {
		n += len(e.ready) + e.creating
	}
	return n
}
func (p *Pool) retireLocked(e *poolEntry) {
	delete(p.entries, e.key)
	p.retiring[e.key] = true
	e.retired = true
	p.retired = append(p.retired, retirement{entry: e, ready: e.ready})
	e.ready = nil
	p.notify(e)
}
func (p *Pool) ensure(ctx context.Context, a EngineAdapter, v Payload) (*poolEntry, error) {
	key, manifest, err := poolIdentity(a, v)
	if err != nil {
		return nil, err
	}
	for {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		p.mu.Lock()
		if p.closed || p.disabled[a.Engine()] {
			p.mu.Unlock()
			return nil, Unavailable("The SQL pool is stopping.")
		}
		if len(p.quarantine) >= 16 || len(p.retired) >= 8 {
			p.mu.Unlock()
			return nil, Unavailable("SQL cleanup backlog is temporarily full.")
		}
		if p.retiring[key] {
			p.mu.Unlock()
			return nil, Unavailable("This materialization is being safely retired; retry later.")
		}
		if e := p.entries[key]; e != nil {
			if e.manifest != manifest {
				p.mu.Unlock()
				return nil, Unavailable("Conflicting materialization content for an existing key.")
			}
			if e.preparing {
				changed := e.changed
				p.mu.Unlock()
				select {
				case <-ctx.Done():
					return nil, ctx.Err()
				case <-changed:
					continue
				}
			}
			if e.failure != nil {
				failure := e.failure
				p.mu.Unlock()
				return nil, failure
			}
			p.mu.Unlock()
			return e, nil
		}
		count := 0
		var oldest *poolEntry
		for _, e := range p.entries {
			if e.adapter.Engine() == a.Engine() {
				count++
				if !e.preparing && e.creating == 0 && e.borrowers == 0 && e.quarantined == 0 && (oldest == nil || e.lastUsed.Before(oldest.lastUsed)) {
					oldest = e
				}
			}
		}
		if count >= p.options.MaxMaterializations {
			if oldest == nil {
				p.mu.Unlock()
				return nil, Unavailable("All SQL materialization slots are busy; retry later.")
			}
			p.retireLocked(oldest)
		}
		e := &poolEntry{key: key, manifest: manifest, adapter: a, payload: cachePayload(v), preparing: true, lastUsed: time.Now(), changed: make(chan struct{})}
		p.entries[key] = e
		p.mu.Unlock()
		done := p.metrics.Measure("sql_materialization", a.Engine())
		err = a.Prepare(ctx, v)
		done()
		p.mu.Lock()
		e.preparing = false
		e.failure = err
		p.notify(e)
		if err != nil {
			p.metrics.Inc("sql_materialization_failures", a.Engine(), 1)
			e.lastFailure = time.Now()
		}
		p.mu.Unlock()
		return e, err
	}
}
func (p *Pool) Warm(ctx context.Context, a EngineAdapter, v Payload) error {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	e, err := p.ensure(ctx, a, v)
	if err == nil {
		p.mu.Lock()
		if !e.retired {
			e.lastUsed = time.Now()
			p.notify(e)
		}
		p.mu.Unlock()
	}
	return err
}
func (p *Pool) Acquire(ctx context.Context, a EngineAdapter, v Payload) (*Lease, error) {
	done := p.metrics.Measure("sql_sandbox_acquire", a.Engine())
	defer done()
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	e, err := p.ensure(ctx, a, v)
	if err != nil {
		return nil, err
	}
	wait := time.NewTimer(p.options.AcquireWait)
	defer wait.Stop()
	for {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		p.mu.Lock()
		if p.closed || e.retired {
			p.mu.Unlock()
			return nil, Unavailable("The SQL cache changed while acquiring a sandbox.")
		}
		if e.failure != nil {
			failure := e.failure
			p.mu.Unlock()
			return nil, failure
		}
		if len(e.ready) > 0 {
			s := e.ready[len(e.ready)-1]
			e.ready = e.ready[:len(e.ready)-1]
			e.borrowers++
			e.lastUsed = time.Now()
			e.hits = append(e.hits, e.lastUsed)
			if len(e.hits) > 12 {
				e.hits = e.hits[len(e.hits)-12:]
			}
			p.notify(e)
			p.mu.Unlock()
			return &Lease{pool: p, entry: e, Sandbox: s}, nil
		}
		if e.creating > 0 {
			changed := e.changed
			p.mu.Unlock()
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-wait.C:
				return nil, Unavailable("The SQL ready pool is temporarily exhausted.")
			case <-changed:
				continue
			}
		}
		if !e.lastFailure.IsZero() && time.Since(e.lastFailure) < p.options.RetryDelay {
			p.mu.Unlock()
			return nil, Unavailable("The SQL sandbox engine is recovering.")
		}
		// At most ONE foreground create, never a synchronous full-pool refill.
		e.creating++
		e.borrowers++
		e.lastUsed = time.Now()
		e.hits = append(e.hits, e.lastUsed)
		if len(e.hits) > 12 {
			e.hits = e.hits[len(e.hits)-12:]
		}
		p.mu.Unlock()
		p.metrics.Inc("sql_pool_cold_create", a.Engine(), 1)
		sandbox, createErr := a.Create(ctx, e.payload)
		p.mu.Lock()
		e.creating--
		if createErr != nil {
			e.borrowers--
			e.lastFailure = time.Now()
			if sandbox.ID != "" {
				e.quarantined++
				p.quarantine = append(p.quarantine, quarantine{entry: e, sandbox: sandbox, after: time.Now().Add(p.options.RetryDelay)})
			}
		}
		p.notify(e)
		p.mu.Unlock()
		if createErr != nil {
			return nil, createErr
		}
		lease := &Lease{pool: p, entry: e, Sandbox: sandbox}
		if err := ctx.Err(); err != nil {
			_ = lease.Release()
			return nil, err
		}
		return lease, nil
	}
}
func (l *Lease) Release() error {
	if l == nil || l.done.Swap(true) {
		return nil
	}
	p, e := l.pool, l.entry
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	done := p.metrics.Measure("sql_cleanup", e.adapter.Engine())
	err := e.adapter.Destroy(ctx, l.Sandbox)
	done()
	p.mu.Lock()
	e.borrowers--
	if err != nil {
		e.quarantined++
		p.quarantine = append(p.quarantine, quarantine{entry: e, sandbox: l.Sandbox, after: time.Now().Add(p.options.RetryDelay)})
		p.metrics.Inc("sql_cleanup_failures", e.adapter.Engine(), 1)
	}
	p.notify(e)
	p.mu.Unlock()
	if err != nil {
		return Unavailable("The used sandbox is quarantined because destruction failed.")
	}
	return nil
}
func (l *Lease) Kill(ctx context.Context) error { return l.entry.adapter.Kill(ctx, l.Sandbox) }
func (p *Pool) background() {
	defer p.bg.Done()
	tick := time.NewTicker(p.options.Tick)
	defer tick.Stop()
	for {
		select {
		case <-p.ctx.Done():
			return
		case <-p.wake:
		case <-tick.C:
		}
		for i := 0; i < 4 && p.ctx.Err() == nil; i++ {
			if !p.maintenance() {
				break
			}
		}
	}
}
func (p *Pool) maintenance() bool {
	now := time.Now()
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()
		return false
	}
	p.updateMetricsLocked()
	// Retired materializations and quarantined sandboxes are not returned to READY.
	for i, item := range p.quarantine {
		if !item.after.After(now) {
			p.quarantine = append(p.quarantine[:i], p.quarantine[i+1:]...)
			p.mu.Unlock()
			ctx, cancel := context.WithTimeout(p.ctx, 15*time.Second)
			err := item.entry.adapter.Destroy(ctx, item.sandbox)
			cancel()
			p.mu.Lock()
			if err != nil {
				item.after = time.Now().Add(p.options.RetryDelay)
				p.quarantine = append(p.quarantine, item)
			} else {
				item.entry.quarantined--
			}
			p.notify(item.entry)
			p.mu.Unlock()
			return true
		}
	}
	for i, item := range p.retired {
		if !item.after.After(now) {
			p.retired = append(p.retired[:i], p.retired[i+1:]...)
			p.mu.Unlock()
			ctx, cancel := context.WithTimeout(p.ctx, 20*time.Second)
			err := p.cleanupRetirement(ctx, &item)
			cancel()
			p.mu.Lock()
			if err != nil {
				item.after = time.Now().Add(p.options.RetryDelay)
				p.retired = append(p.retired, item)
			} else {
				delete(p.retiring, item.entry.key)
			}
			p.mu.Unlock()
			return true
		}
	}
	for _, e := range p.entries {
		if e.preparing || e.creating > 0 || e.borrowers > 0 || e.quarantined > 0 {
			continue
		}
		if (e.failure != nil && now.Sub(e.lastFailure) >= p.options.RetryDelay) || now.Sub(e.lastUsed) > p.options.IdleGolden {
			p.retireLocked(e)
			p.mu.Unlock()
			return true
		}
		target := p.target(e, now)
		if len(e.ready) > target {
			sandbox := e.ready[len(e.ready)-1]
			e.ready = e.ready[:len(e.ready)-1]
			e.quarantined++
			p.quarantine = append(p.quarantine, quarantine{entry: e, sandbox: sandbox})
			p.notify(e)
			p.mu.Unlock()
			return true
		}
	}
	if p.readyCount() >= p.options.MaxReady {
		p.mu.Unlock()
		return false
	}
	// Oldest undersupplied hot entry first avoids one dataset monopolizing replenishment.
	var selected *poolEntry
	for _, e := range p.entries {
		if p.disabled[e.adapter.Engine()] || e.preparing || e.failure != nil || e.creating > 0 || e.quarantined > 0 || now.Sub(e.lastFailure) < p.options.RetryDelay {
			continue
		}
		if len(e.ready) >= p.target(e, now) {
			continue
		}
		if selected == nil || e.lastUsed.Before(selected.lastUsed) {
			selected = e
		}
	}
	if selected == nil {
		p.mu.Unlock()
		return false
	}
	e := selected
	e.creating++
	p.mu.Unlock()
	ctx, cancel := context.WithTimeout(p.ctx, 30*time.Second)
	sandbox, err := e.adapter.Create(ctx, e.payload)
	cancel()
	p.mu.Lock()
	e.creating--
	if err != nil || p.closed {
		e.lastFailure = time.Now()
		if sandbox.ID != "" {
			e.quarantined++
			p.quarantine = append(p.quarantine, quarantine{entry: e, sandbox: sandbox, after: time.Now().Add(p.options.RetryDelay)})
		}
	} else {
		e.ready = append(e.ready, sandbox)
	}
	p.notify(e)
	p.mu.Unlock()
	return true
}
func (p *Pool) cleanupRetirement(ctx context.Context, item *retirement) error {
	for len(item.ready) > 0 {
		if e := item.entry.adapter.Destroy(ctx, item.ready[0]); e != nil {
			return e
		}
		item.ready = item.ready[1:]
	}
	return item.entry.adapter.DropMaterialization(ctx, item.entry.payload.MaterializationKey)
}
func (p *Pool) updateMetricsLocked() {
	for _, engine := range []string{"sqlite", "postgresql", "mysql"} {
		ready, leased, creating, quarantined := 0, 0, 0, 0
		for _, e := range p.entries {
			if e.adapter.Engine() == engine {
				ready += len(e.ready)
				leased += e.borrowers
				creating += e.creating
				quarantined += e.quarantined
			}
		}
		for name, value := range map[string]int{"sql_pool_ready": ready, "sql_pool_leased": leased, "sql_pool_replenishing": creating, "sql_pool_quarantined": quarantined} {
			p.metrics.Set(name, engine, float64(value))
		}
	}
}
func (p *Pool) Stats() map[string]int {
	p.mu.Lock()
	defer p.mu.Unlock()
	out := map[string]int{"materializations": len(p.entries), "retired": len(p.retired), "quarantined": len(p.quarantine)}
	for _, e := range p.entries {
		out["ready"] += len(e.ready)
		out["leased"] += e.borrowers
		out["creating"] += e.creating
	}
	return out
}

// Close is called after the coordinator has stopped and joined its job/warmup
// goroutines. It never drops a golden database with a live lease.
func (p *Pool) Close(ctx context.Context) error {
	p.mu.Lock()
	p.closed = true
	p.cancel()
	p.mu.Unlock()
	p.bg.Wait()
	p.mu.Lock()
	for _, e := range p.entries {
		if e.borrowers > 0 || e.creating > 0 || e.preparing {
			p.mu.Unlock()
			return fmt.Errorf("pool still has active operations")
		}
	}
	retired := p.retired
	p.retired = nil
	quarantined := p.quarantine
	p.quarantine = nil
	for _, e := range p.entries {
		retired = append(retired, retirement{entry: e, ready: e.ready})
	}
	p.entries = map[string]*poolEntry{}
	p.mu.Unlock()
	var first error
	for _, item := range quarantined {
		if e := item.entry.adapter.Destroy(ctx, item.sandbox); e != nil && first == nil {
			first = e
		}
	}
	for i := range retired {
		if e := p.cleanupRetirement(ctx, &retired[i]); e != nil && first == nil {
			first = e
		}
	}
	return first
}

// Reset pauses only this engine's cache. Other engines remain available. Cleanup
// runs in the janitor and a namespace reset cannot race an active lease/create.
func (p *Pool) Reset(a EngineAdapter) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.disabled[a.Engine()] = true
	for _, e := range p.entries {
		if e.adapter.Engine() == a.Engine() && (e.preparing || e.borrowers > 0 || e.creating > 0 || e.quarantined > 0) {
			return false
		}
	}
	found := false
	for _, e := range p.entries {
		if e.adapter.Engine() == a.Engine() {
			p.retireLocked(e)
			found = true
		}
	}
	if found {
		return false
	}
	for key := range p.retiring {
		if strings.HasPrefix(key, a.Engine()+":") {
			return false
		}
	}
	return true
}
func (p *Pool) Resume(a EngineAdapter) {
	p.mu.Lock()
	defer p.mu.Unlock()
	delete(p.disabled, a.Engine())
	select {
	case p.wake <- struct{}{}:
	default:
	}
}
