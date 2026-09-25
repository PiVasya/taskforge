#!/usr/bin/env bash
# Shared, side-effect-light helpers for TaskForge MinIO bucket replication.
# Callers keep ownership of logging/error policy.

minio_rule_present_in_text(){
  local rules="$1" expected="$2"
  grep -Fq "Remote Bucket: $expected" <<<"$rules"
}

minio_rules_list(){
  local mc="$1" source_path="$2" out rc had_errexit=0

  # MinIO treats a bucket with zero replication rules as an error-like CLI
  # state: `mc replicate ls` exits non-zero and prints
  # "replication configuration not set". For reconciliation this is a valid
  # empty rule set, not a transport/auth failure. Only normalize this exact
  # empty-state family; every other error must remain fatal.
  [[ $- == *e* ]] && had_errexit=1
  set +e
  out="$("$mc" replicate ls "$source_path" 2>&1)"
  rc=$?
  if [ "$had_errexit" -eq 1 ]; then set -e; else set +e; fi

  if [ "$rc" -eq 0 ]; then
    printf '%s\n' "$out"
    return 0
  fi

  if grep -Eqi 'replication configuration( is)? not set|no replication configuration( is)? set' <<<"$out"; then
    return 0
  fi

  [ -z "$out" ] || printf '%s\n' "$out" >&2
  return "$rc"
}

# Add a replication rule and *verify the exact destination afterwards*.
# This intentionally does not accept generic "already/exists" text as success:
# MinIO also uses wording such as "already has a rule with this priority" for
# priority collisions. v34 swallowed that error and falsely printed configured.
#
# stdout: "existing" or "created"
# return: 0 only when expected destination is present after the operation.
minio_add_rule_verified(){
  local mc="$1" source_path="$2" remote_bucket="$3" expected_label="$4"
  local priority="$5" replicate_features="$6"
  local before after out rc had_errexit=0

  [[ $- == *e* ]] && had_errexit=1
  if ! before="$(minio_rules_list "$mc" "$source_path" 2>&1)"; then
    printf '%s\n' "$before" >&2
    return 1
  fi
  if minio_rule_present_in_text "$before" "$expected_label"; then
    printf '%s\n' existing
    return 0
  fi

  set +e
  out="$("$mc" replicate add "$source_path" \
    --remote-bucket "$remote_bucket" \
    --priority "$priority" \
    --replicate "$replicate_features" 2>&1)"
  rc=$?
  if [ "$had_errexit" -eq 1 ]; then set -e; else set +e; fi

  # Always verify state. This handles a concurrent/idempotent add safely, while
  # refusing the v34 priority-collision case where the destination is absent.
  if ! after="$(minio_rules_list "$mc" "$source_path" 2>&1)"; then
    [ -z "$out" ] || printf '%s\n' "$out" >&2
    printf '%s\n' "$after" >&2
    return 1
  fi
  if minio_rule_present_in_text "$after" "$expected_label"; then
    printf '%s\n' created
    return 0
  fi

  [ -z "$out" ] || printf '%s\n' "$out" >&2
  if [ "$rc" -eq 0 ]; then
    printf 'mc replicate add returned success but destination is still absent: %s\n' "$expected_label" >&2
  fi
  return 1
}

# Wait until an object is materially listed on the destination bucket.
# Do not use `mc stat`: bucket/site replication can proxy GET/HEAD requests,
# which can make a missing local replica look present. LIST is the stronger
# local-materialization check for this probe.
minio_wait_local_object(){
  local mc="$1" target_path="$2" needle="$3" timeout_seconds="${4:-60}"
  local deadline out
  deadline=$((SECONDS + timeout_seconds))
  while [ "$SECONDS" -lt "$deadline" ]; do
    out="$("$mc" ls --versions "$target_path" 2>/dev/null || true)"
    if grep -Fq "$needle" <<<"$out"; then
      return 0
    fi
    sleep 1
  done
  return 1
}
