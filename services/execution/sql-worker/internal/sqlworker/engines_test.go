package sqlworker

import (
	"context"
	"encoding/json"
	"os"
	"strings"
	"taskforge/sqlworker/internal/native"
	"testing"
	"time"
)

func engineHarness(t *testing.T, engine string) *harness {
	t.Helper()
	if os.Getenv("SQL_TEST_ENGINE_GATE") != "1" {
		t.Skip("requires isolated Docker PostgreSQL 18 / MySQL 8.4 engines")
	}
	host, port, digest := "sql-postgres", "5432", os.Getenv("SQL_TEST_PG_DIGEST")
	user := "postgres"
	if engine == "mysql" {
		host, port, user, digest = "sql-mysql", "3306", "root", os.Getenv("SQL_TEST_MY_DIGEST")
	}
	cfg := ServerConfig{Config: native.Config{Engine: engine, Host: host, Port: port, User: user, Password: os.Getenv("SQL_TEST_PASSWORD")}, RuntimeDigest: digest, Marker: os.Getenv("SQL_TEST_MARKER")}
	a, e := NewServerAdapter(cfg, "go-engine-test-"+t.Name(), textHash("go-test-executor"))
	if e != nil {
		t.Fatal(e)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if e = a.Startup(ctx); e != nil {
		t.Fatal("dedicated engine startup:", e)
	}
	reg, e := a.Registration(ctx)
	if e != nil {
		t.Fatal(e)
	}
	fp, e := ContentHash(reg)
	if e != nil {
		t.Fatal(e)
	}
	profile := Profile{ID: "10000000-0000-4000-8000-000000000005", Key: engine, DisplayName: engine, Engine: reg.Engine, EngineVersion: reg.EngineVersion, RuntimeDigest: reg.RuntimeDigest, AdapterVersion: reg.AdapterVersion, Settings: reg.Settings, Fingerprint: fp}
	metrics := NewMetrics()
	o := DefaultPoolOptions()
	o.MaxReady = 2
	pool := NewPool(o, metrics)
	h := &harness{a, profile, pool, &JobRunner{pool, &ProcessRunner{Executable: testExecutable, MemoryBytes: 256 << 20, Metrics: metrics}, metrics}, fixture(profile)}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer cancel()
		if e := pool.Close(ctx); e != nil {
			t.Error("pool close:", e)
		}
		if e := a.Startup(ctx); e != nil {
			t.Error("namespace cleanup:", e)
		}
	})
	return h
}
func forEngines(t *testing.T, run func(*testing.T, *harness)) {
	for _, engine := range []string{"postgresql", "mysql"} {
		t.Run(engine, func(t *testing.T) { run(t, engineHarness(t, engine)) })
	}
}
func TestRealEnginesResultAndEmptyResult(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		p := h.materialize(t, h.payload, "SELECT id,name,price FROM products ORDER BY id")
		requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
		p.Source = "SELECT id,name,price FROM products WHERE id=1"
		requireVerdict(t, h.run(t, "sql-check", p), "WrongAnswer")
		p = h.materialize(t, h.payload, "SELECT id FROM products WHERE id=-1")
		p.Source = "SELECT id FROM products WHERE id=-1"
		requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
	})
}
func TestRealEnginesStateAndIndependentAttempts(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		p := h.payload
		p.Mode = "state"
		p.AllowMultipleStatements = true
		source := "DELETE FROM products WHERE id=2; INSERT INTO products(name,price) VALUES('third',30); UPDATE products SET price=11 WHERE id=1"
		p = h.materialize(t, p, source)
		p.Source = source
		requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
		p.Expected = nil
		p.ExpectedContentHash = nil
		p.Mode = "result"
		p.Source = "SELECT name FROM products ORDER BY id"
		out := h.run(t, "sql-preview", p)
		requireVerdict(t, out, "Previewed")
		if decodeSnapshot(t, out).Results[0].Rows[1][0].Value != "pen" {
			t.Fatal("dirty state reused")
		}
	})
}
func TestRealEnginesSchemaDDL(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		p := h.payload
		p.Mode = "schema"
		p.AllowMultipleStatements = true
		source := "DROP TABLE products; CREATE TABLE people(id INTEGER PRIMARY KEY, name VARCHAR(80) NOT NULL); ALTER TABLE people ADD age INTEGER; CREATE UNIQUE INDEX ux_people ON people(name)"
		p = h.materialize(t, p, source)
		p.Source = source
		requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
		p.Source = "DROP TABLE products; CREATE TABLE people(id INTEGER, name VARCHAR(80))"
		requireVerdict(t, h.run(t, "sql-check", p), "WrongAnswer")
	})
}
func TestRealEnginesPortableForeignKeysDefaults(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		p := h.payload
		p.MaterializationKey = textHash("foreign-keys" + p.Profile.Fingerprint)
		p.Definition.Tables = append(p.Definition.Tables, Table{Name: "tags", Columns: []Column{{Name: "id", Type: "integer"}, {Name: "product_id", Type: "integer", Nullable: true}, {Name: "enabled", Type: "boolean", Default: &Default{Kind: "literal", Value: true}}}, PrimaryKey: []string{"id"}, ForeignKeys: []ForeignKey{{Name: "fk_tags_product", Columns: []string{"product_id"}, ReferenceTable: "products", ReferenceColumns: []string{"id"}, OnDelete: "set_null", OnUpdate: "no_action"}}, Unique: [][]string{}, Indexes: []Index{}})
		p.Seed["tags"] = []map[string]any{{"id": json.Number("1"), "product_id": json.Number("1")}}
		p = h.materialize(t, p, "SELECT id FROM tags WHERE enabled=TRUE")
		p.Source = "SELECT id FROM tags WHERE enabled=TRUE"
		requireVerdict(t, h.run(t, "sql-check", p), "Accepted")
	})
}
func TestRealEnginesPartialErrorAndResultLimits(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		p := h.payload
		p.Limits.MaxRows = 10
		p.Limits.PreviewRows = 5
		p.Source = "SELECT a.id,b.id,c.id,d.id FROM products a,products b,products c,products d"
		requireVerdict(t, h.run(t, "sql-preview", p), "OutputLimitExceeded")
		p = h.payload
		p.AllowMultipleStatements = true
		p.Source = "DELETE FROM products WHERE id=2; SELECT * FROM missing"
		out := h.run(t, "sql-preview", p)
		requireVerdict(t, out, "RuntimeError")
		if len(decodeSnapshot(t, out).Data["products"].Rows) != 1 {
			t.Fatal("missing partial mutation snapshot")
		}
	})
}
func TestRealEnginesPrivilegesIndependentOfLexer(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		a := h.adapter.(*ServerAdapter)
		l, e := h.pool.Acquire(context.Background(), a, h.payload)
		if e != nil {
			t.Fatal(e)
		}
		defer l.Release()
		cfg := l.Sandbox.Connection
		if a.Engine() == "postgresql" {
			cfg.Database = "postgres"
		} else {
			cfg.Database = "mysql"
		}
		if c, e := native.Open(cfg); e == nil {
			c.Close()
			t.Fatal("sandbox user accessed administrative database")
		}
		c, e := native.Open(l.Sandbox.Connection)
		if e != nil {
			t.Fatal(e)
		}
		defer c.Close()
		for _, sql := range []string{"CREATE ROLE tf_forbidden_probe", "CREATE DATABASE tf_forbidden_probe"} {
			if e = c.Exec(context.Background(), sql); e == nil {
				t.Fatal("server itself allowed administrative SQL:", sql)
			}
		}
		sql := "SELECT pg_read_file('/etc/passwd')"
		if a.Engine() == "mysql" {
			sql = "SELECT * FROM mysql.user"
		}
		if _, e = c.Query(context.Background(), sql, 10, 4096); e == nil {
			t.Fatal("server privilege boundary failed")
		}
		if a.Engine() == "mysql" {
			r, e := c.Query(context.Background(), "SELECT LOAD_FILE('/etc/passwd')", 1, 4096)
			if e == nil && (len(r.Rows) != 1 || !r.Rows[0][0].Null) {
				t.Fatal("MySQL FILE privilege leaked")
			}
		}
	})
}
func TestRealEnginesTimeoutAndCancellation(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		p := h.payload
		p.Limits.TimeoutMS = 100
		p.Source = "SELECT pg_sleep(5)"
		if h.adapter.Engine() == "mysql" {
			p.Source = "SELECT SLEEP(5)"
		}
		requireVerdict(t, h.run(t, "sql-preview", p), "TimeLimitExceeded")
		p.Limits.TimeoutMS = 10000
		ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
		defer cancel()
		_, e := h.runner.Run(ctx, Job{Kind: "sql-preview", Payload: p}, h.adapter, h.profile)
		if e != ErrLostLease {
			t.Fatal("cancelled lease could produce a verdict", e)
		}
	})
}
func TestRealEnginesMarkerAndOrphanRecovery(t *testing.T) {
	forEngines(t, func(t *testing.T, h *harness) {
		a := h.adapter.(*ServerAdapter)
		badCfg := a.config
		badCfg.Marker = strings.Repeat("0", 64)
		bad, e := NewServerAdapter(badCfg, "foreign-probe", textHash("executor"))
		if e != nil {
			t.Fatal(e)
		}
		if e = bad.verifyGuard(context.Background()); e == nil {
			t.Fatal("wrong marker allowed admin access")
		}
		// Use another namespace, so startup recovery cannot interfere with this pool.
		orphan, e := NewServerAdapter(a.config, "orphan-"+t.Name(), textHash("executor"))
		if e != nil {
			t.Fatal(e)
		}
		if e = orphan.Startup(context.Background()); e != nil {
			t.Fatal(e)
		}
		if e = orphan.Prepare(context.Background(), h.payload); e != nil {
			t.Fatal(e)
		}
		sandbox, e := orphan.Create(context.Background(), h.payload)
		if e != nil {
			t.Fatal(e)
		}
		if e = orphan.Startup(context.Background()); e != nil {
			t.Fatal(e)
		}
		if c, e := native.Open(sandbox.Connection); e == nil {
			c.Close()
			t.Fatal("orphan survived startup cleanup")
		}
	})
}
