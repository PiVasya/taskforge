#!/usr/bin/env bash
set -euo pipefail

workflow=".github/workflows/develop-build.yml"
prod_dir="deploy/prod/compose"
cluster_compose="deploy/cluster/compose.cluster.yaml"

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

[ -f "$workflow" ] || fail "workflow not found: $workflow"
[ -d "$prod_dir" ] || fail "prod compose directory not found: $prod_dir"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

awk '
  /^[[:space:]]*"[A-Za-z0-9_-]+\|/ {
    line=$0
    sub(/^[[:space:]]*"/, "", line)
    sub(/"[[:space:]]*$/, "", line)
    split(line, parts, "|")
    if (length(parts[1]) > 0 && length(parts[2]) > 0 && length(parts[3]) > 0) {
      print parts[1] "|" parts[2] "|" parts[3]
    }
  }
' "$workflow" | sort > "$tmp/matrix_specs"

[ -s "$tmp/matrix_specs" ] || fail "could not parse any image specs from $workflow"

cut -d'|' -f1 "$tmp/matrix_specs" | sort > "$tmp/matrix_images"
awk -F'|' '
  {
    context=$2
    dockerfile=$3
    sub(/^\.\//, "", context)
    if (context == "." || context == "") print dockerfile
    else print context "/" dockerfile
  }
' "$tmp/matrix_specs" | sed 's#//#/#g' | sort > "$tmp/matrix_dockerfiles"

find . -name Dockerfile \
  -not -path './.git/*' \
  -print | sed 's#^./##' | sort > "$tmp/repo_dockerfiles"

while IFS= read -r path; do
  [ -f "$path" ] || fail "matrix references missing Dockerfile: $path"
done < "$tmp/matrix_dockerfiles"

missing_from_matrix="$(comm -23 "$tmp/repo_dockerfiles" "$tmp/matrix_dockerfiles" || true)"
if [ -n "$missing_from_matrix" ]; then
  echo "$missing_from_matrix" >&2
  fail "Dockerfiles above are not covered by develop build matrix"
fi

extra_in_matrix="$(comm -13 "$tmp/repo_dockerfiles" "$tmp/matrix_dockerfiles" || true)"
if [ -n "$extra_in_matrix" ]; then
  echo "$extra_in_matrix" >&2
  fail "matrix Dockerfiles above do not exist in repo"
fi

grep -hEo 'image:[[:space:]]*\$\{IMAGE_REPOSITORY[^}]*}/[^:[:space:]]*' "$prod_dir"/*.y*ml "$cluster_compose" \
  | sed -E 's#.*}/([^:[:space:]]*)#\1#' \
  | sort -u > "$tmp/prod_images"

prod_missing="$(comm -23 "$tmp/prod_images" "$tmp/matrix_images" || true)"
if [ -n "$prod_missing" ]; then
  echo "$prod_missing" >&2
  fail "prod compose images above are not present in build matrix"
fi

matrix_not_prod="$(comm -23 "$tmp/matrix_images" "$tmp/prod_images" || true)"
if [ -n "$matrix_not_prod" ]; then
  echo "$matrix_not_prod" >&2
  fail "matrix images above are not used by prod compose"
fi

echo "Workflow matrix integrity OK: $(wc -l < "$tmp/matrix_images") images, $(wc -l < "$tmp/repo_dockerfiles") Dockerfiles."
