package sqlworker

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"
)

type fakeAdapter struct {
	mu                                                                   sync.Mutex
	prepares, creates, destroys, drops, startups, creating, peakCreating int
	delay                                                                time.Duration
	destroyFails, createFails, dropFails, probeFails                     bool
	live                                                                 map[string]bool
	blocked                                                              chan struct{}
}

func (a *fakeAdapter) Engine() string { return "sqlite" }
func (a *fakeAdapter) Startup(context.Context) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.startups++
	return nil
}
func (a *fakeAdapter) Registration(context.Context) (Registration, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.probeFails {
		return Registration{}, errors.New("probe failed")
	}
	return Registration{Engine: "sqlite", EngineVersion: "test", RuntimeDigest: "sha256:" + textHash("runtime"), AdapterVersion: AdapterVersion, Settings: map[string]any{"implementation": "test"}}, nil
}
func (a *fakeAdapter) Prepare(ctx context.Context, p Payload) error {
	a.mu.Lock()
	a.prepares++
	delay := a.delay
	a.mu.Unlock()
	if !sleep(ctx, delay) {
		return ctx.Err()
	}
	return nil
}
func (a *fakeAdapter) Create(ctx context.Context, p Payload) (Sandbox, error) {
	a.mu.Lock()
	a.creates++
	id := fmt.Sprint(a.creates)
	a.creating++
	if a.creating > a.peakCreating {
		a.peakCreating = a.creating
	}
	delay, fail, blocked := a.delay, a.createFails, a.blocked
	a.mu.Unlock()
	defer func() { a.mu.Lock(); a.creating--; a.mu.Unlock() }()
	if blocked != nil {
		select {
		case <-blocked:
		case <-ctx.Done():
			return Sandbox{}, ctx.Err()
		}
	}
	if !sleep(ctx, delay) {
		return Sandbox{}, ctx.Err()
	}
	a.mu.Lock()
	if a.live == nil {
		a.live = map[string]bool{}
	}
	a.live[id] = true
	a.mu.Unlock()
	if fail {
		return Sandbox{ID: id}, errors.New("partial create")
	}
	return Sandbox{ID: id}, nil
}
func (a *fakeAdapter) Destroy(ctx context.Context, s Sandbox) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.destroys++
	if a.destroyFails {
		return errors.New("destroy failed")
	}
	delete(a.live, s.ID)
	return nil
}
func (a *fakeAdapter) Kill(context.Context, Sandbox) error { return nil }
func (a *fakeAdapter) DropMaterialization(context.Context, string) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.drops++
	if a.dropFails {
		return errors.New("drop failed")
	}
	return nil
}
func fakePool(t *testing.T, a *fakeAdapter, mutate func(*PoolOptions)) (*Pool, Payload) {
	t.Helper()
	o := DefaultPoolOptions()
	o.MaxReady = 0
	o.RetryDelay = 10 * time.Millisecond
	o.Tick = 5 * time.Millisecond
	if mutate != nil {
		mutate(&o)
	}
	p := NewPool(o, NewMetrics())
	t.Cleanup(func() {
		a.mu.Lock()
		a.destroyFails = false
		a.dropFails = false
		a.mu.Unlock()
		if e := p.Close(context.Background()); e != nil {
			t.Errorf("close: %v", e)
		}
	})
	return p, fixture(Profile{Engine: "sqlite", Fingerprint: textHash("fake-profile")})
}
func eventually(t *testing.T, f func() bool) {
	t.Helper()
	until := time.Now().Add(3 * time.Second)
	for time.Now().Before(until) {
		if f() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatal("condition not reached before deadline")
}
func TestPoolSingleflightAndNoDirtyReuse(t *testing.T) {
	a := &fakeAdapter{delay: time.Millisecond}
	p, v := fakePool(t, a, nil)
	var wg sync.WaitGroup
	ids := map[string]bool{}
	var mu sync.Mutex
	for i := 0; i < 24; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			l, e := p.Acquire(context.Background(), a, v)
			if e != nil {
				t.Error(e)
				return
			}
			mu.Lock()
			if ids[l.Sandbox.ID] {
				t.Error("dirty sandbox reused")
			}
			ids[l.Sandbox.ID] = true
			mu.Unlock()
			if e = l.Release(); e != nil {
				t.Error(e)
			}
			if e = l.Release(); e != nil {
				t.Error(e)
			}
		}()
	}
	wg.Wait()
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.prepares != 1 || a.creates != 24 || a.destroys != 24 || a.peakCreating != 1 || len(a.live) != 0 {
		t.Fatalf("unexpected lifecycle: %+v", a)
	}
}
func TestPoolCancelledAcquireNeverBorrows(t *testing.T) {
	a := &fakeAdapter{}
	p, v := fakePool(t, a, func(o *PoolOptions) { o.MaxReady = 2 })
	if e := p.Warm(context.Background(), a, v); e != nil {
		t.Fatal(e)
	}
	eventually(t, func() bool { return p.Stats()["ready"] > 0 })
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if l, e := p.Acquire(ctx, a, v); e == nil || l != nil {
		t.Fatal("cancelled caller received a sandbox")
	}
	if p.Stats()["leased"] != 0 {
		t.Fatal("leaked borrower")
	}
}
func TestPoolQuarantineAndRecovery(t *testing.T) {
	a := &fakeAdapter{destroyFails: true}
	p, v := fakePool(t, a, nil)
	l, e := p.Acquire(context.Background(), a, v)
	if e != nil {
		t.Fatal(e)
	}
	if e = l.Release(); e == nil {
		t.Fatal("failed cleanup accepted")
	}
	eventually(t, func() bool { return p.Stats()["quarantined"] > 0 })
	if p.Stats()["ready"] != 0 || p.Stats()["leased"] != 0 {
		t.Fatal("dirty sandbox returned to ready")
	}
	a.mu.Lock()
	a.destroyFails = false
	a.mu.Unlock()
	eventually(t, func() bool { a.mu.Lock(); defer a.mu.Unlock(); return len(a.live) == 0 })
}
func TestPoolPartialCreateQuarantine(t *testing.T) {
	a := &fakeAdapter{createFails: true, destroyFails: true}
	p, v := fakePool(t, a, nil)
	if l, e := p.Acquire(context.Background(), a, v); e == nil || l != nil {
		t.Fatal("partial create accepted")
	}
	if p.Stats()["leased"] != 0 {
		t.Fatal("partial create leaked borrower")
	}
	a.mu.Lock()
	a.destroyFails = false
	a.createFails = false
	a.mu.Unlock()
	eventually(t, func() bool { a.mu.Lock(); defer a.mu.Unlock(); return len(a.live) == 0 })
}
func TestPoolResetCannotDropActiveGolden(t *testing.T) {
	a := &fakeAdapter{}
	p, v := fakePool(t, a, nil)
	l, e := p.Acquire(context.Background(), a, v)
	if e != nil {
		t.Fatal(e)
	}
	if p.Reset(a) {
		t.Fatal("reset accepted live lease")
	}
	a.mu.Lock()
	drops := a.drops
	a.mu.Unlock()
	if drops != 0 {
		t.Fatal("golden dropped with borrower")
	}
	if _, e = p.Acquire(context.Background(), a, v); e == nil {
		t.Fatal("disabled engine accepted lease")
	}
	if e = l.Release(); e != nil {
		t.Fatal(e)
	}
	eventually(t, func() bool { return p.Reset(a) })
	p.Resume(a)
	l, e = p.Acquire(context.Background(), a, v)
	if e != nil {
		t.Fatal(e)
	}
	if e = l.Release(); e != nil {
		t.Fatal(e)
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.prepares != 2 {
		t.Fatal("reset did not rebuild golden")
	}
}
func TestPoolLRUAndInFlightRetirementFence(t *testing.T) {
	a := &fakeAdapter{dropFails: true}
	p, v := fakePool(t, a, func(o *PoolOptions) { o.MaxMaterializations = 1 })
	l, e := p.Acquire(context.Background(), a, v)
	if e != nil {
		t.Fatal(e)
	}
	_ = l.Release()
	second := v
	second.MaterializationKey = textHash("second")
	l, e = p.Acquire(context.Background(), a, second)
	if e != nil {
		t.Fatal(e)
	}
	_ = l.Release()
	if _, e = p.Acquire(context.Background(), a, v); e == nil {
		t.Fatal("old key recreated before cleanup completed")
	}
	a.mu.Lock()
	a.dropFails = false
	a.mu.Unlock()
	eventually(t, func() bool { p.mu.Lock(); defer p.mu.Unlock(); return len(p.retiring) == 0 })
}
func TestPoolIdleShrinkAndGoldenGC(t *testing.T) {
	a := &fakeAdapter{}
	p, v := fakePool(t, a, func(o *PoolOptions) {
		o.MaxReady = 2
		o.IdleReady = 30 * time.Millisecond
		o.IdleGolden = 80 * time.Millisecond
	})
	if e := p.Warm(context.Background(), a, v); e != nil {
		t.Fatal(e)
	}
	eventually(t, func() bool { return p.Stats()["ready"] > 0 })
	eventually(t, func() bool { return p.Stats()["materializations"] == 0 })
	eventually(t, func() bool { a.mu.Lock(); defer a.mu.Unlock(); return len(a.live) == 0 && a.drops > 0 })
}
func TestPoolReadyLeaseDoesNotWaitForRefill(t *testing.T) {
	a := &fakeAdapter{}
	p, v := fakePool(t, a, func(o *PoolOptions) { o.MaxReady = 4 })
	if e := p.Warm(context.Background(), a, v); e != nil {
		t.Fatal(e)
	}
	eventually(t, func() bool { return p.Stats()["ready"] > 0 })
	blocked := make(chan struct{})
	a.mu.Lock()
	a.blocked = blocked
	a.mu.Unlock()
	defer close(blocked)
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	l, e := p.Acquire(ctx, a, v)
	if e != nil {
		t.Fatal("READY waited on replenishment:", e)
	}
	if e = l.Release(); e != nil {
		t.Fatal(e)
	}
}
