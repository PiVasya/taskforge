package main

import "testing"

func TestNormalizeOutputForComparison(t *testing.T) {
	cases := []struct {
		name  string
		left  string
		right string
		equal bool
	}{
		{"final newline ignored", "Hello\n", "Hello", true},
		{"trailing space ignored", "Hello \n", "Hello", true},
		{"trailing space on intermediate line ignored", "A \nB", "A\nB", true},
		{"leading space remains significant", " Hello", "Hello", false},
		{"internal space remains significant", "A  B", "A B", false},
		{"tab remains significant", "Hello\t", "Hello", false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := normalizeOutputForComparison(tc.left) == normalizeOutputForComparison(tc.right)
			if got != tc.equal {
				t.Fatalf("comparison mismatch: left=%q right=%q got=%v want=%v", tc.left, tc.right, got, tc.equal)
			}
		})
	}
}
