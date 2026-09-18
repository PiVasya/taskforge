package sqlworker

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sort"
	"strings"
	"taskforge/sqlworker/internal/native"
	"time"
	"unicode/utf8"
)

// ChildRequest intentionally has no business identity, reference SQL, expected
// artifact, internal HTTP key, message-broker credentials or engine admin account.
type ChildRequest struct {
	EngineVersion           string        `json:"engineVersion"`
	Engine                  string        `json:"engine"`
	Connection              native.Config `json:"connection"`
	Source                  string        `json:"source"`
	Mode                    string        `json:"mode"`
	AllowMultipleStatements bool          `json:"allowMultipleStatements"`
	Limits                  Limits        `json:"limits"`
	StateCheck              StateCheck    `json:"stateCheck"`
	InitialDatabases        []string      `json:"initialDatabases,omitempty"`
}

type querySession struct {
	conn             *native.Session
	ctx              context.Context
	engine, database string
	limits           Limits
}

func (s *querySession) Meta(sql string) ([][]native.Value, error) {
	r, e := s.conn.Query(s.ctx, sql, 4096, max(1<<20, s.limits.MaxBytes))
	return r.Rows, e
}
func (s *querySession) tableQuery(name string, limit int) string {
	table := q(name, s.engine)
	switch s.engine {
	case "postgresql":
		table = "\"public\"." + table
	case "mysql":
		table = q(s.database, "mysql") + "." + table
	case "sqlite":
		table = "\"main\"." + table
	}
	return fmt.Sprintf("SELECT * FROM %s LIMIT %d", table, limit)
}
func nativeCell(v native.Value) (Cell, error) {
	if v.Null {
		return Cell{"null", nil}, nil
	}
	switch v.Kind {
	case "number":
		text, e := NumberText(v.Text)
		if e != nil {
			return Cell{}, Fail("SQL_UNSUPPORTED_VALUE", "Non-finite or unsupported numeric value.")
		}
		return Cell{"number", text}, nil
	case "boolean":
		return Cell{"boolean", v.Text == "t" || v.Text == "true" || v.Text == "1"}, nil
	case "binary":
		return Cell{"binary", base64.StdEncoding.EncodeToString(v.Bytes)}, nil
	case "date":
		if _, e := time.Parse("2006-01-02", v.Text); e != nil {
			return Cell{}, Fail("SQL_UNSUPPORTED_VALUE", "Unsupported date value.")
		}
		return Cell{"date", v.Text}, nil
	case "datetime":
		layouts := []string{"2006-01-02 15:04:05.999999999Z07:00", "2006-01-02 15:04:05.999999999Z07", "2006-01-02 15:04:05.999999999", time.RFC3339Nano}
		for _, layout := range layouts {
			if value, e := time.Parse(layout, v.Text); e == nil {
				return Cell{"datetime", value.UTC().Format("2006-01-02T15:04:05.000000Z")}, nil
			}
		}
		return Cell{}, Fail("SQL_UNSUPPORTED_VALUE", "Unsupported datetime value.")
	case "string":
		if !utf8.ValidString(v.Text) {
			return Cell{}, Fail("SQL_UNSUPPORTED_VALUE", "Database returned invalid UTF-8.")
		}
		return Cell{"string", v.Text}, nil
	default:
		return Cell{}, Fail("SQL_UNSUPPORTED_VALUE", "The query returned a type outside the supported SQL value contract.")
	}
}
func convertResult(r native.Result) (ResultSet, error) {
	out := ResultSet{Columns: r.Columns, Rows: [][]Cell{}}
	if len(r.Columns) > 256 {
		return out, OutputLimit()
	}
	for _, row := range r.Rows {
		cells := make([]Cell, len(row))
		for i, v := range row {
			c, e := nativeCell(v)
			if e != nil {
				return out, e
			}
			cells[i] = c
		}
		out.Rows = append(out.Rows, cells)
	}
	return out, nil
}
func chargeJSON(remaining *int, value any) error {
	b, e := json.Marshal(value)
	if e != nil {
		return e
	}
	*remaining -= len(b)
	if *remaining < 0 {
		return OutputLimit()
	}
	return nil
}
func NormalizeFailure(err error) *Failure {
	var own *Failure
	if errors.As(err, &own) {
		return own
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return Timeout()
	}
	if errors.Is(err, context.Canceled) {
		return Unavailable("The execution was cancelled.")
	}
	var db *native.Error
	if errors.As(err, &db) {
		if db.Code == "3D000" || db.Code == "1049" || db.Code == "SQLITE_CANTOPEN" {
			return RecoverCache("The local disposable SQL cache must be rebuilt.")
		}
		if db.Timeout {
			return Timeout()
		}
		if db.Limit {
			return OutputLimit()
		}
		if strings.HasPrefix(db.Code, "08") || contains([]string{"57P01", "57P02", "57P03", "2002", "2003", "2006", "2013", "SQLITE_CANTOPEN", "SQLITE_CLOSED"}, db.Code) {
			return Unavailable("The dedicated SQL engine is unavailable.")
		}
		return Fail(db.Code, strings.Split(db.Message, "\nCONTEXT:")[0])
	}
	return Unavailable("The SQL execution component failed. Consult the job's bounded diagnostic code.")
}
func snapshotFailure(s *Snapshot, err error) {
	f := NormalizeFailure(err)
	s.Error = &f.PublicError
	s.ErrorVerdict = f.Verdict
}

func ExecuteChild(req ChildRequest) (out Snapshot) {
	out = EmptySnapshot()
	defer func() {
		if recover() != nil {
			snapshotFailure(&out, Unavailable("Invalid or unsupported SQL execution data."))
		}
	}()
	if e := req.Limits.Validate(); e != nil {
		snapshotFailure(&out, e)
		return
	}
	if !contains([]string{"result", "state", "schema"}, req.Mode) || req.Engine != req.Connection.Engine {
		snapshotFailure(&out, Unavailable("Invalid child execution contract."))
		return
	}
	statements, e := SplitScript(req.Source, req.Engine, req.AllowMultipleStatements, req.Limits.MaxStatements)
	if e != nil {
		snapshotFailure(&out, e)
		return
	}

	// Infrastructure setup has its own bounded budget. Learner execution time starts
	// only after the disposable connection, runtime identity and mandatory isolation
	// are ready; otherwise a 100 ms learner limit can be mostly consumed by setup.
	setupCtx, setupCancel := context.WithTimeout(context.Background(), time.Second)
	defer setupCancel()
	cfg := req.Connection
	cfg.Create = false
	cfg.TimeoutMS = req.Limits.TimeoutMS
	conn, e := native.Open(cfg)
	if e != nil {
		snapshotFailure(&out, e)
		return
	}
	defer conn.Close()
	s := &querySession{conn, setupCtx, req.Engine, cfg.Database, req.Limits}
	setupFailure := func(err error) error {
		if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, context.Canceled) {
			return Unavailable("The disposable SQL sandbox setup exceeded its bounded internal deadline.")
		}
		return err
	}
	if req.EngineVersion == "" {
		snapshotFailure(&out, Unavailable("Missing pinned engine version."))
		return
	}
	actual := native.LibraryVersions()["sqlite"]
	if req.Engine == "postgresql" {
		actual, e = readScalar(setupCtx, conn, "SHOW server_version")
	} else if req.Engine == "mysql" {
		actual, e = readScalar(setupCtx, conn, "SELECT VERSION()")
	}
	if e != nil {
		snapshotFailure(&out, setupFailure(e))
		return
	}
	if actual != req.EngineVersion {
		snapshotFailure(&out, RecoverCache("The connected engine no longer matches the pinned runtime profile."))
		return
	}

	if req.Engine == "sqlite" {
		for _, sql := range []string{"PRAGMA trusted_schema=OFF", "PRAGMA foreign_keys=ON", "PRAGMA journal_mode=MEMORY", "PRAGMA temp_store=MEMORY", "PRAGMA synchronous=OFF", "PRAGMA cell_size_check=ON", "PRAGMA max_page_count=16384"} {
			if _, e = s.Meta(sql); e != nil {
				snapshotFailure(&out, setupFailure(e))
				return
			}
		}
		rows, err := s.Meta("PRAGMA quick_check")
		if err != nil || len(rows) != 1 || rows[0][0].Text != "ok" {
			if err != nil {
				snapshotFailure(&out, setupFailure(err))
			} else {
				snapshotFailure(&out, Unavailable("The disposable SQLite database failed integrity validation."))
			}
			return
		}
	} else if req.Engine == "mysql" {
		for _, sql := range []string{fmt.Sprintf("SET SESSION max_execution_time=%d", req.Limits.TimeoutMS), "SET SESSION tmp_table_size=16777216", "SET SESSION max_heap_table_size=16777216", "SET SESSION cte_max_recursion_depth=1000"} {
			if _, e = s.Meta(sql); e != nil {
				snapshotFailure(&out, setupFailure(e))
				return
			}
		}
	}
	// There is deliberately no environment switch that bypasses this boundary.
	if e = InstallIsolation(req.Limits.TimeoutMS); e != nil {
		snapshotFailure(&out, Unavailable("The mandatory all-thread query isolation policy could not be installed."))
		return
	}
	setupCancel()

	// This is the learner-visible SQL execution budget. Server-side PostgreSQL and
	// MySQL statement limits remain pinned to the same contract, while this context
	// bounds the whole script. SQLite installs its progress deadline at this point,
	// not during sandbox setup.
	executionCtx, executionCancel := context.WithTimeout(context.Background(), time.Duration(req.Limits.TimeoutMS)*time.Millisecond)
	s.ctx = executionCtx
	if req.Engine == "sqlite" {
		if e = conn.ConfigureSQLite(req.Limits.MaxBytes, req.Limits.TimeoutMS); e != nil {
			executionCancel()
			snapshotFailure(&out, e)
			return
		}
		if e = conn.AuthorizeSQLite(true); e != nil {
			executionCancel()
			snapshotFailure(&out, e)
			return
		}
	}
	started := time.Now()
	remaining := req.Limits.MaxBytes
	databases := map[string]bool{}
	for _, name := range req.InitialDatabases {
		databases[name] = true
	}
	for index, stmt := range statements {
		if operation, handled, lifecycleErr := parseDatabaseStatement(stmt, req.Engine); handled {
			if lifecycleErr != nil {
				snapshotFailure(&out, lifecycleErr)
				break
			}
			if lifecycleErr = applyDatabaseStatement(databases, operation); lifecycleErr != nil {
				snapshotFailure(&out, lifecycleErr)
				break
			}
			out.DatabaseOperations = append(out.DatabaseOperations, operation.DatabaseOperation)
			out.StatementsExecuted = index + 1
			continue
		}
		r, err := conn.Query(executionCtx, stmt.SQL, req.Limits.MaxRows, max(1, remaining))
		if err != nil {
			snapshotFailure(&out, err)
			break
		}
		out.StatementsExecuted = index + 1
		out.AffectedRows += r.Affected
		if r.HasRows {
			result, err := convertResult(r)
			if err != nil {
				snapshotFailure(&out, err)
				break
			}
			result.Statement = index + 1
			if err = chargeJSON(&remaining, result); err != nil {
				snapshotFailure(&out, err)
				break
			}
			out.Results = append(out.Results, result)
		}
	}
	out.ExecutionMS = time.Since(started).Milliseconds()
	if len(databases) > 0 || len(req.InitialDatabases) > 0 || len(out.DatabaseOperations) > 0 {
		out.Databases = make([]string, 0, len(databases))
		for name := range databases {
			out.Databases = append(out.Databases, name)
		}
		sort.Strings(out.Databases)
	}
	executionCancel()
	if req.Engine == "sqlite" {
		if e = conn.AuthorizeSQLite(false); e != nil {
			snapshotFailure(&out, e)
			return
		}
	}

	// Result/schema inspection is generated by TaskForge, not learner SQL. Give it
	// a separate small infrastructure budget so it neither steals learner execution
	// time nor turns a completed query into a fake learner timeout.
	inspectionCtx, inspectionCancel := context.WithTimeout(context.Background(), 500*time.Millisecond)
	defer inspectionCancel()
	s.ctx = inspectionCtx
	if req.Engine == "sqlite" {
		if e = conn.ConfigureSQLite(req.Limits.MaxBytes, 500); e != nil {
			snapshotFailure(&out, e)
			return
		}
	}
	inspectionStart := time.Now()
	e = inspectSnapshot(s, req, &out)
	out.InspectionMS = time.Since(inspectionStart).Milliseconds()
	if e != nil {
		if errors.Is(e, context.DeadlineExceeded) || errors.Is(e, context.Canceled) {
			e = Unavailable("The SQL result inspection exceeded its bounded internal deadline.")
		}
		f := NormalizeFailure(e)
		out.PreviewError = &f.PublicError
		if out.Error == nil && (req.Mode == "state" || req.Mode == "schema") {
			snapshotFailure(&out, e)
		}
	}
	return
}

func inspectSnapshot(s *querySession, req ChildRequest, out *Snapshot) error {
	schema, e := InspectSchema(s, req.Engine)
	if e != nil {
		return e
	}
	budget := req.Limits.MaxBytes
	if e = chargeJSON(&budget, schema); e != nil {
		return e
	}
	out.Schema = schema
	previewBudget, stateBudget := req.Limits.MaxBytes, req.Limits.MaxBytes
	for _, table := range schema.Tables {
		if table["kind"] != "table" {
			continue
		}
		name, ok := table["name"].(string)
		if !ok {
			return Unavailable("Invalid schema metadata.")
		}
		verify := out.Error == nil && req.Mode == "state" && (len(req.StateCheck.Tables) == 0 || contains(req.StateCheck.Tables, name))
		limit := req.Limits.PreviewRows + 1
		if verify {
			limit = req.Limits.MaxRows + 1
		}
		r, err := s.conn.Query(s.ctx, s.tableQuery(name, limit), limit, req.Limits.MaxBytes)
		if err != nil {
			return err
		}
		data, err := convertResult(r)
		if err != nil {
			return err
		}
		if verify {
			if len(data.Rows) > req.Limits.MaxRows {
				return OutputLimit()
			}
			if err = chargeJSON(&stateBudget, data); err != nil {
				return err
			}
			out.VerificationData[name] = data
		}
		if len(data.Rows) > req.Limits.PreviewRows {
			data.Truncated = true
			data.Rows = data.Rows[:req.Limits.PreviewRows]
		}
		if err = chargeJSON(&previewBudget, data); err != nil {
			return err
		}
		out.Data[name] = data
	}
	return nil
}
func ChildMain(in io.Reader, out io.Writer) error {
	raw, e := io.ReadAll(io.LimitReader(in, 5_000_001))
	if e != nil {
		return e
	}
	if len(raw) > 5_000_000 {
		return fmt.Errorf("child request too large")
	}
	var req ChildRequest
	if e = DecodeStrict(raw, &req); e != nil {
		return fmt.Errorf("invalid child request")
	}
	snapshot := ExecuteChild(req)
	encoded, e := json.Marshal(snapshot)
	if e != nil {
		return e
	}
	if len(encoded) > 7_000_000 {
		snapshot = EmptySnapshot()
		snapshotFailure(&snapshot, OutputLimit())
		encoded, _ = json.Marshal(snapshot)
	}
	_, e = out.Write(encoded)
	return e
}
