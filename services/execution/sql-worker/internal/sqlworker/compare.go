package sqlworker

import (
	"context"
	"encoding/json"
	"fmt"
	"math/big"
	"sort"
	"strings"
	"time"
	"unicode"
)

// Default Unicode case folding is independent of the DB collation. Multi-rune
// expansions preserve the previous worker's casefold semantics (e.g. sharp S).
func fold(s string) string {
	simple := func(r rune) rune {
		smallest := r
		for next := unicode.SimpleFold(r); next != r; next = unicode.SimpleFold(next) {
			if next < smallest {
				smallest = next
			}
		}
		return smallest
	}
	var b strings.Builder
	for _, r := range s {
		if expanded, ok := fullFoldExpansions[r]; ok {
			for _, part := range expanded {
				b.WriteRune(simple(part))
			}
		} else {
			b.WriteRune(simple(r))
		}
	}
	return b.String()
}

type compareBudget struct {
	ctx   context.Context
	cells int
}

func (b *compareBudget) check(n int) error {
	b.cells += n
	if b.cells > 2_000_000 || b.ctx.Err() != nil {
		return &Failure{PublicError{"SQL_VERIFICATION_LIMIT", "Comparison exceeded its bounded verification budget."}, "OutputLimitExceeded"}
	}
	return nil
}
func cellKey(c Cell, settings Comparison) (Cell, error) {
	if c.Type == "number" {
		s, ok := c.Value.(string)
		if !ok {
			return Cell{}, Unavailable("Invalid numeric artifact cell.")
		}
		n, e := NumberText(s)
		if e != nil {
			return Cell{}, e
		}
		c.Value = n
	}
	if c.Type == "string" && !settings.CaseSensitive {
		s, ok := c.Value.(string)
		if !ok {
			return Cell{}, Unavailable("Invalid string artifact cell.")
		}
		c.Value = fold(s)
	}
	if c.Type == "null" {
		c.Value = nil
	}
	return c, nil
}
func rowKey(row []Cell, settings Comparison) (string, error) {
	normalized := make([]Cell, len(row))
	for i, c := range row {
		x, e := cellKey(c, settings)
		if e != nil {
			return "", e
		}
		normalized[i] = x
	}
	b, e := json.Marshal(normalized)
	return string(b), e
}
func uniqueRows(rows [][]Cell, o Comparison) ([][]Cell, error) {
	seen := map[string]bool{}
	out := [][]Cell{}
	for _, r := range rows {
		k, e := rowKey(r, o)
		if e != nil {
			return nil, e
		}
		if !seen[k] {
			seen[k] = true
			out = append(out, r)
		}
	}
	return out, nil
}
func sameCell(a, b Cell, o Comparison, tolerance *big.Rat) (bool, error) {
	if a.Type != b.Type {
		return false, nil
	}
	if a.Type == "null" {
		return true, nil
	}
	if a.Type == "number" {
		x, ok := a.Value.(string)
		if !ok {
			return false, Unavailable("Invalid expected numeric cell.")
		}
		y, ok := b.Value.(string)
		if !ok {
			return false, Unavailable("Invalid expected numeric cell.")
		}
		ar, e := parseDecimal(x)
		if e != nil {
			return false, e
		}
		br, e := parseDecimal(y)
		if e != nil {
			return false, e
		}
		delta := new(big.Rat).Sub(ar, br)
		delta.Abs(delta)
		return delta.Cmp(tolerance) <= 0, nil
	}
	if a.Type == "string" && !o.CaseSensitive {
		x, ok := a.Value.(string)
		y, ok2 := b.Value.(string)
		return ok && ok2 && fold(x) == fold(y), nil
	}
	return sameJSON(a.Value, b.Value), nil
}
func sameRow(a, b []Cell, o Comparison, t *big.Rat, budget *compareBudget) (bool, error) {
	if e := budget.check(max(1, len(a))); e != nil {
		return false, e
	}
	if len(a) != len(b) {
		return false, nil
	}
	for i := range a {
		yes, e := sameCell(a[i], b[i], o, t)
		if e != nil || !yes {
			return yes, e
		}
	}
	return true, nil
}
func CompareResult(ctx context.Context, actual, expected ResultSet, o Comparison) (bool, error) {
	ctx, cancel := context.WithTimeout(ctx, 2500*time.Millisecond)
	defer cancel()
	return compareResult(actual, expected, o, &compareBudget{ctx: ctx})
}
func compareResult(actual, expected ResultSet, o Comparison, budget *compareBudget) (bool, error) {
	if len(actual.Columns) != len(expected.Columns) {
		return false, nil
	}
	if o.ColumnNamesMatter && !sameJSON(actual.Columns, expected.Columns) {
		return false, nil
	}
	left, right := actual.Rows, expected.Rows
	var err error
	if !o.DuplicatesMatter {
		left, err = uniqueRows(left, o)
		if err != nil {
			return false, err
		}
		right, err = uniqueRows(right, o)
		if err != nil {
			return false, err
		}
	}
	if len(left) != len(right) {
		return false, nil
	}
	if len(left) > 1000 {
		return false, OutputLimit()
	}
	tol := o.NumericTolerance.String()
	if tol == "" {
		tol = "0"
	}
	t, e := parseDecimal(tol)
	if e != nil || t.Sign() < 0 || t.Cmp(big.NewRat(1000000, 1)) > 0 {
		return false, Unavailable("Invalid numeric tolerance.")
	}
	if o.OrderMatters {
		for i := range left {
			ok, e := sameRow(left[i], right[i], o, t, budget)
			if e != nil || !ok {
				return ok, e
			}
		}
		return true, nil
	}
	counts := map[string]int{}
	for _, r := range left {
		k, e := rowKey(r, o)
		if e != nil {
			return false, e
		}
		counts[k]++
	}
	for _, r := range right {
		k, e := rowKey(r, o)
		if e != nil {
			return false, e
		}
		counts[k]--
	}
	exact := true
	for _, c := range counts {
		if c != 0 {
			exact = false
			break
		}
	}
	if exact || t.Sign() == 0 {
		return exact, nil
	}
	// Numeric tolerance is not transitive. Build a full bipartite matching, not a
	// greedy nearest-row pairing which can reject a valid result multiset.
	adj := make([][]int, len(left))
	for i, a := range left {
		for j, b := range right {
			ok, e := sameRow(a, b, o, t, budget)
			if e != nil {
				return false, e
			}
			if ok {
				adj[i] = append(adj[i], j)
			}
		}
		if len(adj[i]) == 0 {
			return false, nil
		}
	}
	mr, ml := make([]int, len(left)), make([]int, len(left))
	order := make([]int, len(left))
	for i := range mr {
		mr[i] = -1
		ml[i] = -1
		order[i] = i
	}
	sort.Slice(order, func(i, j int) bool { return len(adj[order[i]]) < len(adj[order[j]]) })
	for _, root := range order {
		stack := []int{root}
		seenL := make([]bool, len(left))
		seenR := make([]bool, len(right))
		parents := make([]int, len(right))
		seenL[root] = true
		free := -1
		for len(stack) > 0 && free < 0 {
			i := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			for _, j := range adj[i] {
				if e := budget.check(1); e != nil {
					return false, e
				}
				if seenR[j] {
					continue
				}
				seenR[j] = true
				parents[j] = i
				if mr[j] < 0 {
					free = j
					break
				}
				if !seenL[mr[j]] {
					seenL[mr[j]] = true
					stack = append(stack, mr[j])
				}
			}
		}
		if free < 0 {
			return false, nil
		}
		for j := free; j >= 0; {
			i := parents[j]
			old := ml[i]
			mr[j] = i
			ml[i] = j
			j = old
		}
	}
	return true, nil
}
func canonicalSchema(schema Schema, settings SchemaCheck) (Schema, error) {
	out := Schema{Tables: []map[string]any{}}
	byName := map[string]map[string]any{}
	for _, t := range schema.Tables {
		name, ok := t["name"].(string)
		if !ok {
			return out, Unavailable("Invalid schema artifact.")
		}
		byName[name] = t
	}
	names := []string{}
	if len(settings.Tables) > 0 {
		for _, n := range settings.Tables {
			if _, ok := byName[n]; !ok {
				return out, Fail("SQL_CHECK_TABLE_MISSING", "A configured verification table is missing.")
			}
			names = append(names, n)
		}
	} else {
		for n := range byName {
			names = append(names, n)
		}
	}
	sort.Strings(names)
	allowed := map[string]bool{}
	for _, k := range strings.Fields("name kind columns primaryKey foreignKeys unique indexes viewDefinition checks primaryKeyName options") {
		allowed[k] = true
	}
	for _, name := range names {
		raw, e := json.Marshal(byName[name])
		if e != nil {
			return out, e
		}
		var copy map[string]any
		if e = DecodeStrict(raw, &copy); e != nil {
			return out, e
		}
		item := map[string]any{}
		for k, v := range copy {
			if allowed[k] {
				item[k] = v
			}
		}
		if cols, ok := item["columns"].([]any); ok {
			for _, v := range cols {
				c, ok := v.(map[string]any)
				if !ok {
					return out, Unavailable("Invalid schema column artifact.")
				}
				delete(c, "nativeType")
				if !settings.DefaultsMatter {
					delete(c, "default")
				}
				if !settings.ConstraintNamesMatter {
					delete(c, "notNullName")
				}
			}
		}
		if !settings.ConstraintNamesMatter {
			delete(item, "primaryKeyName")
		}
		if !settings.IndexesMatter {
			delete(item, "indexes")
		}
		for _, key := range []string{"foreignKeys", "unique", "indexes", "checks"} {
			values, ok := item[key].([]any)
			if !ok {
				continue
			}
			for _, v := range values {
				m, ok := v.(map[string]any)
				if !ok {
					return out, Unavailable("Invalid schema constraint.")
				}
				if !settings.ConstraintNamesMatter {
					delete(m, "name")
				}
			}
			sort.Slice(values, func(i, j int) bool {
				a, _ := Canonical(values[i])
				b, _ := Canonical(values[j])
				return string(a) < string(b)
			})
			item[key] = values
		}
		out.Tables = append(out.Tables, item)
	}
	return out, nil
}
func (s Artifact) MarshalJSON() ([]byte, error) {
	switch s.Mode {
	case "result":
		return json.Marshal(map[string]any{"formatVersion": s.FormatVersion, "mode": s.Mode, "result": s.Result})
	case "state":
		m := s.Tables
		if m == nil {
			m = map[string]ResultSet{}
		}
		return json.Marshal(map[string]any{"formatVersion": s.FormatVersion, "mode": s.Mode, "tables": m})
	case "schema":
		return json.Marshal(map[string]any{"formatVersion": s.FormatVersion, "mode": s.Mode, "schema": s.Schema})
	}
	return nil, fmt.Errorf("unsupported artifact mode")
}
func MakeArtifact(snapshot Snapshot, p Payload) (Artifact, error) {
	a := Artifact{FormatVersion: 1, Mode: p.Mode}
	switch p.Mode {
	case "result":
		if len(snapshot.Results) == 0 {
			return a, Fail("SQL_RESULT_REQUIRED", "This assignment must produce a result set.")
		}
		r := snapshot.Results[len(snapshot.Results)-1]
		a.Result = &r
	case "state":
		a.Tables = map[string]ResultSet{}
		if len(p.StateCheck.Tables) > 0 {
			for _, n := range p.StateCheck.Tables {
				v, ok := snapshot.VerificationData[n]
				if !ok {
					return a, Fail("SQL_CHECK_TABLE_MISSING", "A configured state table is missing.")
				}
				a.Tables[n] = v
			}
		} else {
			for n, v := range snapshot.VerificationData {
				a.Tables[n] = v
			}
		}
	case "schema":
		s, e := canonicalSchema(snapshot.Schema, p.SchemaCheck)
		if e != nil {
			return a, e
		}
		a.Schema = &s
	default:
		return a, Unavailable("Unknown verification mode.")
	}
	return a, nil
}
func Verify(ctx context.Context, a, b Artifact, p Payload) (bool, error) {
	if a.FormatVersion != 1 || b.FormatVersion != 1 || a.Mode != b.Mode {
		return false, Unavailable("Unsupported expected artifact format.")
	}
	switch a.Mode {
	case "result":
		if a.Result == nil || b.Result == nil {
			return false, Unavailable("Missing expected result.")
		}
		return CompareResult(ctx, *a.Result, *b.Result, p.Comparison)
	case "state":
		if len(a.Tables) != len(b.Tables) {
			return false, nil
		}
		o := p.Comparison
		o.OrderMatters = false
		o.DuplicatesMatter = true
		o.ColumnNamesMatter = true
		ctx, cancel := context.WithTimeout(ctx, 2500*time.Millisecond)
		defer cancel()
		budget := &compareBudget{ctx: ctx}
		for k, av := range a.Tables {
			bv, ok := b.Tables[k]
			if !ok {
				return false, nil
			}
			ok, e := compareResult(av, bv, o, budget)
			if e != nil || !ok {
				return ok, e
			}
		}
		return true, nil
	case "schema":
		if a.Schema == nil || b.Schema == nil {
			return false, Unavailable("Missing expected schema.")
		}
		return sameJSON(a.Schema, b.Schema), nil
	}
	return false, Unavailable("Unknown expected artifact mode.")
}
