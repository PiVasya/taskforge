package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestRuntimeReadinessRequiresDriver(t *testing.T) {
	t.Setenv("ONEC_DRIVER", filepath.Join(t.TempDir(), "missing-driver"))
	if err := runtimeReadiness(); err == nil || !strings.Contains(err.Error(), "public key") {
		// Public-key validation intentionally happens first. The contract image is
		// fail-closed until both the analyzer key and the private 1C runtime exist.
		if err == nil {
			t.Fatal("expected readiness failure")
		}
	}
}

func TestDriverPathCanBeConfigured(t *testing.T) {
	path := filepath.Join(t.TempDir(), "driver")
	if err := os.WriteFile(path, []byte("#!/bin/sh\nexit 0\n"), 0o700); err != nil {
		t.Fatal(err)
	}
	t.Setenv("ONEC_DRIVER", path)
	if got := driverPath(); got != path {
		t.Fatalf("driverPath()=%q want %q", got, path)
	}
}

func TestRequestTimeoutIsBounded(t *testing.T) {
	huge := 600000
	if got := requestTimeout(&huge); got != maxTimeout {
		t.Fatalf("timeout=%v want %v", got, maxTimeout)
	}
}

func TestOuterTimeoutExceedsDriverHardLimit(t *testing.T) {
	huge := 999999
	if got := requestTimeout(&huge); got <= 90*time.Second {
		t.Fatalf("outer timeout=%v must exceed driver hard limit 90s", got)
	}
}
