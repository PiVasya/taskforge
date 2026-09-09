package sqlworker

import (
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math/big"
	"regexp"
	"strconv"
	"strings"
	"time"
)

var identRE = regexp.MustCompile(`^[a-z][a-z0-9_]{0,47}$`)
var typeRE = regexp.MustCompile(`^[a-z][a-z0-9 ]{0,31}(\([0-9]{1,4}(,[0-9]{1,2})?\))?$`)

func Quote(name, engine string) (string, error) {
	if strings.ContainsRune(name, 0) || len([]rune(name)) > 256 {
		return "", Fail("SQL_SCHEMA_IDENTIFIER", "Invalid schema identifier.")
	}
	q := "\""
	if engine == "mysql" {
		q = "`"
	}
	return q + strings.ReplaceAll(name, q, q+q) + q, nil
}
func q(name, engine string) string {
	v, e := Quote(name, engine)
	if e != nil {
		panic(e)
	}
	return v
}
func portable(name string) error {
	if !identRE.MatchString(name) || strings.HasPrefix(name, "tfq_") || strings.HasPrefix(name, "sqlite_") {
		return Fail("SQL_DATASET_IDENTIFIER", "Invalid portable dataset identifier.")
	}
	return nil
}
func textLiteral(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }
func NativeType(c Column, engine string, m ColumnMapping) (string, error) {
	if m.Type != nil {
		v := strings.ToLower(strings.TrimSpace(*m.Type))
		if !typeRE.MatchString(v) {
			return "", Fail("SQL_TYPE_MAPPING", "Invalid native type mapping.")
		}
		return strings.ToUpper(v), nil
	}
	switch c.Type {
	case "integer":
		return "INTEGER", nil
	case "bigint":
		return "BIGINT", nil
	case "decimal":
		if c.Precision == nil || c.Scale == nil || *c.Precision < 1 || *c.Precision > 28 || *c.Scale < 0 || *c.Scale > *c.Precision {
			return "", Fail("SQL_DECIMAL", "Invalid decimal precision or scale.")
		}
		return fmt.Sprintf("DECIMAL(%d,%d)", *c.Precision, *c.Scale), nil
	case "string":
		if c.Length == nil || *c.Length < 1 || *c.Length > 4000 {
			return "", Fail("SQL_STRING_LENGTH", "Invalid string length.")
		}
		return fmt.Sprintf("VARCHAR(%d)", *c.Length), nil
	case "text":
		return "TEXT", nil
	case "boolean":
		return "BOOLEAN", nil
	case "date":
		return "DATE", nil
	case "datetime":
		switch engine {
		case "postgresql":
			return "TIMESTAMP(6) WITHOUT TIME ZONE", nil
		case "mysql":
			return "DATETIME(6)", nil
		default:
			return "DATETIME", nil
		}
	case "uuid":
		if engine == "mysql" {
			return "CHAR(36)", nil
		}
		return "UUID", nil
	case "binary":
		if engine == "postgresql" {
			return "BYTEA", nil
		}
		return "BLOB", nil
	}
	return "", Fail("SQL_LOGICAL_TYPE", "Unsupported logical column type.")
}
func ScalarLiteral(value any, c Column, engine string) (string, error) {
	invalid := func() (string, error) {
		return "", Fail("SQL_SEED_VALUE", "Invalid "+c.Type+" seed/default value in column "+c.Name+".")
	}
	if value == nil {
		if !c.Nullable {
			return "", Fail("SQL_SEED_NULL", "NULL is not allowed in column "+c.Name+".")
		}
		return "NULL", nil
	}
	switch c.Type {
	case "integer", "bigint":
		var s string
		switch v := value.(type) {
		case json.Number:
			s = string(v)
		case string:
			s = v
		case int:
			s = strconv.Itoa(v)
		case int64:
			s = strconv.FormatInt(v, 10)
		default:
			return invalid()
		}
		if !regexp.MustCompile(`^-?[0-9]+$`).MatchString(s) {
			return invalid()
		}
		bits := 64
		if c.Type == "integer" {
			bits = 32
		}
		v, e := strconv.ParseInt(s, 10, bits)
		if e != nil {
			return invalid()
		}
		return strconv.FormatInt(v, 10), nil
	case "decimal":
		var s string
		switch v := value.(type) {
		case json.Number:
			s = string(v)
		case string:
			s = v
		default:
			return invalid()
		}
		normalized, e := NumberText(s)
		if e != nil || c.Scale == nil || c.Precision == nil {
			return invalid()
		}
		parts := strings.Split(strings.TrimPrefix(normalized, "-"), ".")
		if len(parts) == 2 && len(parts[1]) > *c.Scale {
			return invalid()
		}
		if len(strings.TrimLeft(parts[0], "0")) > *c.Precision-*c.Scale {
			return invalid()
		}
		return normalized, nil
	case "boolean":
		v, ok := value.(bool)
		if !ok {
			return invalid()
		}
		if v {
			return "TRUE", nil
		}
		return "FALSE", nil
	case "string", "text":
		v, ok := value.(string)
		if !ok || strings.ContainsRune(v, 0) {
			return invalid()
		}
		limit := 65536
		if c.Type == "string" {
			if c.Length == nil {
				return invalid()
			}
			limit = *c.Length
		}
		if len([]rune(v)) > limit {
			return invalid()
		}
		return textLiteral(v), nil
	case "date":
		v, ok := value.(string)
		if !ok {
			return invalid()
		}
		parsed, e := time.Parse("2006-01-02", v)
		if e != nil {
			return invalid()
		}
		return textLiteral(parsed.Format("2006-01-02")), nil
	case "datetime":
		v, ok := value.(string)
		if !ok {
			return invalid()
		}
		var parsed time.Time
		var e error
		for _, layout := range []string{time.RFC3339Nano, "2006-01-02T15:04:05.999999999", "2006-01-02 15:04:05.999999999"} {
			parsed, e = time.Parse(layout, v)
			if e == nil {
				break
			}
		}
		if e != nil {
			return invalid()
		}
		return textLiteral(parsed.UTC().Format("2006-01-02 15:04:05.000000")), nil
	case "uuid":
		v, ok := value.(string)
		if !ok || !uuidRE.MatchString(v) {
			return invalid()
		}
		return textLiteral(strings.ToLower(v)), nil
	case "binary":
		v, ok := value.(string)
		if !ok {
			return invalid()
		}
		data, e := base64.StdEncoding.Strict().DecodeString(v)
		if e != nil || len(data) > 65536 {
			return invalid()
		}
		h := hex.EncodeToString(data)
		if engine == "postgresql" {
			return "decode('" + h + "','hex')", nil
		}
		return "X'" + h + "'", nil
	}
	return invalid()
}
func defaultSQL(d *Default, c Column, engine string) (string, error) {
	if d == nil {
		return "", nil
	}
	switch d.Kind {
	case "literal":
		c.Nullable = true
		v, e := ScalarLiteral(d.Value, c, engine)
		if engine == "mysql" && (c.Type == "text" || c.Type == "binary") {
			return " DEFAULT (" + v + ")", e
		}
		return " DEFAULT " + v, e
	case "current_timestamp":
		if c.Type == "datetime" {
			if engine == "sqlite" {
				return " DEFAULT CURRENT_TIMESTAMP", nil
			}
			return " DEFAULT CURRENT_TIMESTAMP(6)", nil
		}
	case "current_date":
		if c.Type == "date" {
			if engine == "mysql" {
				return " DEFAULT (CURRENT_DATE)", nil
			}
			return " DEFAULT CURRENT_DATE", nil
		}
	}
	return "", Fail("SQL_DEFAULT", "Invalid semantic default for the logical column type.")
}

type DatasetPlan struct{ Creates, Inserts, After []string }

func mysqlUnboundedKeyType(nativeType string) bool {
	base := strings.ToUpper(strings.TrimSpace(nativeType))
	if at := strings.IndexByte(base, '('); at >= 0 {
		base = strings.TrimSpace(base[:at])
	}
	switch base {
	case "TEXT", "TINYTEXT", "MEDIUMTEXT", "LONGTEXT", "BLOB", "TINYBLOB", "MEDIUMBLOB", "LONGBLOB":
		return true
	default:
		return false
	}
}

func CompileDataset(p Payload, engine string) (DatasetPlan, error) {
	plan := DatasetPlan{[]string{}, []string{}, []string{}}
	bad := func(code, msg string) (DatasetPlan, error) { return plan, Fail(code, msg) }
	if engine != "sqlite" && engine != "postgresql" && engine != "mysql" {
		return bad("SQL_ENGINE", "Unsupported engine.")
	}
	tables := p.Definition.Tables
	if tables == nil || len(tables) > 24 {
		return bad("SQL_DATASET_SIZE", "Invalid dataset table count.")
	}
	names := map[string]Table{}
	for _, t := range tables {
		if e := portable(t.Name); e != nil {
			return plan, e
		}
		if _, ok := names[t.Name]; ok {
			return bad("SQL_DATASET_DUPLICATE", "Duplicate table name.")
		}
		names[t.Name] = t
	}
	for name := range p.Seed {
		if _, ok := names[name]; !ok {
			return bad("SQL_SEED_TABLE", "Unknown seed table.")
		}
	}
	// MySQL cannot preserve full PK/UNIQUE/index/FK semantics on unbounded
	// TEXT/BLOB columns without prefix indexes. Prefix indexes would change the
	// logical TaskForge contract, so reject that engine/materialization before
	// sending generated DDL to the server. An engine override to VARCHAR/VARBINARY
	// (or a portable string(n)) remains available to the author.
	mysqlKeyColumns := map[string]map[string]bool{}
	if engine == "mysql" {
		mark := func(table, column string) {
			if mysqlKeyColumns[table] == nil {
				mysqlKeyColumns[table] = map[string]bool{}
			}
			mysqlKeyColumns[table][column] = true
		}
		for _, t := range tables {
			for _, c := range t.PrimaryKey {
				mark(t.Name, c)
			}
			for _, key := range t.Unique {
				for _, c := range key {
					mark(t.Name, c)
				}
			}
			for _, idx := range t.Indexes {
				for _, c := range idx.Columns {
					mark(t.Name, c)
				}
			}
			for _, fk := range t.ForeignKeys {
				for _, c := range fk.Columns {
					mark(t.Name, c)
				}
				for _, c := range fk.ReferenceColumns {
					mark(fk.ReferenceTable, c)
				}
			}
		}
	}
	total := 0
	for _, t := range tables {
		if len(t.Columns) < 1 || len(t.Columns) > 64 {
			return bad("SQL_DATASET_COLUMNS", "Invalid column count.")
		}
		cols := map[string]Column{}
		identity := ""
		for _, c := range t.Columns {
			if e := portable(c.Name); e != nil {
				return plan, e
			}
			if _, ok := cols[c.Name]; ok {
				return bad("SQL_DATASET_COLUMNS", "Duplicate column.")
			}
			cols[c.Name] = c
			if c.Identity {
				if identity != "" || c.Type != "integer" || c.Nullable || len(t.PrimaryKey) != 1 || t.PrimaryKey[0] != c.Name {
					return bad("SQL_IDENTITY", "Identity requires one non-null integer primary key.")
				}
				identity = c.Name
			}
		}
		rows := make([]map[string]any, len(p.Seed[t.Name]))
		if len(rows) > 1000 {
			return bad("SQL_SEED_SIZE", "Too many seed rows.")
		}
		total += len(rows)
		if total > 5000 {
			return bad("SQL_SEED_SIZE", "Total seed limit exceeded.")
		}
		maxID := int64(0)
		for i, r := range p.Seed[t.Name] {
			if r == nil {
				return bad("SQL_SEED_VALUE", "A seed row cannot be null.")
			}
			rows[i] = map[string]any{}
			for k, v := range r {
				if _, ok := cols[k]; !ok {
					return bad("SQL_SEED_COLUMN", "Unknown seed column.")
				}
				rows[i][k] = v
			}
			if identity != "" {
				if v, ok := r[identity]; ok {
					s, e := ScalarLiteral(v, cols[identity], engine)
					if e != nil {
						return plan, e
					}
					id, _ := strconv.ParseInt(s, 10, 64)
					if id > maxID {
						maxID = id
					}
				}
			}
		}
		if identity != "" {
			for _, r := range rows {
				if _, ok := r[identity]; !ok {
					maxID++
					r[identity] = json.Number(strconv.FormatInt(maxID, 10))
				}
			}
		}
		colList := func(values []string) (string, error) {
			if len(values) == 0 {
				return "", Fail("SQL_DATASET_KEY", "Empty constraint key.")
			}
			qs := []string{}
			seen := map[string]bool{}
			for _, v := range values {
				if _, ok := cols[v]; !ok || seen[v] {
					return "", Fail("SQL_DATASET_KEY", "Invalid constraint columns.")
				}
				seen[v] = true
				qs = append(qs, q(v, engine))
			}
			return strings.Join(qs, ","), nil
		}
		defs := []string{}
		mapping := p.EngineOverrides[engine].Columns
		for _, c := range t.Columns {
			override := mapping[t.Name+"."+c.Name]
			nt, e := NativeType(c, engine, override)
			if e != nil {
				return plan, e
			}
			if engine == "mysql" && mysqlKeyColumns[t.Name][c.Name] && mysqlUnboundedKeyType(nt) {
				return bad("SQL_SCHEMA_UNSUPPORTED", "MySQL requires a bounded type for key/index column "+t.Name+"."+c.Name+"; use string(n) or a MySQL type override.")
			}
			s := q(c.Name, engine) + " " + nt
			if c.Identity {
				switch engine {
				case "sqlite":
					if nt != "INTEGER" {
						return bad("SQL_IDENTITY_MAPPING", "SQLite identity requires INTEGER.")
					}
					s += " PRIMARY KEY AUTOINCREMENT"
				case "postgresql":
					s += " GENERATED BY DEFAULT AS IDENTITY"
				case "mysql":
					s += " AUTO_INCREMENT"
				}
			}
			if !c.Nullable {
				s += " NOT NULL"
			}
			d := c.Default
			if override.Default != nil {
				d = override.Default
			}
			if !c.Identity {
				x, e := defaultSQL(d, c, engine)
				if e != nil {
					return plan, e
				}
				s += x
			}
			defs = append(defs, s)
		}
		if len(t.PrimaryKey) > 0 && !(engine == "sqlite" && identity != "") {
			c, e := colList(t.PrimaryKey)
			if e != nil {
				return plan, e
			}
			defs = append(defs, "PRIMARY KEY ("+c+")")
		}
		for n, u := range t.Unique {
			c, e := colList(u)
			if e != nil {
				return plan, e
			}
			name := fmt.Sprintf("tfq_uq_%d_%s", n, t.Name)
			if len(name) > 48 {
				name = name[:48]
			}
			defs = append(defs, "CONSTRAINT "+q(name, engine)+" UNIQUE ("+c+")")
		}
		actions := map[string]string{"": "NO ACTION", "no_action": "NO ACTION", "restrict": "RESTRICT", "cascade": "CASCADE", "set_null": "SET NULL"}
		for _, fk := range t.ForeignKeys {
			if e := portable(fk.Name); e != nil {
				return plan, e
			}
			c, e := colList(fk.Columns)
			if e != nil {
				return plan, e
			}
			target, ok := names[fk.ReferenceTable]
			if !ok || len(fk.Columns) != len(fk.ReferenceColumns) {
				return bad("SQL_FOREIGN_KEY", "Invalid referenced table or key length.")
			}
			targetCols := map[string]bool{}
			for _, tc := range target.Columns {
				targetCols[tc.Name] = true
			}
			refs := []string{}
			for _, rc := range fk.ReferenceColumns {
				if !targetCols[rc] {
					return bad("SQL_FOREIGN_KEY", "Unknown referenced column.")
				}
				refs = append(refs, q(rc, engine))
			}
			del, ok := actions[fk.OnDelete]
			if !ok {
				return bad("SQL_FOREIGN_KEY", "Unsupported delete action.")
			}
			up, ok := actions[fk.OnUpdate]
			if !ok {
				return bad("SQL_FOREIGN_KEY", "Unsupported update action.")
			}
			constraint := "CONSTRAINT " + q(fk.Name, engine) + " FOREIGN KEY (" + c + ") REFERENCES " + q(fk.ReferenceTable, engine) + " (" + strings.Join(refs, ",") + ") ON DELETE " + del + " ON UPDATE " + up
			if engine == "sqlite" {
				defs = append(defs, constraint)
			} else {
				plan.After = append(plan.After, "ALTER TABLE "+q(t.Name, engine)+" ADD "+constraint)
			}
		}
		create := "CREATE TABLE " + q(t.Name, engine) + " (" + strings.Join(defs, ",") + ")"
		if engine == "mysql" {
			create += " ENGINE=InnoDB"
		}
		plan.Creates = append(plan.Creates, create)
		for _, idx := range t.Indexes {
			if e := portable(idx.Name); e != nil {
				return plan, e
			}
			cs, e := colList(idx.Columns)
			if e != nil {
				return plan, e
			}
			uq := ""
			if idx.Unique {
				uq = "UNIQUE "
			}
			plan.After = append(plan.After, "CREATE "+uq+"INDEX "+q(idx.Name, engine)+" ON "+q(t.Name, engine)+" ("+cs+")")
		}
		for _, r := range rows {
			selected, values := []string{}, []string{}
			for _, c := range t.Columns {
				v, ok := r[c.Name]
				if ok {
					s, e := ScalarLiteral(v, c, engine)
					if e != nil {
						return plan, e
					}
					selected = append(selected, q(c.Name, engine))
					values = append(values, s)
					continue
				}
				d := c.Default
				if ov := mapping[t.Name+"."+c.Name]; ov.Default != nil {
					d = ov.Default
				}
				if d != nil && (d.Kind == "current_date" || d.Kind == "current_timestamp") {
					return bad("SQL_SEED_CLOCK", "Seed rows must explicitly fill clock-dependent defaults.")
				}
				if !c.Nullable && d == nil {
					return bad("SQL_SEED_REQUIRED", "Missing seed value for "+t.Name+"."+c.Name+".")
				}
			}
			sql := "INSERT INTO " + q(t.Name, engine)
			if len(selected) > 0 {
				sql += " (" + strings.Join(selected, ",") + ") VALUES (" + strings.Join(values, ",") + ")"
			} else if engine == "mysql" {
				sql += " () VALUES ()"
			} else {
				sql += " DEFAULT VALUES"
			}
			plan.Inserts = append(plan.Inserts, sql)
		}
	}
	return plan, nil
}
func parseDecimal(s string) (*big.Rat, error) {
	n, e := NumberText(s)
	if e != nil {
		return nil, e
	}
	r, ok := new(big.Rat).SetString(n)
	if !ok {
		return nil, fmt.Errorf("invalid exact decimal")
	}
	return r, nil
}
