package sqlworker

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"taskforge/sqlworker/internal/native"
	"testing"
	"time"
)

var testExecutable string

func TestMain(m *testing.M) {
	if path := os.Getenv("SQL_TEST_HELPER"); path != "" {
		if !filepath.IsAbs(path) {
			panic("SQL_TEST_HELPER must be absolute")
		}
		if info, err := os.Stat(path); err != nil || !info.Mode().IsRegular() {
			panic("SQL_TEST_HELPER is missing")
		}
		testExecutable = path
		os.Exit(m.Run())
	}
	dir, e := os.MkdirTemp("", "taskforge-sql-go-tests-")
	if e != nil {
		panic(e)
	}
	testExecutable = filepath.Join(dir, "sql-worker")
	cmd := exec.Command("go", "build", "-trimpath", "-buildvcs=false", "-o", testExecutable, "./cmd/sql-worker")
	cmd.Dir = "../.."
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if e = cmd.Run(); e != nil {
		os.RemoveAll(dir)
		os.Exit(2)
	}
	code := m.Run()
	os.RemoveAll(dir)
	os.Exit(code)
}
func ptr[T any](v T) *T { return &v }
func fixture(profile Profile) Payload {
	return Payload{ContractVersion: 1, AssignmentID: "10000000-0000-4000-8000-000000000001", SpecVersionID: "10000000-0000-4000-8000-000000000002", DatasetVersionID: "10000000-0000-4000-8000-000000000003", EngineTargetID: "10000000-0000-4000-8000-000000000004", Profile: profile, MaterializationKey: textHash("shop-fixture" + profile.Fingerprint), ArtifactKey: textHash("shop-assignment-one"), DatasetHash: textHash("shop-data"),
		Definition: Definition{Tables: []Table{{Name: "products", Columns: []Column{{Name: "id", Type: "integer", Identity: true}, {Name: "name", Type: "string", Length: ptr(100)}, {Name: "price", Type: "decimal", Precision: ptr(10), Scale: ptr(2)}}, PrimaryKey: []string{"id"}, Unique: [][]string{{"name"}}, ForeignKeys: []ForeignKey{}, Indexes: []Index{{Name: "price_idx", Columns: []string{"price"}}}}}},
		Seed:       map[string][]map[string]any{"products": {{"id": json.Number("1"), "name": "book", "price": json.Number("1.25")}, {"id": json.Number("2"), "name": "pen", "price": json.Number("2.5")}}}, EngineOverrides: map[string]EngineMapping{}, Mode: "result", Source: "SELECT id,name,price FROM products ORDER BY id", Comparison: Comparison{OrderMatters: false, ColumnNamesMatter: true, DuplicatesMatter: true, CaseSensitive: true, NumericTolerance: "0"}, StateCheck: StateCheck{Tables: []string{}}, SchemaCheck: SchemaCheck{Tables: []string{}, DefaultsMatter: true, IndexesMatter: true}, Limits: DefaultLimits()}
}

type harness struct {
	adapter EngineAdapter
	profile Profile
	pool    *Pool
	runner  *JobRunner
	payload Payload
}

func newHarness(t *testing.T, options ...PoolOptions) *harness {
	t.Helper()
	a, e := NewSQLiteAdapter(t.TempDir(), "test-worker", textHash("go-test-executor"))
	if e != nil {
		t.Fatal(e)
	}
	if e = a.Startup(context.Background()); e != nil {
		t.Fatal(e)
	}
	reg, e := a.Registration(context.Background())
	if e != nil {
		t.Fatal(e)
	}
	hash, e := ContentHash(reg)
	if e != nil {
		t.Fatal(e)
	}
	profile := Profile{ID: "10000000-0000-4000-8000-000000000005", Key: "sqlite", DisplayName: "SQLite", Engine: reg.Engine, EngineVersion: reg.EngineVersion, RuntimeDigest: reg.RuntimeDigest, AdapterVersion: reg.AdapterVersion, Settings: reg.Settings, Fingerprint: hash}
	metrics := NewMetrics()
	opts := DefaultPoolOptions()
	if len(options) > 0 {
		opts = options[0]
	}
	pool := NewPool(opts, metrics)
	runner := &JobRunner{pool, &ProcessRunner{Executable: testExecutable, MemoryBytes: 256 << 20, Metrics: metrics}, metrics}
	h := &harness{a, profile, pool, runner, fixture(profile)}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		if e := pool.Close(ctx); e != nil {
			t.Errorf("pool close: %v", e)
		}
	})
	return h
}
func (h *harness) run(t *testing.T, kind string, p Payload) Outcome {
	t.Helper()
	out, e := h.runner.Run(context.Background(), Job{ID: "10000000-0000-4000-8000-000000000006", Kind: kind, Payload: p}, h.adapter, h.profile)
	if e != nil {
		t.Fatal(e)
	}
	return out
}
func (h *harness) materialize(t *testing.T, p Payload, source string) Payload {
	t.Helper()
	p.ReferenceSQL = &source
	p.ValidationRunID = ptr("10000000-0000-4000-8000-000000000007")
	out := h.run(t, "sql-materialize", p)
	if out.Verdict != "Validated" {
		b, _ := json.Marshal(out.Result)
		t.Fatalf("materialization %s: %s", out.Verdict, b)
	}
	p.Expected, _ = json.Marshal(out.Result["expected"])
	hash := out.Result["expectedContentHash"].(string)
	p.ExpectedContentHash = &hash
	p.ReferenceSQL = nil
	p.ValidationRunID = nil
	return p
}
func requireVerdict(t *testing.T, out Outcome, verdict string) {
	t.Helper()
	if out.Verdict != verdict {
		b, _ := json.Marshal(out.Result)
		t.Fatalf("got %s expected %s: %s", out.Verdict, verdict, b)
	}
}
func decodeSnapshot(t *testing.T, out Outcome) Snapshot {
	t.Helper()
	b, _ := json.Marshal(out.Result)
	var public map[string]any
	if e := DecodeStrict(b, &public); e != nil {
		t.Fatal(e)
	}
	delete(public, "check")
	b, _ = json.Marshal(public)
	var s Snapshot
	if e := DecodeStrict(b, &s); e != nil {
		t.Fatal(e)
	}
	return s
}

func TestAllThreadIsolationProbe(t *testing.T) {
	cmd := exec.Command(testExecutable, "self-test-isolation")
	cmd.Env = childEnvironment()
	out, e := cmd.CombinedOutput()
	if e != nil || !strings.Contains(string(out), "SQL_ALL_THREAD_ISOLATION_OK") {
		t.Fatalf("isolation probe failed: %v %s", e, out)
	}
}
func TestSQLiteResultAndHiddenAnswerBoundary(t *testing.T) {
	h := newHarness(t)
	p := h.materialize(t, h.payload, "SELECT name FROM products WHERE id=1")
	p.Source = "SELECT name FROM products WHERE id=1"
	out := h.run(t, "sql-check", p)
	requireVerdict(t, out, "Accepted")
	raw, _ := json.Marshal(out.Result)
	for _, secret := range []string{"expected", "referenceSql", "verificationData"} {
		if strings.Contains(string(raw), secret) {
			t.Errorf("private field leaked: %s", secret)
		}
	}
	p.Source = "SELECT name FROM products WHERE id=2"
	requireVerdict(t, h.run(t, "sql-check", p), "WrongAnswer")
	p.Expected = nil
	p.ExpectedContentHash = nil
	requireVerdict(t, h.run(t, "sql-preview", p), "Previewed")
}
func TestSQLiteStateAndIndependentPreview(t *testing.T) {
	h := newHarness(t)
	p := h.payload
	p.Mode = "state"
	p.AllowMultipleStatements = true
	p = h.materialize(t, p, "UPDATE products SET price=price+10 WHERE id=1; DELETE FROM products WHERE id=2")
	p.Source = "DELETE FROM products WHERE id=2; UPDATE products SET price=11.25 WHERE id=1"
	requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
	p.Expected = nil
	p.ExpectedContentHash = nil
	p.Mode = "result"
	p.Source = "UPDATE products SET name='changed' WHERE id=1; SELECT name FROM products WHERE id=1"
	out := h.run(t, "sql-preview", p)
	requireVerdict(t, out, "Previewed")
	snapshot := decodeSnapshot(t, out)
	if snapshot.Data["products"].Rows[0][1].Value != "changed" {
		t.Fatal("preview did not show updated table")
	}
	p.Source = "SELECT name FROM products WHERE id=1"
	out = h.run(t, "sql-preview", p)
	requireVerdict(t, out, "Previewed")
	snapshot = decodeSnapshot(t, out)
	if snapshot.Results[0].Rows[0][0].Value != "book" {
		t.Fatal("next run reused dirty state")
	}
}
func TestSQLiteSchemaVerificationAndDropPreview(t *testing.T) {
	h := newHarness(t)
	p := h.payload
	p.Mode = "schema"
	p.AllowMultipleStatements = true
	p = h.materialize(t, p, "ALTER TABLE products ADD COLUMN stock INTEGER NOT NULL DEFAULT 0; CREATE INDEX stock_idx ON products(stock)")
	p.Source = "ALTER TABLE products ADD COLUMN stock INTEGER NOT NULL DEFAULT 0; CREATE INDEX another_name ON products(stock)"
	requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
	p.Source = "ALTER TABLE products ADD COLUMN stock TEXT"
	requireVerdict(t, h.run(t, "sql-check", p), "WrongAnswer")
	p.Expected = nil
	p.ExpectedContentHash = nil
	p.Source = "DROP TABLE products"
	out := h.run(t, "sql-preview", p)
	requireVerdict(t, out, "Previewed")
	if len(decodeSnapshot(t, out).Schema.Tables) != 0 {
		t.Fatal("DROP was not reflected in live schema")
	}
}
func TestSQLiteEmptyDatasetSchemaTask(t *testing.T) {
	h := newHarness(t)
	p := h.payload
	p.Definition.Tables = []Table{}
	p.Seed = map[string][]map[string]any{}
	p.MaterializationKey = textHash("empty")
	p.Mode = "schema"
	p = h.materialize(t, p, "CREATE TABLE people(id INTEGER PRIMARY KEY, name TEXT NOT NULL)")
	p.Source = "CREATE TABLE people(id INTEGER PRIMARY KEY, name TEXT NOT NULL)"
	requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
}
func TestSQLitePartialScriptFailureSnapshot(t *testing.T) {
	h := newHarness(t)
	p := h.payload
	p.AllowMultipleStatements = true
	p.Source = "UPDATE products SET name='changed' WHERE id=1; SELECT missing FROM products"
	out := h.run(t, "sql-preview", p)
	requireVerdict(t, out, "RuntimeError")
	snapshot := decodeSnapshot(t, out)
	if snapshot.StatementsExecuted != 1 || snapshot.Data["products"].Rows[0][1].Value != "changed" {
		t.Fatal("partial execution snapshot was not preserved")
	}
}
func TestSQLiteBudgetsAndTimeout(t *testing.T) {
	h := newHarness(t)
	for _, tc := range []struct {
		name, source, verdict string
		change                func(*Payload)
	}{
		{"row cap", "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<100) SELECT x FROM n", "OutputLimitExceeded", func(p *Payload) { p.Limits.MaxRows = 10; p.Limits.PreviewRows = 5 }},
		{"field cap", "SELECT hex(zeroblob(65536))", "OutputLimitExceeded", func(p *Payload) { p.Limits.MaxBytes = 1024 }},
		{"recursive timeout", "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n) SELECT sum(x) FROM n", "TimeLimitExceeded", func(p *Payload) { p.Limits.TimeoutMS = 100 }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := h.payload
			p.Source = tc.source
			tc.change(&p)
			requireVerdict(t, h.run(t, "sql-preview", p), tc.verdict)
		})
	}
}
func TestSQLiteFileAndAdministrationDenied(t *testing.T) {
	h := newHarness(t)
	for _, sql := range []string{"ATTACH DATABASE '/tmp/evil.db' AS other", "VACUUM INTO '/tmp/evil.db'", "PRAGMA writable_schema=1", "SELECT load_extension('/tmp/evil.so')", "SELECT readfile('/etc/passwd')", "CREATE USER root", "CREATE DATABASE other", "CREATE TRIGGER bad AFTER INSERT ON products BEGIN DELETE FROM products; END"} {
		t.Run(sql, func(t *testing.T) {
			p := h.payload
			p.Source = sql
			out := h.run(t, "sql-preview", p)
			if out.Verdict == "Previewed" {
				t.Fatal("unsafe operation accepted")
			}
		})
	}
}
func TestSQLiteNativeAuthorizerIndependentOfLexer(t *testing.T) {
	path := filepath.Join(t.TempDir(), "sandbox.db")
	c, e := native.Open(native.Config{Engine: "sqlite", Path: path, Create: true})
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	if e = c.ConfigureSQLite(1048576, 5000); e != nil {
		t.Fatal(e)
	}
	if e = c.AuthorizeSQLite(true); e != nil {
		t.Fatal(e)
	}
	for _, sql := range []string{"ATTACH DATABASE ':memory:' AS forbidden", "PRAGMA writable_schema=ON", "BEGIN", "CREATE VIRTUAL TABLE v USING fts5(x)", "CREATE TEMP TABLE secret(x)"} {
		if e = c.Exec(context.Background(), sql); e == nil {
			t.Errorf("native policy allowed %s", sql)
		}
	}
	if e = c.Exec(context.Background(), "CREATE TABLE allowed(id INTEGER)"); e != nil {
		t.Fatal(e)
	}
}
func TestExpectedArtifactSeparationAndIntegrity(t *testing.T) {
	h := newHarness(t)
	a := h.materialize(t, h.payload, "SELECT name FROM products WHERE id=1")
	b := h.payload
	b.AssignmentID = "10000000-0000-4000-8000-000000000008"
	b.ArtifactKey = textHash("second-spec")
	b = h.materialize(t, b, "SELECT name FROM products WHERE id=2")
	if *a.ExpectedContentHash == *b.ExpectedContentHash {
		t.Fatal("different assignment answers collapsed")
	}
	if h.pool.Stats()["materializations"] != 1 {
		t.Fatal("shared dataset was not deduplicated")
	}
	b.Source = "SELECT name FROM products WHERE id=1"
	requireVerdict(t, h.run(t, "sql-check", b), "WrongAnswer")
	a.Source = "SELECT name FROM products WHERE id=1"
	a.Expected = append(json.RawMessage(nil), b.Expected...)
	requireVerdict(t, h.run(t, "sql-check", a), "JudgeUnavailable")
}
func TestInvalidReferenceAndRuntimePinning(t *testing.T) {
	h := newHarness(t)
	p := h.payload
	p.ReferenceSQL = ptr("SELECT random()")
	requireVerdict(t, h.run(t, "sql-materialize", p), "ValidationFailed")
	p = h.payload
	p.Profile.Fingerprint = textHash("another-worker")
	requireVerdict(t, h.run(t, "sql-preview", p), "JudgeUnavailable")
	p = h.payload
	p.ReferenceSQL = ptr("SELECT hidden")
	requireVerdict(t, h.run(t, "sql-preview", p), "JudgeUnavailable")
}
func TestPoolManifestCannotBeRebound(t *testing.T) {
	h := newHarness(t)
	requireVerdict(t, h.run(t, "sql-preview", h.payload), "Previewed")
	p, _ := cloneJSON(h.payload)
	p.Seed["products"][0]["name"] = "poison"
	requireVerdict(t, h.run(t, "sql-preview", p), "JudgeUnavailable")
}
func TestGoldenBytesNeverMutate(t *testing.T) {
	h := newHarness(t)
	if e := h.pool.Warm(context.Background(), h.adapter, h.payload); e != nil {
		t.Fatal(e)
	}
	a := h.adapter.(*SQLiteAdapter)
	path, e := a.golden(h.payload.MaterializationKey)
	if e != nil {
		t.Fatal(e)
	}
	before, e := os.ReadFile(path)
	if e != nil {
		t.Fatal(e)
	}
	p := h.payload
	p.Source = "DELETE FROM products"
	requireVerdict(t, h.run(t, "sql-preview", p), "Previewed")
	after, e := os.ReadFile(path)
	if e != nil {
		t.Fatal(e)
	}
	if string(before) != string(after) {
		t.Fatal("golden was modified")
	}
}
func TestWorkerSecretEnvironmentNotInherited(t *testing.T) {
	t.Setenv("TASKFORGE_INTERNAL_KEY", "private-internal-key")
	t.Setenv("SQL_POSTGRES_PASSWORD", "private-admin-password")
	t.Setenv("LD_PRELOAD", "/not-used.so")
	for _, entry := range childEnvironment() {
		if strings.Contains(entry, "private") || strings.HasPrefix(entry, "LD_") {
			t.Fatal("private environment passed to child")
		}
	}
}
func TestSQLMixedConcurrentRuns(t *testing.T) {
	h := newHarness(t)
	p := h.materialize(t, h.payload, "SELECT count(*) AS total FROM products")
	p.Source = "SELECT count(*) AS total FROM products"
	var wg sync.WaitGroup
	failures := make(chan string, 12)
	slots := make(chan struct{}, 4)
	for i := 0; i < 12; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			slots <- struct{}{}
			defer func() { <-slots }()
			kind := "sql-check"
			payload := p
			if i%2 == 0 {
				kind = "sql-preview"
				payload.Expected = nil
				payload.ExpectedContentHash = nil
			}
			out, e := h.runner.Run(context.Background(), Job{Kind: kind, Payload: payload}, h.adapter, h.profile)
			want := "Accepted"
			if kind == "sql-preview" {
				want = "Previewed"
			}
			if e != nil || out.Verdict != want {
				failures <- fmt.Sprintf("%s %v %+v", out.Verdict, e, out.Result)
			}
		}(i)
	}
	wg.Wait()
	close(failures)
	for e := range failures {
		t.Error(e)
	}
}
func TestSQLLoad100Run100Check(t *testing.T) {
	if os.Getenv("SQL_RUN_LOAD_TESTS") != "1" {
		t.Skip("Set SQL_RUN_LOAD_TESTS=1 for the 200-attempt local SQLite load gate")
	}
	h := newHarness(t)
	p := h.materialize(t, h.payload, "SELECT id,name,price FROM products ORDER BY id")
	p.Source = "SELECT id,name,price FROM products ORDER BY id"
	start := time.Now()
	var wg sync.WaitGroup
	var mu sync.Mutex
	completed := 0
	failures := []string{}
	slots := make(chan struct{}, 4)
	for i := 0; i < 200; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			slots <- struct{}{}
			defer func() { <-slots }()
			kind, want := "sql-check", "Accepted"
			payload := p
			if i < 100 {
				kind, want = "sql-preview", "Previewed"
				payload.Expected = nil
				payload.ExpectedContentHash = nil
			}
			out, e := h.runner.Run(context.Background(), Job{Kind: kind, Payload: payload}, h.adapter, h.profile)
			mu.Lock()
			defer mu.Unlock()
			if e != nil || out.Verdict != want {
				failures = append(failures, fmt.Sprintf("%s %v", out.Verdict, e))
			} else {
				completed++
			}
		}(i)
	}
	wg.Wait()
	if len(failures) > 0 {
		t.Fatalf("%d failed: %v", len(failures), failures)
	}
	t.Logf("LOCAL SQLITE: 100 Run + 100 Check, four execution slots, completed=%d elapsed=%s (not HTTP/PG/MySQL throughput)", completed, time.Since(start))
	t.Log(h.runner.Metrics.Render())
}
