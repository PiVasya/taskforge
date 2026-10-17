package main

import "testing"

func TestCompilationDeadlineIsTimeLimit(t *testing.T) {
	result := compileTimedOutResult(commandResult{
		TimedOut: true, ExitCode: 1, Stderr: "compiler interrupted after timeout",
	})
	if result == nil || result.Status != "time_limit" || result.ExitCode != 124 {
		t.Fatalf("expected TLE for deadline, got %#v", result)
	}
	if compileTimedOutResult(commandResult{TimedOut: false, ExitCode: 1, Stderr: "syntax error"}) != nil {
		t.Fatal("normal compiler failure must retain its compilation error")
	}
}
