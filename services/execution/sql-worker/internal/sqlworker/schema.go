package sqlworker

import (
	"encoding/json"
	"fmt"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"unicode"

	"taskforge/sqlworker/internal/native"
)

type MetadataSession interface {
	Meta(string) ([][]native.Value, error)
}

func sVal(v native.Value) string { return v.Text }
func bVal(v native.Value) bool {
	return !v.Null && (v.Text == "t" || v.Text == "true" || v.Text == "1" || v.Text == "YES")
}
func iVal(v native.Value) int { n, _ := strconv.Atoi(v.Text); return n }
func nullableText(v native.Value) any {
	if v.Null {
		return nil
	}
	return v.Text
}
func nullableExpr(v native.Value) any {
	if v.Null {
		return nil
	}
	return Expression(v.Text)
}
func optionalName(s string) any {
	if s == "" {
		return nil
	}
	return s
}
func contains(a []string, s string) bool {
	for _, v := range a {
		if v == s {
			return true
		}
	}
	return false
}
func indexOf(a []string, s string) int {
	for i, v := range a {
		if v == s {
			return i
		}
	}
	return -1
}
func cutColumns(a []string) []string {
	out := []string{}
	for _, v := range a {
		if v != "," {
			out = append(out, v)
		}
	}
	return out
}
func skipToClose(tokens []string, start int) int {
	d := 1
	for j := start; j < len(tokens); j++ {
		if tokens[j] == "(" {
			d++
		}
		if tokens[j] == ")" {
			d--
			if d == 0 {
				return j
			}
		}
	}
	return -1
}

// SQLTokens is only a metadata normalizer. Authorization uses DB permissions and
// SplitScript; this function never decides whether student SQL may execute.
func SQLTokens(sql string) []string {
	s := []rune(sql)
	out := []string{}
	for i := 0; i < len(s); {
		if unicode.IsSpace(s[i]) {
			i++
			continue
		}
		if i+1 < len(s) && s[i] == '-' && s[i+1] == '-' {
			for i < len(s) && s[i] != '\n' {
				i++
			}
			continue
		}
		if i+1 < len(s) && s[i] == '/' && s[i+1] == '*' {
			i += 2
			for i+1 < len(s) && !(s[i] == '*' && s[i+1] == '/') {
				i++
			}
			i = min(len(s), i+2)
			continue
		}
		c := s[i]
		if c == '"' || c == '\'' || c == '`' || c == '[' {
			end := c
			if c == '[' {
				end = ']'
			}
			i++
			v := []rune{}
			for i < len(s) {
				if s[i] == end {
					if i+1 < len(s) && s[i+1] == end {
						v = append(v, end)
						i += 2
						continue
					}
					i++
					break
				}
				v = append(v, s[i])
				i++
			}
			text := string(v)
			if c == '\'' {
				text = textLiteral(text)
			}
			out = append(out, text)
			continue
		}
		if unicode.IsLetter(c) || c == '_' {
			j := i + 1
			for j < len(s) && (unicode.IsLetter(s[j]) || unicode.IsDigit(s[j]) || s[j] == '_' || s[j] == '$') {
				j++
			}
			out = append(out, strings.ToLower(string(s[i:j])))
			i = j
			continue
		}
		if c >= '0' && c <= '9' {
			j := i + 1
			for j < len(s) && ((s[j] >= '0' && s[j] <= '9') || s[j] == '.') {
				j++
			}
			if j < len(s) && (s[j] == 'e' || s[j] == 'E') {
				j++
				if j < len(s) && (s[j] == '+' || s[j] == '-') {
					j++
				}
				for j < len(s) && s[j] >= '0' && s[j] <= '9' {
					j++
				}
			}
			out = append(out, strings.ToLower(string(s[i:j])))
			i = j
			continue
		}
		op := ""
		for _, v := range []string{"->>", "::", ">=", "<=", "<>", "!=", "||", "->"} {
			r := []rune(v)
			if i+len(r) <= len(s) && string(s[i:i+len(r)]) == v {
				op = v
				break
			}
		}
		if op == "" {
			op = string(c)
		}
		out = append(out, op)
		i += len([]rune(op))
	}
	return out
}
func Expression(sql string) string {
	tokens := SQLTokens(sql)
	for len(tokens) > 1 && tokens[0] == "(" && tokens[len(tokens)-1] == ")" {
		if skipToClose(tokens, 1) != len(tokens)-1 {
			break
		}
		tokens = tokens[1 : len(tokens)-1]
	}
	return strings.Join(tokens, " ")
}

var typeArgsRE = regexp.MustCompile(`\(([0-9]+)(?:,\s*([0-9]+))?\)`)
var typeBaseRE = regexp.MustCompile(`\([^)]*\)`)

func TypeInfo(nativeType string) map[string]any {
	text := strings.Join(strings.Fields(strings.ToLower(nativeType)), " ")
	args := typeArgsRE.FindStringSubmatch(text)
	base := strings.TrimSpace(typeBaseRE.ReplaceAllString(text, ""))
	out := map[string]any{"nativeType": nativeType}
	first := 0
	if len(args) > 1 {
		first, _ = strconv.Atoi(args[1])
	}
	switch base {
	case "int", "int4", "integer", "mediumint", "smallint", "int2", "tinyint":
		out["logicalType"] = "integer"
		if base == "tinyint" && first == 1 {
			out["logicalType"] = "boolean"
		} else if base == "smallint" || base == "int2" || base == "tinyint" || base == "mediumint" {
			out["integerWidth"] = base
		}
	case "bigint", "int8":
		out["logicalType"] = "bigint"
	case "numeric", "decimal", "number":
		out["logicalType"] = "decimal"
		out["precision"] = nil
		out["scale"] = nil
		if len(args) > 1 {
			scale, _ := strconv.Atoi(args[2])
			out["precision"] = first
			out["scale"] = scale
		}
	case "varchar", "character varying", "nvarchar", "char", "character", "nchar":
		out["logicalType"] = "string"
		out["length"] = nil
		if len(args) > 1 {
			out["length"] = first
		}
		out["fixedLength"] = base == "char" || base == "character" || base == "nchar"
	case "text", "tinytext", "mediumtext", "longtext", "clob":
		out["logicalType"] = "text"
	case "bool", "boolean":
		out["logicalType"] = "boolean"
	case "date":
		out["logicalType"] = "date"
	case "timestamp without time zone", "timestamp", "datetime", "timestamp with time zone", "timestamptz":
		out["logicalType"] = "datetime"
		out["timezone"] = base == "timestamp with time zone" || base == "timestamptz"
		if len(args) > 1 {
			out["precision"] = first
		}
	case "uuid":
		out["logicalType"] = "uuid"
	case "blob", "bytea", "binary", "varbinary", "tinyblob", "mediumblob", "longblob":
		out["logicalType"] = "binary"
	case "real", "double precision", "double", "float":
		out["logicalType"] = "float"
	default:
		out["logicalType"] = text
	}
	return out
}

type sqliteExtras struct {
	checks                    []map[string]any
	fkNames                   map[string][]string
	collation                 map[string]string
	primary                   string
	uniqueNames, notnullNames map[string]string
	tokens                    []string
}

func sqliteDDL(ddl string) (sqliteExtras, error) {
	tokens := SQLTokens(ddl)
	x := sqliteExtras{checks: []map[string]any{}, fkNames: map[string][]string{}, collation: map[string]string{}, uniqueNames: map[string]string{}, notnullNames: map[string]string{}, tokens: tokens}
	for i, t := range tokens {
		if t == "check" && i+1 < len(tokens) && tokens[i+1] == "(" {
			end := skipToClose(tokens, i+2)
			if end < 0 {
				return x, Fail("SQL_SCHEMA_INSPECTION", "Cannot inspect CHECK expression.")
			}
			name := ""
			if i >= 2 && tokens[i-2] == "constraint" {
				name = tokens[i-1]
			}
			x.checks = append(x.checks, map[string]any{"name": optionalName(name), "expression": strings.Join(tokens[i+2:end], " ")})
		}
		if t == "foreign" && i+2 < len(tokens) && tokens[i+1] == "key" && tokens[i+2] == "(" {
			end := skipToClose(tokens, i+3)
			if end >= 0 {
				name := ""
				if i >= 2 && tokens[i-2] == "constraint" {
					name = tokens[i-1]
				}
				x.fkNames[name] = cutColumns(tokens[i+3 : end])
			}
		}
	}
	segments := [][]string{}
	depth := 0
	current := []string{}
	for _, t := range tokens {
		if t == "(" {
			depth++
			if depth == 1 {
				continue
			}
		} else if t == ")" {
			depth--
			if depth == 0 {
				if len(current) > 0 {
					segments = append(segments, current)
				}
				break
			}
		}
		if depth == 1 && t == "," {
			segments = append(segments, current)
			current = []string{}
			continue
		}
		if depth >= 1 {
			current = append(current, t)
		}
	}
	for _, seg := range segments {
		if len(seg) == 0 {
			continue
		}
		inline := !contains([]string{"constraint", "primary", "foreign", "unique", "check"}, seg[0])
		col := ""
		start := 0
		if inline {
			col = seg[0]
			start = 1
			if at := indexOf(seg, "collate"); at >= 0 && at+1 < len(seg) {
				x.collation[col] = seg[at+1]
			}
		}
		d := 0
		pending := ""
		for i := start; i < len(seg); i++ {
			t := seg[i]
			if t == "(" {
				d++
				continue
			}
			if t == ")" {
				d--
				continue
			}
			if d != 0 {
				continue
			}
			switch t {
			case "constraint":
				if i+1 < len(seg) {
					pending = seg[i+1]
					i++
				}
			case "primary":
				if i+1 < len(seg) && seg[i+1] == "key" {
					x.primary = pending
					pending = ""
				}
			case "unique":
				cols := []string{col}
				if !inline {
					cols = []string{}
					if i+1 < len(seg) && seg[i+1] == "(" {
						end := skipToClose(seg, i+2)
						if end < 0 {
							return x, Fail("SQL_SCHEMA_INSPECTION", "Invalid UNIQUE metadata.")
						}
						cols = cutColumns(seg[i+2 : end])
					}
				}
				x.uniqueNames[strings.Join(cols, "\x00")] = pending
				pending = ""
			case "not":
				if col != "" && i+1 < len(seg) && seg[i+1] == "null" {
					x.notnullNames[col] = pending
				}
				pending = ""
			case "references":
				if col != "" {
					x.fkNames[pending] = []string{col}
				}
				pending = ""
			case "check", "default", "collate":
				pending = ""
			}
		}
	}
	return x, nil
}
func InspectSchema(s MetadataSession, engine string) (Schema, error) {
	switch engine {
	case "sqlite":
		return inspectSQLite(s)
	case "postgresql":
		return inspectPostgres(s)
	case "mysql":
		return inspectMySQL(s)
	}
	return Schema{}, Unavailable("Unsupported schema adapter.")
}
func inspectSQLite(s MetadataSession) (Schema, error) {
	out := Schema{Tables: []map[string]any{}}
	rows, e := s.Meta("SELECT name,type,sql FROM sqlite_schema WHERE type IN ('table','view') AND substr(name,1,7)<>'sqlite_' ORDER BY name LIMIT 65")
	if e != nil {
		return out, e
	}
	if len(rows) > 64 {
		return out, OutputLimit()
	}
	for _, r := range rows {
		if len(r) != 3 {
			return out, Unavailable("Invalid SQLite metadata.")
		}
		name, kind, ddl := sVal(r[0]), sVal(r[1]), sVal(r[2])
		quoted := q(name, "sqlite")
		info, e := s.Meta("PRAGMA table_xinfo(" + quoted + ")")
		if e != nil {
			return out, e
		}
		if len(info) > 256 {
			return out, OutputLimit()
		}
		pkRows := [][]native.Value{}
		for _, c := range info {
			if len(c) < 7 {
				return out, Unavailable("Invalid SQLite column metadata.")
			}
			if iVal(c[6]) != 0 {
				return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Generated/hidden columns need an advanced schema profile.")
			}
			if iVal(c[5]) > 0 {
				pkRows = append(pkRows, c)
			}
		}
		sort.Slice(pkRows, func(i, j int) bool { return iVal(pkRows[i][5]) < iVal(pkRows[j][5]) })
		pk := []string{}
		for _, c := range pkRows {
			pk = append(pk, sVal(c[1]))
		}
		extras, e := sqliteDDL(ddl)
		if e != nil {
			return out, e
		}
		if contains(extras.tokens, "deferrable") || contains(extras.tokens, "conflict") || contains(extras.tokens, "desc") {
			return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Deferred/conflict/descending inline constraints need an advanced schema profile.")
		}
		columns := []map[string]any{}
		for _, c := range info {
			col, nt := sVal(c[1]), sVal(c[2])
			identity := iVal(c[5]) > 0 && len(pk) == 1 && strings.EqualFold(nt, "INTEGER")
			item := TypeInfo(nt)
			item["name"] = col
			item["nullable"] = !bVal(c[3]) && !identity
			item["default"] = nullableExpr(c[4])
			item["identity"] = identity
			coll := extras.collation[col]
			if coll == "" {
				coll = "binary"
			}
			item["collation"] = coll
			item["notNullName"] = optionalName(extras.notnullNames[col])
			columns = append(columns, item)
		}
		fkRows, e := s.Meta("PRAGMA foreign_key_list(" + quoted + ")")
		if e != nil {
			return out, e
		}
		groups := map[int][][]native.Value{}
		for _, r := range fkRows {
			if len(r) < 8 {
				return out, Unavailable("Invalid SQLite foreign key.")
			}
			groups[iVal(r[0])] = append(groups[iVal(r[0])], r)
		}
		ids := []int{}
		for id := range groups {
			ids = append(ids, id)
		}
		sort.Ints(ids)
		foreign := []map[string]any{}
		for _, id := range ids {
			g := groups[id]
			sort.Slice(g, func(i, j int) bool { return iVal(g[i][1]) < iVal(g[j][1]) })
			cols, refs := []string{}, []any{}
			for _, r := range g {
				cols = append(cols, sVal(r[3]))
				refs = append(refs, nullableText(r[4]))
			}
			fkname := ""
			keys := []string{}
			for k := range extras.fkNames {
				keys = append(keys, k)
			}
			sort.Strings(keys)
			for _, k := range keys {
				if sameJSON(extras.fkNames[k], cols) {
					fkname = k
					break
				}
			}
			foreign = append(foreign, map[string]any{"name": optionalName(fkname), "columns": cols, "referenceTable": sVal(g[0][2]), "referenceColumns": refs, "onUpdate": strings.ReplaceAll(strings.ToLower(sVal(g[0][5])), " ", "_"), "onDelete": strings.ReplaceAll(strings.ToLower(sVal(g[0][6])), " ", "_")})
		}
		idxRows, e := s.Meta("PRAGMA index_list(" + quoted + ")")
		if e != nil {
			return out, e
		}
		unique, indexes := []map[string]any{}, []map[string]any{}
		for _, idx := range idxRows {
			if len(idx) < 5 {
				return out, Unavailable("Invalid SQLite index metadata.")
			}
			idxName, origin := sVal(idx[1]), sVal(idx[3])
			if origin == "pk" {
				continue
			}
			parts, e := s.Meta("PRAGMA index_xinfo(" + q(idxName, "sqlite") + ")")
			if e != nil {
				return out, e
			}
			cols := []any{}
			plainCols := []string{}
			for _, part := range parts {
				if len(part) < 6 {
					return out, Unavailable("Invalid SQLite index component.")
				}
				if bVal(part[5]) {
					cols = append(cols, nullableText(part[2]))
					plainCols = append(plainCols, sVal(part[2]))
				}
			}
			if origin == "u" {
				unique = append(unique, map[string]any{"name": optionalName(extras.uniqueNames[strings.Join(plainCols, "\x00")]), "columns": cols})
			} else {
				def, e := s.Meta("SELECT sql FROM sqlite_schema WHERE type='index' AND name=" + textLiteral(idxName))
				if e != nil {
					return out, e
				}
				tokens := []string{}
				if len(def) > 0 {
					tokens = SQLTokens(sVal(def[0][0]))
				}
				at := indexOf(tokens, "on")
				if at >= 0 && at+2 < len(tokens) {
					tokens = tokens[at+2:]
				}
				indexes = append(indexes, map[string]any{"name": idxName, "columns": cols, "unique": bVal(idx[2]), "definition": strings.Join(tokens, " "), "partial": bVal(idx[4])})
			}
		}
		lastClose := -1
		for i, t := range extras.tokens {
			if t == ")" {
				lastClose = i
			}
		}
		strict := lastClose >= 0 && contains(extras.tokens[lastClose+1:], "strict")
		table := map[string]any{"name": name, "kind": kind, "columns": columns, "primaryKey": pk, "foreignKeys": foreign, "unique": unique, "indexes": indexes, "checks": extras.checks, "primaryKeyName": optionalName(extras.primary), "options": map[string]any{"withoutRowid": contains(extras.tokens, "without") && contains(extras.tokens, "rowid"), "strict": strict, "autoincrement": contains(extras.tokens, "autoincrement")}}
		if kind == "view" {
			at := indexOf(extras.tokens, "as")
			if at >= 0 {
				table["viewDefinition"] = strings.Join(extras.tokens[at+1:], " ")
			} else {
				table["viewDefinition"] = Expression(ddl)
			}
		}
		out.Tables = append(out.Tables, table)
	}
	return out, nil
}
func pgColumns(v native.Value) ([]string, error) {
	var a []string
	if e := DecodeStrict([]byte(v.Text), &a); e != nil {
		return nil, Unavailable("Invalid PostgreSQL constraint metadata.")
	}
	if a == nil {
		a = []string{}
	}
	return a, nil
}
func inspectPostgres(s MetadataSession) (Schema, error) {
	out := Schema{Tables: []map[string]any{}}
	rows, e := s.Meta("SELECT c.relname,c.relkind,c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f') ORDER BY c.relname LIMIT 65")
	if e != nil {
		return out, e
	}
	if len(rows) > 64 {
		return out, OutputLimit()
	}
	for _, r := range rows {
		if len(r) != 3 {
			return out, Unavailable("Invalid PostgreSQL metadata.")
		}
		name, kind := sVal(r[0]), sVal(r[1])
		if kind != "r" && kind != "v" {
			return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Partitioned, materialized and foreign tables need an advanced profile.")
		}
		oid, err := strconv.ParseUint(sVal(r[2]), 10, 32)
		if err != nil {
			return out, Unavailable("Invalid PostgreSQL table identity.")
		}
		id := strconv.FormatUint(oid, 10)
		colRows, e := s.Meta(`SELECT a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,pg_catalog.pg_get_expr(d.adbin,d.adrelid),a.attidentity,a.attgenerated,CASE WHEN a.attcollation<>t.typcollation THEN col.collname ELSE NULL END FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_type t ON t.oid=a.atttypid LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum LEFT JOIN pg_catalog.pg_collation col ON col.oid=a.attcollation WHERE a.attrelid=` + id + ` AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum`)
		if e != nil {
			return out, e
		}
		if len(colRows) > 256 {
			return out, OutputLimit()
		}
		columns := []map[string]any{}
		for _, c := range colRows {
			if len(c) != 7 {
				return out, Unavailable("Invalid PostgreSQL column metadata.")
			}
			if sVal(c[5]) != "" {
				return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Generated columns need an advanced schema profile.")
			}
			item := TypeInfo(sVal(c[1]))
			item["name"] = sVal(c[0])
			item["nullable"] = !bVal(c[2])
			item["default"] = nullableExpr(c[3])
			identity := sVal(c[4])
			if identity != "" {
				item["default"] = nil
			}
			item["identity"] = identity != ""
			item["identityMode"] = identity
			item["collation"] = nullableText(c[6])
			columns = append(columns, item)
		}
		cons, e := s.Meta(`SELECT c.conname,c.contype,to_json(ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY AS k(num,ord) JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.num ORDER BY k.ord))::text,r.relname,to_json(ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY AS k(num,ord) JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.num ORDER BY k.ord))::text,c.confupdtype,c.confdeltype,pg_catalog.pg_get_constraintdef(c.oid,true),c.condeferrable,c.condeferred,c.convalidated,c.conenforced,c.confmatchtype FROM pg_catalog.pg_constraint c LEFT JOIN pg_catalog.pg_class r ON r.oid=c.confrelid WHERE c.conrelid=` + id + ` ORDER BY c.conname`)
		if e != nil {
			return out, e
		}
		pk := []string{}
		primary := ""
		unique, foreign, checks := []map[string]any{}, []map[string]any{}, []map[string]any{}
		actions := map[string]string{"a": "no_action", "r": "restrict", "c": "cascade", "n": "set_null", "d": "set_default"}
		for _, c := range cons {
			if len(c) != 13 {
				return out, Unavailable("Invalid PostgreSQL constraint metadata.")
			}
			ctype := sVal(c[1])
			def := sVal(c[7])
			if bVal(c[8]) || bVal(c[9]) || !bVal(c[10]) || !bVal(c[11]) || (ctype == "f" && sVal(c[12]) != "s") || strings.Contains(strings.ToUpper(def), "NULLS NOT DISTINCT") {
				return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Deferred, unvalidated and nonstandard NULL constraints need an advanced profile.")
			}
			cols, e := pgColumns(c[2])
			if e != nil {
				return out, e
			}
			refs, e := pgColumns(c[4])
			if e != nil {
				return out, e
			}
			cname := sVal(c[0])
			switch ctype {
			case "p":
				pk = cols
				primary = cname
			case "n":
				for _, col := range columns {
					if contains(cols, col["name"].(string)) {
						col["notNullName"] = cname
					}
				}
			case "u":
				unique = append(unique, map[string]any{"name": cname, "columns": cols})
			case "f":
				up, ok := actions[sVal(c[5])]
				del, ok2 := actions[sVal(c[6])]
				if !ok || !ok2 {
					return out, Unavailable("Unknown PostgreSQL FK action.")
				}
				foreign = append(foreign, map[string]any{"name": cname, "columns": cols, "referenceTable": sVal(c[3]), "referenceColumns": refs, "onUpdate": up, "onDelete": del})
			case "c":
				checks = append(checks, map[string]any{"name": cname, "expression": Expression(strings.TrimPrefix(def, "CHECK "))})
			default:
				return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Unsupported PostgreSQL constraint kind.")
			}
		}
		ix, e := s.Meta(`SELECT c.relname,i.indisunique,pg_catalog.pg_get_indexdef(i.indexrelid),pg_catalog.pg_get_expr(i.indpred,i.indrelid) FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid WHERE i.indrelid=` + id + ` AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint k WHERE k.conindid=i.indexrelid AND k.contype IN ('p','u')) ORDER BY c.relname`)
		if e != nil {
			return out, e
		}
		indexes := []map[string]any{}
		for _, r := range ix {
			if len(r) != 4 {
				return out, Unavailable("Invalid PostgreSQL index metadata.")
			}
			tokens := SQLTokens(sVal(r[2]))
			at := indexOf(tokens, "using")
			if at < 0 {
				at = indexOf(tokens, "(")
			}
			if at < 0 {
				return out, Fail("SQL_SCHEMA_INSPECTION", "Cannot inspect index definition.")
			}
			indexes = append(indexes, map[string]any{"name": sVal(r[0]), "unique": bVal(r[1]), "definition": strings.Join(tokens[at:], " "), "partial": !r[3].Null})
		}
		tableKind := "table"
		if kind == "v" {
			tableKind = "view"
		}
		t := map[string]any{"name": name, "kind": tableKind, "columns": columns, "primaryKey": pk, "foreignKeys": foreign, "unique": unique, "indexes": indexes, "checks": checks, "primaryKeyName": optionalName(primary)}
		if kind == "v" {
			v, e := s.Meta("SELECT pg_catalog.pg_get_viewdef(" + id + ",true)")
			if e != nil || len(v) != 1 || len(v[0]) != 1 {
				if e == nil {
					e = Unavailable("Missing PostgreSQL view definition.")
				}
				return out, e
			}
			t["viewDefinition"] = Expression(sVal(v[0][0]))
		}
		out.Tables = append(out.Tables, t)
	}
	return out, nil
}
func inspectMySQL(s MetadataSession) (Schema, error) {
	out := Schema{Tables: []map[string]any{}}
	rows, e := s.Meta("SELECT TABLE_NAME,TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() ORDER BY TABLE_NAME LIMIT 65")
	if e != nil {
		return out, e
	}
	if len(rows) > 64 {
		return out, OutputLimit()
	}
	for _, r := range rows {
		if len(r) != 2 {
			return out, Unavailable("Invalid MySQL metadata.")
		}
		name, kind := sVal(r[0]), sVal(r[1])
		where := " TABLE_SCHEMA=DATABASE() AND TABLE_NAME=" + textLiteral(name)
		colRows, e := s.Meta("SELECT COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT,EXTRA,COLLATION_NAME,GENERATION_EXPRESSION FROM information_schema.COLUMNS WHERE" + where + " ORDER BY ORDINAL_POSITION")
		if e != nil {
			return out, e
		}
		if len(colRows) > 256 {
			return out, OutputLimit()
		}
		columns := []map[string]any{}
		for _, c := range colRows {
			if len(c) != 7 {
				return out, Unavailable("Invalid MySQL column metadata.")
			}
			if sVal(c[6]) != "" {
				return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Generated columns need an advanced schema profile.")
			}
			item := TypeInfo(sVal(c[1]))
			extra := strings.ToLower(sVal(c[4]))
			item["name"] = sVal(c[0])
			item["nullable"] = sVal(c[2]) == "YES"
			item["default"] = nullableText(c[3])
			if strings.Contains(extra, "default_generated") {
				item["default"] = nullableExpr(c[3])
			}
			item["identity"] = strings.Contains(extra, "auto_increment")
			item["extra"] = strings.TrimSpace(strings.ReplaceAll(extra, "default_generated", ""))
			item["collation"] = nullableText(c[5])
			columns = append(columns, item)
		}
		cons, e := s.Meta(`SELECT tc.CONSTRAINT_NAME,tc.CONSTRAINT_TYPE,kcu.COLUMN_NAME,kcu.REFERENCED_TABLE_NAME,kcu.REFERENCED_COLUMN_NAME,rc.DELETE_RULE,rc.UPDATE_RULE FROM information_schema.TABLE_CONSTRAINTS tc LEFT JOIN information_schema.KEY_COLUMN_USAGE kcu ON kcu.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND kcu.TABLE_NAME=tc.TABLE_NAME AND kcu.CONSTRAINT_NAME=tc.CONSTRAINT_NAME LEFT JOIN information_schema.REFERENTIAL_CONSTRAINTS rc ON rc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND rc.TABLE_NAME=tc.TABLE_NAME AND rc.CONSTRAINT_NAME=tc.CONSTRAINT_NAME WHERE tc.TABLE_SCHEMA=DATABASE() AND tc.TABLE_NAME=` + textLiteral(name) + ` ORDER BY tc.CONSTRAINT_NAME,kcu.ORDINAL_POSITION`)
		if e != nil {
			return out, e
		}
		groups := map[string][][]native.Value{}
		keys := []string{}
		for _, c := range cons {
			if len(c) != 7 {
				return out, Unavailable("Invalid MySQL constraint metadata.")
			}
			key := sVal(c[0]) + "\x00" + sVal(c[1])
			if _, ok := groups[key]; !ok {
				keys = append(keys, key)
			}
			groups[key] = append(groups[key], c)
		}
		pk := []string{}
		primary := ""
		unique, foreign, checks := []map[string]any{}, []map[string]any{}, []map[string]any{}
		for _, key := range keys {
			g := groups[key]
			cname, ctype := sVal(g[0][0]), sVal(g[0][1])
			cols, refs := []any{}, []any{}
			for _, c := range g {
				cols = append(cols, nullableText(c[2]))
				refs = append(refs, nullableText(c[4]))
			}
			switch ctype {
			case "PRIMARY KEY":
				for _, c := range g {
					pk = append(pk, sVal(c[2]))
				}
				primary = cname
			case "UNIQUE":
				unique = append(unique, map[string]any{"name": cname, "columns": cols})
			case "FOREIGN KEY":
				foreign = append(foreign, map[string]any{"name": cname, "columns": cols, "referenceTable": sVal(g[0][3]), "referenceColumns": refs, "onDelete": strings.ReplaceAll(strings.ToLower(sVal(g[0][5])), " ", "_"), "onUpdate": strings.ReplaceAll(strings.ToLower(sVal(g[0][6])), " ", "_")})
			case "CHECK":
				check, e := s.Meta("SELECT cc.CHECK_CLAUSE,tc.ENFORCED FROM information_schema.CHECK_CONSTRAINTS cc JOIN information_schema.TABLE_CONSTRAINTS tc ON tc.CONSTRAINT_SCHEMA=cc.CONSTRAINT_SCHEMA AND tc.CONSTRAINT_NAME=cc.CONSTRAINT_NAME WHERE cc.CONSTRAINT_SCHEMA=DATABASE() AND cc.CONSTRAINT_NAME=" + textLiteral(cname))
				if e != nil {
					return out, e
				}
				if len(check) != 1 || len(check[0]) != 2 {
					return out, Unavailable("Missing MySQL check definition.")
				}
				checks = append(checks, map[string]any{"name": cname, "expression": Expression(sVal(check[0][0])), "enforced": sVal(check[0][1]) == "YES"})
			default:
				return out, Fail("SQL_SCHEMA_UNSUPPORTED", "Unsupported MySQL constraint kind.")
			}
		}
		ix, e := s.Meta("SELECT INDEX_NAME,NON_UNIQUE,COLUMN_NAME,COLLATION,SUB_PART,EXPRESSION,IS_VISIBLE,INDEX_TYPE FROM information_schema.STATISTICS WHERE" + where + " AND INDEX_NAME<>'PRIMARY' ORDER BY INDEX_NAME,SEQ_IN_INDEX")
		if e != nil {
			return out, e
		}
		indexes := []map[string]any{}
		im := map[string]int{}
		for _, r := range ix {
			if len(r) != 8 {
				return out, Unavailable("Invalid MySQL index metadata.")
			}
			idxName := sVal(r[0])
			at, ok := im[idxName]
			if !ok {
				at = len(indexes)
				im[idxName] = at
				indexes = append(indexes, map[string]any{"name": idxName, "unique": !bVal(r[1]), "parts": []map[string]any{}})
			}
			var prefix any
			if !r[4].Null {
				prefix = iVal(r[4])
			}
			part := map[string]any{"column": nullableText(r[2]), "order": nullableText(r[3]), "prefix": prefix, "expression": nullableExpr(r[5]), "visible": sVal(r[6]) == "YES", "indexType": sVal(r[7])}
			indexes[at]["parts"] = append(indexes[at]["parts"].([]map[string]any), part)
		}
		tableKind := "table"
		if kind == "VIEW" {
			tableKind = "view"
		}
		t := map[string]any{"name": name, "kind": tableKind, "columns": columns, "primaryKey": pk, "foreignKeys": foreign, "unique": unique, "indexes": indexes, "checks": checks, "primaryKeyName": optionalName(primary)}
		if kind == "VIEW" {
			v, e := s.Meta("SELECT VIEW_DEFINITION,CHECK_OPTION,IS_UPDATABLE,SECURITY_TYPE FROM information_schema.VIEWS WHERE" + where)
			if e != nil {
				return out, e
			}
			if len(v) != 1 || len(v[0]) != 4 {
				return out, Unavailable("Missing MySQL view definition.")
			}
			t["viewDefinition"] = Expression(sVal(v[0][0]))
			t["options"] = map[string]any{"checkOption": sVal(v[0][1]), "updatable": sVal(v[0][2]), "securityType": sVal(v[0][3])}
		}
		out.Tables = append(out.Tables, t)
	}
	return out, nil
}

// MetadataJSON is used by cross-language golden fixtures, not by learner input.
func MetadataJSON(v any) string {
	raw, e := json.Marshal(v)
	if e != nil {
		return fmt.Sprintf("<invalid:%T>", v)
	}
	return string(raw)
}
