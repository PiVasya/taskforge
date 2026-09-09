package sqlworker

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
)

func TestCanonicalWireFixtures(t *testing.T) {
	for _, tc := range []struct{ raw, want string }{
		{`{"b":1.000,"a":-0,"z":[null,true,1e2,0.00120]}`, `{"a":0,"b":1e0,"z":[null,true,1e2,12e-4]}`},
		{`{"text":"<>&/\n\u0410\ud83d\ude00"}`, `{"text":"<>&/\n\u0410\ud83d\ude00"}`},
		{`{"\ue000":1,"\ud800\udc00":2}`, `{"\ud800\udc00":2e0,"\ue000":1e0}`},
		{`[1234567890123456789012345678.90,-1000,0.0,1E+003]`, `[12345678901234567890123456789e-1,-1e3,0,1e3]`},
	} {
		var value any
		if e := DecodeStrict([]byte(tc.raw), &value); e != nil {
			t.Fatal(e)
		}
		encoded, e := Canonical(value)
		if e != nil || string(encoded) != tc.want {
			t.Fatalf("canonical %s: got %s want %s (%v)", tc.raw, encoded, tc.want, e)
		}
	}
}
func TestStrictJSONRejectsAmbiguousContracts(t *testing.T) {
	for _, raw := range []string{`{"x":1,"x":2}`, `{"x":{"y":1,"y":2}}`, `[1] [2]`, `{"x":"\ud800"}`, `{"x":"\udc00"}`, `{"x":1e10001}`, `{"x":NaN}`, strings.Repeat("[", 50) + "0" + strings.Repeat("]", 50)} {
		var v any
		if e := DecodeStrict([]byte(raw), &v); e == nil {
			t.Errorf("accepted ambiguous JSON %s", raw)
		}
	}
	var limits Limits
	if e := DecodeStrict([]byte(`{"timeoutMs":100,"unexpected":true}`), &limits); e == nil {
		t.Fatal("unknown field accepted")
	}
}
func TestCanonicalIgnoresOnlyObjectOrderAndNumericSpelling(t *testing.T) {
	var a, b any
	_ = DecodeStrict([]byte(`{"a":1.0,"b":[1,2]}`), &a)
	_ = DecodeStrict([]byte(`{"b":[1,2],"a":1e0}`), &b)
	ha, _ := ContentHash(a)
	hb, _ := ContentHash(b)
	if ha != hb {
		t.Fatal("equivalent objects differ")
	}
	_ = DecodeStrict([]byte(`{"b":[2,1],"a":1}`), &b)
	hb, _ = ContentHash(b)
	if ha == hb {
		t.Fatal("array order lost")
	}
}
func TestExactDecimalNormalization(t *testing.T) {
	for _, tc := range []struct{ input, want string }{{"-0.000", "0"}, {"1.2300", "1.23"}, {"1234567890123456789012345678.90", "1234567890123456789012345678.9"}, {"1e-5", "0.00001"}, {"1e5", "100000"}} {
		got, e := NumberText(tc.input)
		if e != nil || got != tc.want {
			t.Errorf("%s -> %s %v", tc.input, got, e)
		}
	}
}
func numberResult(values ...string) ResultSet {
	r := ResultSet{Columns: []string{"n"}, Rows: [][]Cell{}}
	for _, s := range values {
		r.Rows = append(r.Rows, []Cell{{Type: "number", Value: s}})
	}
	return r
}
func comparison() Comparison {
	return Comparison{ColumnNamesMatter: true, DuplicatesMatter: true, CaseSensitive: true, NumericTolerance: "0"}
}
func TestResultMultisetAndOrderSemantics(t *testing.T) {
	o := comparison()
	yes, e := CompareResult(context.Background(), numberResult("1", "2", "1"), numberResult("1", "1", "2"), o)
	if e != nil || !yes {
		t.Fatal("unordered multiset failed", e)
	}
	yes, e = CompareResult(context.Background(), numberResult("1", "2", "1"), numberResult("1", "2", "2"), o)
	if e != nil || yes {
		t.Fatal("duplicate counts ignored", e)
	}
	o.OrderMatters = true
	yes, _ = CompareResult(context.Background(), numberResult("2", "1"), numberResult("1", "2"), o)
	if yes {
		t.Fatal("order ignored")
	}
	o.OrderMatters = false
	o.DuplicatesMatter = false
	yes, e = CompareResult(context.Background(), numberResult("1", "1", "2"), numberResult("2", "1"), o)
	if e != nil || !yes {
		t.Fatal("set comparison failed", e)
	}
}
func TestNumericToleranceUsesBipartiteMatchingNotGreedy(t *testing.T) {
	o := comparison()
	o.NumericTolerance = "0.15"
	yes, e := CompareResult(context.Background(), numberResult("0.1", "0.2"), numberResult("0.2", "0"), o)
	if e != nil || !yes {
		t.Fatal("valid matching was lost by greedy matching", e)
	}
	o.NumericTolerance = "0.01"
	yes, e = CompareResult(context.Background(), numberResult("1000000000000000000000000.01"), numberResult("1000000000000000000000000.03"), o)
	if e != nil || yes {
		t.Fatal("decimal precision was lost", e)
	}
}
func TestUnicodeDefaultCaseFold(t *testing.T) {
	for _, pair := range [][2]string{{"Stra\u00dfe", "STRASSE"}, {"\u1e9e", "ss"}, {"\ufb03", "ffi"}, {"\u03c2", "\u03c3"}, {"\u041f\u0420\u0418\u0412\u0415\u0422", "\u043f\u0440\u0438\u0432\u0435\u0442"}, {"\u0130", "i\u0307"}} {
		if fold(pair[0]) != fold(pair[1]) {
			t.Errorf("case fold mismatch: %q %q", pair[0], pair[1])
		}
	}
}
func TestNullIsNotStringNullOrNumberZero(t *testing.T) {
	o := comparison()
	a := ResultSet{Columns: []string{"x"}, Rows: [][]Cell{{{Type: "null"}}}}
	for _, v := range []Cell{{Type: "string", Value: "null"}, {Type: "number", Value: "0"}, {Type: "string", Value: ""}} {
		b := ResultSet{Columns: []string{"x"}, Rows: [][]Cell{{v}}}
		yes, e := CompareResult(context.Background(), a, b, o)
		if e != nil || yes {
			t.Fatal("NULL conflated with other type", e)
		}
	}
}
func TestScriptDialectLexing(t *testing.T) {
	cases := []struct {
		engine, sql string
		count       int
	}{{"sqlite", "SELECT ';'; SELECT 2", 2}, {"mysql", "SELECT 'a;''b'; # comment\nSELECT 2", 2}, {"postgresql", "SELECT $x$semi;colon$x$; /* outer /* nested */ still */ SELECT 2", 2}, {"postgresql", `SELECT E'a\';b'; SELECT 2`, 2}, {"sqlite", `SELECT "semi;colon"; -- tail`, 1}}
	for _, tc := range cases {
		stmts, e := SplitScript(tc.sql, tc.engine, true, 20)
		if e != nil || len(stmts) != tc.count {
			t.Errorf("%s: %v count=%d", tc.sql, e, len(stmts))
		}
	}
	for _, sql := range []string{"SELECT 1; SELECT 2", "SELECT 'unterminated", "/*!50000 SELECT 1 */", "SELECT 1 /* unterminated", "SELECT 1\x00"} {
		if _, e := SplitScript(sql, "mysql", false, 20); e == nil {
			t.Errorf("accepted %q", sql)
		}
	}
}
func TestNormalProfileRejectsServerAndFileCommands(t *testing.T) {
	for _, engine := range []string{"sqlite", "postgresql", "mysql"} {
		for _, sql := range []string{"CREATE ROLE foo", "CREATE DATABASE foo", "DROP DATABASE foo", "SET search_path=public", "SELECT pg_read_file('/etc/passwd')", "SELECT load_file('/etc/passwd')", "SELECT 1 INTO OUTFILE '/tmp/x'", "SELECT set_config('work_mem','1GB',false)", `SELECT "pg_read_file"('/etc/passwd')`} {
			if _, e := SplitScript(sql, engine, true, 20); e == nil {
				t.Errorf("%s allowed %s", engine, sql)
			}
		}
	}
	for _, sql := range []string{"CREATE TABLE t(id INTEGER)", "ALTER TABLE t ADD COLUMN name TEXT", "DROP TABLE t", "CREATE INDEX idx ON t(id)", "CREATE VIEW v AS SELECT id FROM t", "WITH a AS (SELECT 1) SELECT * FROM a"} {
		if _, e := SplitScript(sql, "postgresql", true, 20); e != nil {
			t.Errorf("valid normal operation rejected: %s %v", sql, e)
		}
	}
}
func TestPortableDDLAllThreeEngines(t *testing.T) {
	p := fixture(Profile{})
	for _, engine := range []string{"postgresql", "mysql", "sqlite"} {
		plan, e := CompileDataset(p, engine)
		if e != nil {
			t.Fatalf("%s: %v", engine, e)
		}
		all := strings.Join(plan.Creates, " ")
		switch engine {
		case "postgresql":
			if !strings.Contains(all, "GENERATED BY DEFAULT AS IDENTITY") {
				t.Fatal("PG identity missing")
			}
		case "mysql":
			if !strings.Contains(all, "AUTO_INCREMENT") || !strings.Contains(all, "InnoDB") {
				t.Fatal("MySQL DDL missing")
			}
		case "sqlite":
			if !strings.Contains(all, "AUTOINCREMENT") {
				t.Fatal("SQLite identity missing")
			}
		}
		if len(plan.Inserts) != 2 {
			t.Fatal("seed rows missing")
		}
	}
}
func TestDatasetEngineOverridesAndExactSeeds(t *testing.T) {
	p := fixture(Profile{})
	p.EngineOverrides = map[string]EngineMapping{"mysql": {Columns: map[string]ColumnMapping{"products.name": {Type: ptr("varchar(100)")}}}}
	my, e := CompileDataset(p, "mysql")
	if e != nil {
		t.Fatal(e)
	}
	pg, e := CompileDataset(p, "postgresql")
	if e != nil {
		t.Fatal(e)
	}
	if !strings.Contains(my.Creates[0], "VARCHAR(100)") || strings.Contains(pg.Creates[0], "VARCHAR(100)") {
		t.Fatal("override crossed engine boundary")
	}
	p.Seed["products"][0]["price"] = json.Number("1.234")
	if _, e = CompileDataset(p, "postgresql"); e == nil {
		t.Fatal("over-precision decimal silently rounded")
	}
}
func TestNoClockDefaultsInImmutableSeed(t *testing.T) {
	p := fixture(Profile{})
	p.Definition.Tables[0].Columns = append(p.Definition.Tables[0].Columns, Column{Name: "created", Type: "datetime", Default: &Default{Kind: "current_timestamp"}})
	if _, e := CompileDataset(p, "sqlite"); e == nil {
		t.Fatal("non-repeatable seed default accepted")
	}
}
