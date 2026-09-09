package sqlworker

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"math/big"
	"time"
)

type Outcome struct {
	Verdict string
	Result  map[string]any
}
type JobRunner struct {
	Pool      *Pool
	Processes *ProcessRunner
	Metrics   *Metrics
}

func ValidatePayload(job Job, profile Profile) error {
	p := job.Payload
	if p.ContractVersion != ContractVersion || !contains([]string{"sql-check", "sql-preview", "sql-materialize"}, job.Kind) {
		return Unavailable("Unsupported SQL execution contract or job kind.")
	}
	if !sameJSON(p.Profile, profile) || profile.AdapterVersion != AdapterVersion {
		return Unavailable("The exact immutable SQL runtime profile is not available on this worker.")
	}
	if !contains([]string{"result", "state", "schema"}, p.Mode) {
		return Unavailable("Unsupported SQL verification mode.")
	}
	for _, key := range []string{p.MaterializationKey, p.ArtifactKey, p.DatasetHash} {
		if !hashRE.MatchString(key) {
			return Unavailable("Invalid immutable SQL identity.")
		}
	}
	if e := p.Limits.Validate(); e != nil {
		return e
	}
	tolerance, e := parseDecimal(p.Comparison.NumericTolerance.String())
	if e != nil || tolerance.Sign() < 0 || tolerance.Cmp(big.NewRat(1000000, 1)) > 0 {
		return Unavailable("Invalid numeric comparison tolerance.")
	}
	if job.Kind == "sql-check" {
		if len(p.Expected) < 2 || len(p.Expected) > 2_000_000 || p.ExpectedContentHash == nil {
			return Unavailable("The immutable expected artifact is missing.")
		}
		var expected Artifact
		if e = DecodeStrict(p.Expected, &expected); e != nil {
			return Unavailable("Invalid expected artifact contract.")
		}
		// Hash the raw decoded document, not a reserialized struct with default fields.
		var document any
		if e = DecodeStrict(p.Expected, &document); e != nil {
			return Unavailable("Invalid expected artifact.")
		}
		hash, err := ContentHash(document)
		if err != nil || hash != *p.ExpectedContentHash {
			return Unavailable("The immutable expected artifact failed its integrity check.")
		}
		if expected.FormatVersion != 1 || expected.Mode != p.Mode {
			return Unavailable("The expected artifact does not match this verification mode.")
		}
	}
	if job.Kind != "sql-materialize" && p.ReferenceSQL != nil && *p.ReferenceSQL != "" {
		return Unavailable("Learner jobs must not contain reference SQL.")
	}
	if job.Kind == "sql-preview" && len(p.Expected) > 0 && string(p.Expected) != "null" {
		return Unavailable("Preview jobs must not contain hidden expected artifacts.")
	}
	return nil
}
func (r *JobRunner) executeOnce(ctx context.Context, a EngineAdapter, p Payload, source string) (snapshot Snapshot, err error) {
	lease, e := r.Pool.Acquire(ctx, a, p)
	if e != nil {
		return EmptySnapshot(), e
	}
	slog.Debug("sql_sandbox_leased", "engine", a.Engine(), "materialization", p.MaterializationKey, "sandbox", lease.Sandbox.ID)
	defer func() {
		if cleanup := lease.Release(); cleanup != nil {
			snapshot = EmptySnapshot()
			err = cleanup
		}
	}()
	done := r.Metrics.Measure("sql_db_execution", a.Engine())
	defer done()
	return r.Processes.Run(ctx, lease, p, source)
}
func snapshotError(s Snapshot) error {
	if s.Error == nil {
		return nil
	}
	verdict := s.ErrorVerdict
	if !contains([]string{"RuntimeError", "TimeLimitExceeded", "OutputLimitExceeded", "JudgeUnavailable"}, verdict) {
		verdict = "RuntimeError"
	}
	return &Failure{*s.Error, verdict}
}
func publicSnapshot(s Snapshot) (map[string]any, error) {
	out := s.Public()
	raw, e := json.Marshal(out)
	if e != nil || len(raw) > 2_800_000 {
		return nil, OutputLimit()
	}
	return out, nil
}
func (r *JobRunner) Run(ctx context.Context, job Job, a EngineAdapter, profile Profile) (out Outcome, err error) {
	started := time.Now()
	datasetValid := false
	defer func() {
		if recover() != nil {
			err = Unavailable("An internal SQL execution invariant failed.")
		}
		r.Metrics.Inc("sql_attempt_duration_seconds_sum", a.Engine(), time.Since(started).Seconds())
		r.Metrics.Inc("sql_attempt_duration_seconds_count", a.Engine(), 1)
		if ctx.Err() != nil {
			out = Outcome{}
			err = ErrLostLease
			return
		}
		if err != nil {
			if errors.Is(err, ErrLostLease) {
				out = Outcome{}
				return
			}
			failure := NormalizeFailure(err)
			if failure.Verdict == "TimeLimitExceeded" {
				r.Metrics.Inc("sql_timeouts", a.Engine(), 1)
			}
			if failure.Verdict == "OutputLimitExceeded" {
				r.Metrics.Inc("sql_result_limit_hits", a.Engine(), 1)
			}
			if job.Kind == "sql-materialize" {
				out = Outcome{"ValidationFailed", map[string]any{"datasetValid": datasetValid, "error": failure.PublicError}}
			} else {
				s := EmptySnapshot()
				snapshotFailure(&s, failure)
				out = Outcome{failure.Verdict, s.Public()}
			}
			err = nil
		}
	}()
	if err = ValidatePayload(job, profile); err != nil {
		return
	}
	if err = ctx.Err(); err != nil {
		return
	}
	p := job.Payload
	if job.CreatedAt != nil {
		r.Metrics.Inc("sql_queue_wait_seconds_sum", a.Engine(), max(0, time.Since(*job.CreatedAt).Seconds()))
		r.Metrics.Inc("sql_queue_wait_seconds_count", a.Engine(), 1)
	}
	if job.Kind == "sql-materialize" {
		if err = r.Pool.Warm(ctx, a, p); err != nil {
			return
		}
		datasetValid = true
		reference := ""
		if p.ReferenceSQL != nil {
			reference = *p.ReferenceSQL
		}
		var stmts []Statement
		stmts, err = SplitScript(reference, a.Engine(), p.AllowMultipleStatements, p.Limits.MaxStatements)
		if err != nil {
			return
		}
		if err = ValidateReference(stmts, p.Mode); err != nil {
			return
		}
		var expected Artifact
		for pass := 0; pass < 2; pass++ {
			var snapshot Snapshot
			snapshot, err = r.executeOnce(ctx, a, p, reference)
			if err != nil {
				return
			}
			if err = snapshotError(snapshot); err != nil {
				return
			}
			done := r.Metrics.Measure("sql_verification", a.Engine())
			var current Artifact
			current, err = MakeArtifact(snapshot, p)
			if err != nil {
				done()
				return
			}
			if pass == 0 {
				expected = current
			} else {
				var equal bool
				equal, err = Verify(ctx, current, expected, p)
				if err == nil && !equal {
					err = Fail("SQL_REFERENCE_NONDETERMINISTIC", "The reference does not produce a repeatable result on two fresh sandboxes.")
				}
			}
			done()
			if err != nil {
				return
			}
		}
		var raw []byte
		raw, err = json.Marshal(expected)
		if err != nil {
			return
		}
		if len(raw) > 2_000_000 {
			err = OutputLimit()
			return
		}
		var hash string
		hash, err = ContentHash(expected)
		if err != nil {
			return
		}
		out = Outcome{"Validated", map[string]any{"datasetValid": true, "expected": expected, "expectedContentHash": hash, "error": nil}}
		return
	}
	if _, err = SplitScript(p.Source, a.Engine(), p.AllowMultipleStatements, p.Limits.MaxStatements); err != nil {
		return
	}
	var snapshot Snapshot
	snapshot, err = r.executeOnce(ctx, a, p, p.Source)
	if err != nil {
		return
	}
	var public map[string]any
	public, err = publicSnapshot(snapshot)
	if err != nil {
		return
	}
	if fail := snapshotError(snapshot); fail != nil {
		failure := NormalizeFailure(fail)
		out = Outcome{failure.Verdict, public}
		if failure.Verdict == "TimeLimitExceeded" {
			r.Metrics.Inc("sql_timeouts", a.Engine(), 1)
		}
		if failure.Verdict == "OutputLimitExceeded" {
			r.Metrics.Inc("sql_result_limit_hits", a.Engine(), 1)
		}
		return
	}
	if job.Kind == "sql-preview" {
		out = Outcome{"Previewed", public}
		return
	}
	done := r.Metrics.Measure("sql_verification", a.Engine())
	defer done()
	var expected, current Artifact
	if err = DecodeStrict(p.Expected, &expected); err != nil {
		return
	}
	current, err = MakeArtifact(snapshot, p)
	if err != nil {
		return
	}
	var passed bool
	passed, err = Verify(ctx, current, expected, p)
	if err != nil {
		return
	}
	message, verdict := "The solution does not meet the assignment checks.", "WrongAnswer"
	if passed {
		message, verdict = "The solution is correct.", "Accepted"
	}
	public["check"] = map[string]any{"mode": p.Mode, "passed": passed, "message": message}
	out = Outcome{verdict, public}
	return
}
