package native

import (
	"context"
	"errors"
	"syscall"
	"testing"
	"time"
)

type deadlineOnlyContext struct {
	deadline time.Time
}

func (c deadlineOnlyContext) Deadline() (time.Time, bool) { return c.deadline, true }
func (deadlineOnlyContext) Done() <-chan struct{}         { return nil }
func (deadlineOnlyContext) Err() error                    { return nil }
func (deadlineOnlyContext) Value(any) any                 { return nil }

func TestContextDeadlineErrorUsesMonotonicDeadlineEvenBeforeErrPublication(t *testing.T) {
	if err := contextDeadlineError(deadlineOnlyContext{deadline: time.Now().Add(-time.Millisecond)}); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("past deadline must be classified as deadline exceeded, got %v", err)
	}
	if err := contextDeadlineError(deadlineOnlyContext{deadline: time.Now().Add(time.Second)}); err != nil {
		t.Fatalf("future deadline must remain usable, got %v", err)
	}
}

func TestPostgresPollTreatsEINTRAsRetryableWakeup(t *testing.T) {
	if got := normalizePollResult(-1, int(syscall.EINTR)); got != 0 {
		t.Fatalf("EINTR must be treated as a retryable wakeup, got %d", got)
	}
	if got := normalizePollResult(-1, int(syscall.EIO)); got != -1 {
		t.Fatalf("real poll errors must remain errors, got %d", got)
	}
	if got := normalizePollResult(1, 0); got != 1 {
		t.Fatalf("ready poll result changed unexpectedly, got %d", got)
	}
}
