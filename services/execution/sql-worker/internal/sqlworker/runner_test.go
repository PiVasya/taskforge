package sqlworker

import (
	"errors"
	"testing"
	"time"
)

type laggingDeadlineContext struct {
	deadline time.Time
}

func (c laggingDeadlineContext) Deadline() (time.Time, bool) { return c.deadline, true }
func (laggingDeadlineContext) Done() <-chan struct{}         { return nil }
func (laggingDeadlineContext) Err() error                    { return nil }
func (laggingDeadlineContext) Value(any) any                 { return nil }

func TestRunnerFencesExpiredLeaseBeforeContextErrPublication(t *testing.T) {
	h := newHarness(t)
	ctx := laggingDeadlineContext{deadline: time.Now().Add(-time.Millisecond)}
	out, err := h.runner.Run(ctx, Job{
		ID:      "10000000-0000-4000-8000-000000000006",
		Kind:    "sql-preview",
		Payload: h.payload,
	}, h.adapter, h.profile)
	if !errors.Is(err, ErrLostLease) {
		t.Fatalf("expired lease must be fenced as ErrLostLease, got verdict=%q err=%v", out.Verdict, err)
	}
	if out.Verdict != "" || out.Result != nil {
		t.Fatalf("expired lease leaked an outcome: %+v", out)
	}
}
