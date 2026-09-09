package sqlworker

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"
)

type fakeQueue struct {
	mu        sync.Mutex
	calls     int
	failFirst bool
	out       Outcome
}

func (*fakeQueue) Capabilities(context.Context, string, []string, int) error { return nil }
func (*fakeQueue) Claim(context.Context, string, []string) (*Job, error)     { return nil, nil }
func (*fakeQueue) Renew(context.Context, string, Job) error                  { return nil }
func (*fakeQueue) Warmup(context.Context, string) ([]Payload, error)         { return nil, nil }
func (c *fakeQueue) Complete(ctx context.Context, worker string, j Job, out Outcome, d time.Duration) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.calls++
	c.out = out
	if c.failFirst && c.calls == 1 {
		return errors.New("lost completion ACK")
	}
	return nil
}
func testWorker(t *testing.T, h *harness, q *fakeQueue) *Worker {
	t.Helper()
	r := NewRegistry([]EngineAdapter{h.adapter}, nil, h.pool, h.runner.Metrics)
	r.entries[0].profile = &h.profile
	r.entries[0].ready = true
	r.entries[0].initialized = true
	w := &Worker{Config: Config{WorkerID: "test", Concurrency: 1}, Client: q, Registry: r, Pool: h.pool, Runner: h.runner, Metrics: h.runner.Metrics, wake: make(chan struct{}, 1), slots: make(chan struct{}, 1)}
	w.slots <- struct{}{}
	w.jobs.Add(1)
	return w
}
func TestCompletionRetryDoesNotReexecuteSQL(t *testing.T) {
	h := newHarness(t)
	q := &fakeQueue{failFirst: true}
	w := testWorker(t, h, q)
	j := Job{ID: "10000000-0000-4000-8000-000000000001", Kind: "sql-preview", Payload: h.payload, LeaseToken: ptr("10000000-0000-4000-8000-000000000002"), LeaseExpiresAt: ptr(time.Now().Add(45 * time.Second))}
	w.execute(context.Background(), j)
	w.jobs.Wait()
	q.mu.Lock()
	defer q.mu.Unlock()
	if q.calls != 2 || q.out.Verdict != "Previewed" {
		t.Fatal("completion not retried safely", q.calls, q.out.Verdict)
	}
	w.Metrics.mu.Lock()
	defer w.Metrics.mu.Unlock()
	if w.Metrics.values[metricKey{"sql_attempt_duration_seconds_count", "sqlite"}] != 1 {
		t.Fatal("lost ACK caused SQL reexecution")
	}
	if w.Pool.Stats()["leased"] != 0 {
		t.Fatal("completion retained sandbox")
	}
}
func TestLeaseWatchdogCancelsHelperAndFencesCompletion(t *testing.T) {
	h := newHarness(t)
	q := &fakeQueue{}
	w := testWorker(t, h, q)
	p := h.payload
	p.Limits.TimeoutMS = 10000
	p.Source = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n) SELECT sum(x) FROM n"
	j := Job{ID: "10000000-0000-4000-8000-000000000001", Kind: "sql-preview", Payload: p, LeaseToken: ptr("10000000-0000-4000-8000-000000000002"), LeaseExpiresAt: ptr(time.Now().Add(5200 * time.Millisecond))}
	start := time.Now()
	w.execute(context.Background(), j)
	w.jobs.Wait()
	if time.Since(start) > 3*time.Second {
		t.Fatal("lease watchdog did not promptly kill helper")
	}
	q.mu.Lock()
	defer q.mu.Unlock()
	if q.calls != 0 {
		t.Fatal("expired lease delivered a verdict")
	}
	if h.pool.Stats()["leased"] != 0 {
		t.Fatal("lost lease retained sandbox")
	}
}
