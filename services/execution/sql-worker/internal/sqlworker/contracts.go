package sqlworker

import (
	"encoding/json"
	"fmt"
	"regexp"
	"time"
)

const ContractVersion = 1
const AdapterVersion = "1.0.0"
const ImplementationVersion = "go-native-v1"

// The wire names mirror services/shared/Sql/SqlContracts.cs. Unknown fields, duplicate
// properties, lossy numbers and unsupported contract versions are rejected on input.
type Limits struct {
	TimeoutMS     int `json:"timeoutMs"`
	MaxRows       int `json:"maxRows"`
	PreviewRows   int `json:"previewRows"`
	MaxBytes      int `json:"maxBytes"`
	MaxStatements int `json:"maxStatements"`
}

func (l Limits) Validate() error {
	if l.TimeoutMS < 100 || l.TimeoutMS > 10000 || l.MaxRows < 1 || l.MaxRows > 1000 || l.PreviewRows < 1 || l.PreviewRows > 200 || l.PreviewRows > l.MaxRows || l.MaxBytes < 1024 || l.MaxBytes > 2097152 || l.MaxStatements < 1 || l.MaxStatements > 50 {
		return Unavailable("Invalid SQL execution limits.")
	}
	return nil
}
func DefaultLimits() Limits { return Limits{5000, 1000, 200, 1048576, 20} }

type Comparison struct {
	OrderMatters      bool        `json:"orderMatters"`
	ColumnNamesMatter bool        `json:"columnNamesMatter"`
	DuplicatesMatter  bool        `json:"duplicatesMatter"`
	CaseSensitive     bool        `json:"caseSensitive"`
	NumericTolerance  json.Number `json:"numericTolerance"`
}
type StateCheck struct {
	Tables []string `json:"tables"`
}
type SchemaCheck struct {
	Tables                []string `json:"tables"`
	ConstraintNamesMatter bool     `json:"constraintNamesMatter"`
	DefaultsMatter        bool     `json:"defaultsMatter"`
	IndexesMatter         bool     `json:"indexesMatter"`
}
type Default struct {
	Kind  string `json:"kind"`
	Value any    `json:"value"`
}
type Column struct {
	Name      string   `json:"name"`
	Type      string   `json:"type"`
	Nullable  bool     `json:"nullable"`
	Length    *int     `json:"length"`
	Precision *int     `json:"precision"`
	Scale     *int     `json:"scale"`
	Identity  bool     `json:"identity"`
	Default   *Default `json:"default"`
}
type ForeignKey struct {
	Name             string   `json:"name"`
	Columns          []string `json:"columns"`
	ReferenceTable   string   `json:"referenceTable"`
	ReferenceColumns []string `json:"referenceColumns"`
	OnDelete         string   `json:"onDelete"`
	OnUpdate         string   `json:"onUpdate"`
}
type Index struct {
	Name    string   `json:"name"`
	Columns []string `json:"columns"`
	Unique  bool     `json:"unique"`
}
type Table struct {
	Name        string       `json:"name"`
	Columns     []Column     `json:"columns"`
	PrimaryKey  []string     `json:"primaryKey"`
	Unique      [][]string   `json:"unique"`
	ForeignKeys []ForeignKey `json:"foreignKeys"`
	Indexes     []Index      `json:"indexes"`
}
type Definition struct {
	Tables []Table `json:"tables"`
}
type ColumnMapping struct {
	Type    *string  `json:"type"`
	Default *Default `json:"default"`
}
type EngineMapping struct {
	Columns map[string]ColumnMapping `json:"columns"`
}
type Registration struct {
	Engine         string         `json:"engine"`
	EngineVersion  string         `json:"engineVersion"`
	RuntimeDigest  string         `json:"runtimeDigest"`
	AdapterVersion string         `json:"adapterVersion"`
	Settings       map[string]any `json:"settings"`
}
type Profile struct {
	ID             string         `json:"id"`
	Key            string         `json:"key"`
	DisplayName    string         `json:"displayName"`
	Engine         string         `json:"engine"`
	EngineVersion  string         `json:"engineVersion"`
	RuntimeDigest  string         `json:"runtimeDigest"`
	AdapterVersion string         `json:"adapterVersion"`
	Settings       map[string]any `json:"settings"`
	Fingerprint    string         `json:"fingerprint"`
}
type Payload struct {
	ContractVersion         int                         `json:"contractVersion"`
	AssignmentID            string                      `json:"assignmentId"`
	SpecVersionID           string                      `json:"specVersionId"`
	DatasetVersionID        string                      `json:"datasetVersionId"`
	EngineTargetID          string                      `json:"engineTargetId"`
	ValidationRunID         *string                     `json:"validationRunId"`
	Profile                 Profile                     `json:"profile"`
	MaterializationKey      string                      `json:"materializationKey"`
	ArtifactKey             string                      `json:"artifactKey"`
	DatasetHash             string                      `json:"datasetHash"`
	Definition              Definition                  `json:"definition"`
	Seed                    map[string][]map[string]any `json:"seed"`
	EngineOverrides         map[string]EngineMapping    `json:"engineOverrides"`
	Mode                    string                      `json:"mode"`
	Source                  string                      `json:"source"`
	ReferenceSQL            *string                     `json:"referenceSql"`
	AllowMultipleStatements bool                        `json:"allowMultipleStatements"`
	Comparison              Comparison                  `json:"comparison"`
	StateCheck              StateCheck                  `json:"stateCheck"`
	SchemaCheck             SchemaCheck                 `json:"schemaCheck"`
	Limits                  Limits                      `json:"limits"`
	Expected                json.RawMessage             `json:"expected"`
	ExpectedContentHash     *string                     `json:"expectedContentHash"`
}
type Job struct {
	ID             string          `json:"id"`
	SubmissionID   *string         `json:"submissionId"`
	UserID         *string         `json:"userId"`
	Kind           string          `json:"kind"`
	Status         string          `json:"status"`
	LeaseToken     *string         `json:"leaseToken"`
	LeaseExpiresAt *time.Time      `json:"leaseExpiresAt"`
	Payload        Payload         `json:"payload"`
	Result         json.RawMessage `json:"result"`
	CreatedAt      *time.Time      `json:"createdAt"`
}
type PublicError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}
type Failure struct {
	PublicError
	Verdict string
}

func (f *Failure) Error() string { return f.Code + ": " + f.Message }
func Fail(code, message string) *Failure {
	return &Failure{PublicError{code, boundedText(message, 3000)}, "RuntimeError"}
}
func Unavailable(message string) *Failure {
	return &Failure{PublicError{"SQL_ENGINE_UNAVAILABLE", message}, "JudgeUnavailable"}
}
func OutputLimit() *Failure {
	return &Failure{PublicError{"SQL_RESULT_LIMIT", "The SQL operation exceeded its bounded row, byte, memory or storage budget."}, "OutputLimitExceeded"}
}
func Timeout() *Failure {
	return &Failure{PublicError{"SQL_TIMEOUT", "The SQL execution time limit was exceeded."}, "TimeLimitExceeded"}
}

var ErrLostLease = fmt.Errorf("SQL execution lease is no longer owned by this worker")
var hashRE = regexp.MustCompile(`^[a-f0-9]{64}$`)
var digestRE = regexp.MustCompile(`^sha256:[a-f0-9]{64}$`)
var uuidRE = regexp.MustCompile(`^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$`)

func boundedText(v string, size int) string {
	r := []rune(v)
	if len(r) > size {
		return string(r[:size])
	}
	return v
}

type Cell struct {
	Type  string `json:"type"`
	Value any    `json:"value"`
}
type ResultSet struct {
	Columns   []string `json:"columns"`
	Rows      [][]Cell `json:"rows"`
	Truncated bool     `json:"truncated"`
	Statement int      `json:"statement,omitempty"`
}
type Schema struct {
	Tables []map[string]any `json:"tables"`
}
type Snapshot struct {
	Results            []ResultSet          `json:"results"`
	Schema             Schema               `json:"schema"`
	Data               map[string]ResultSet `json:"data"`
	VerificationData   map[string]ResultSet `json:"verificationData"`
	AffectedRows       int64                `json:"affectedRows"`
	ExecutionMS        int64                `json:"executionMs"`
	InspectionMS       int64                `json:"inspectionMs"`
	Error              *PublicError         `json:"error"`
	PreviewError       *PublicError         `json:"previewError,omitempty"`
	ErrorVerdict       string               `json:"errorVerdict,omitempty"`
	StatementsExecuted int                  `json:"statementsExecuted"`
}

func EmptySnapshot() Snapshot {
	return Snapshot{Results: []ResultSet{}, Schema: Schema{Tables: []map[string]any{}}, Data: map[string]ResultSet{}, VerificationData: map[string]ResultSet{}}
}

// Public is an allowlist. Neither reference SQL nor verificationData ever leaves this boundary.
func (s Snapshot) Public() map[string]any {
	return map[string]any{"results": s.Results, "schema": s.Schema, "data": s.Data, "affectedRows": s.AffectedRows, "executionMs": s.ExecutionMS, "inspectionMs": s.InspectionMS, "error": s.Error, "previewError": s.PreviewError, "statementsExecuted": s.StatementsExecuted}
}

type Artifact struct {
	FormatVersion int                  `json:"formatVersion"`
	Mode          string               `json:"mode"`
	Result        *ResultSet           `json:"result,omitempty"`
	Tables        map[string]ResultSet `json:"tables,omitempty"`
	Schema        *Schema              `json:"schema,omitempty"`
}

func RecoverCache(message string) *Failure {
	return &Failure{PublicError{"SQL_CACHE_RECOVERY_REQUIRED", message}, "JudgeUnavailable"}
}
