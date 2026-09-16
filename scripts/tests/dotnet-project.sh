#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

project="${1:-}"
if [ -z "$project" ] || [ ! -f "$project" ]; then
  echo "ERROR: usage: $0 services/.../Project.csproj" >&2
  exit 2
fi
if [[ "$project" != services/* ]] || [[ "$project" == */tests/* ]]; then
  echo "ERROR: expected one production service project, got: $project" >&2
  exit 2
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'ERROR: .NET SDK is required.' >&2
  exit 2
fi
if ! dotnet --list-sdks | grep -Eq '^10\.'; then
  echo 'ERROR: .NET 10 SDK is required.' >&2
  exit 2
fi

root="$(dirname "$project")"
mapfile -t tests < <(find "$root/tests" -type f -name '*Tests.csproj' 2>/dev/null | sort || true)

if [ "${#tests[@]}" -eq 0 ]; then
  printf '========== DOTNET BUILD: %s ==========\n' "$project"
  dotnet build "$project" -c Release --nologo --verbosity minimal
  printf '\nPASS: %s built; no colocated behavior test project exists.\n' "$project"
  exit 0
fi

# Every retained test project has a ProjectReference to its production service.
# dotnet test therefore compiles the service and the tests in one graph; a separate
# dotnet build here would only compile the same service twice.
for test_project in "${tests[@]}"; do
  printf '========== DOTNET TEST + BUILD: %s ==========\n' "$test_project"
  dotnet test "$test_project" -c Release --nologo --verbosity minimal
  printf '\n'
done

printf 'PASS: %s validated through %d colocated test project(s).\n' "$project" "${#tests[@]}"
