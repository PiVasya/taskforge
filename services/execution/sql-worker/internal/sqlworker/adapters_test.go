package sqlworker

import (
	"context"
	"taskforge/sqlworker/internal/native"
	"testing"
)

func TestIdempotentAdministrationRetryIsBoundedAndTransientOnly(t *testing.T) {
	a := &ServerAdapter{config: ServerConfig{Config: native.Config{Engine: "postgresql"}}}
	attempts := 0
	err := a.retryIdempotentAdministration(context.Background(), "test-cleanup", func() error {
		attempts++
		if attempts < 3 {
			return &transientAdministrationFailure{Unavailable("temporary admin disconnect")}
		}
		return nil
	})
	if err != nil || attempts != 3 {
		t.Fatalf("transient cleanup retry: attempts=%d err=%v", attempts, err)
	}

	attempts = 0
	permanent := Unavailable("permanent cleanup failure")
	err = a.retryIdempotentAdministration(context.Background(), "test-cleanup", func() error {
		attempts++
		return permanent
	})
	if err != permanent || attempts != 1 {
		t.Fatalf("non-transient cleanup was retried: attempts=%d err=%v", attempts, err)
	}

	attempts = 0
	err = a.retryIdempotentAdministration(context.Background(), "test-cleanup", func() error {
		attempts++
		return &transientAdministrationFailure{Unavailable("still unavailable")}
	})
	if !isTransientAdministrationFailure(err) || attempts != 3 {
		t.Fatalf("transient cleanup retry was not bounded: attempts=%d err=%v", attempts, err)
	}
}
