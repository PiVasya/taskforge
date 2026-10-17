package main

import (
	"encoding/json"
	"strings"
	"testing"
)

// The full 128-test envelope remains readable by the .NET worker even
// when the test inputs/outputs contain JSON-escaped control bytes.
func TestWorstCaseBatchEnvelopeFitsWorkerLimit(t *testing.T) {
	tests := make([]testResult, 0, maxTestsPerRequest)
	capBytes := testBatchPreviewLimit(maxTestsPerRequest)
	source := strings.Repeat("\x01", maxTestResultPreviewBytes+400)
	for index := 0; index < maxTestsPerRequest; index++ {
		preview := previewCaseTextWithLimit(source, capBytes)
		tests = append(tests, testResult{
			Input: preview, ExpectedOutput: preview, ActualOutput: preview,
			Stderr: preview, Status: "wrong_answer", ExitCode: 1,
		})
	}
	encoded, err := json.Marshal(map[string]any{"results": tests})
	if err != nil {
		t.Fatal(err)
	}
	if len(encoded) >= 2*1024*1024 {
		t.Fatalf("128 tests produced %d bytes, worker limit is 2097152", len(encoded))
	}
	if testBatchPreviewLimit(5) != maxTestResultPreviewBytes {
		t.Fatal("small batches should retain full 8 KiB previews")
	}
}

func TestCompilerDiagnosticIsCapped(t *testing.T) {
	source := strings.Repeat("E", 1000000)
	result := previewCompilerDiagnostic(&source, testBatchPreviewLimit(128))
	if result == nil || len(*result) >= 2048 {
		t.Fatal("compiler diagnostic was not bounded")
	}
}
