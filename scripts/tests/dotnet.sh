#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  echo 'ERROR: .NET SDK is required for TaskForge production builds and behavior tests.' >&2
  exit 2
fi
if ! dotnet --list-sdks | grep -Eq '^10\.'; then
  echo 'ERROR: .NET 10 SDK is required for TaskForge production builds and behavior tests.' >&2
  exit 2
fi

mapfile -t production_projects < <(find services -type f -name '*.csproj' ! -path '*/tests/*' | sort)
mapfile -t test_projects < <(find services -type f -name '*Tests.csproj' | sort)

if [ "${#production_projects[@]}" -eq 0 ]; then
  echo 'No production .NET projects found.' >&2
  exit 1
fi
if [ "${#test_projects[@]}" -eq 0 ]; then
  echo 'No .NET test projects found.' >&2
  exit 1
fi

build_failures=()
test_failures=()

printf 'Building %d production .NET project(s) before tests\n' "${#production_projects[@]}"
for project in "${production_projects[@]}"; do
  printf '\n========== DOTNET BUILD: %s ==========\n' "$project"
  if dotnet build "$project" -c Release --nologo --verbosity minimal; then
    printf 'PASS BUILD: %s\n' "$project"
  else
    build_failures+=("$project")
    printf 'FAIL BUILD: %s\n' "$project" >&2
  fi
done

printf '\nRunning %d .NET test project(s)\n' "${#test_projects[@]}"
for project in "${test_projects[@]}"; do
  printf '\n========== DOTNET TEST: %s ==========\n' "$project"
  if dotnet test "$project" -c Release --nologo --verbosity minimal; then
    printf 'PASS TEST: %s\n' "$project"
  else
    test_failures+=("$project")
    printf 'FAIL TEST: %s\n' "$project" >&2
  fi
done

if [ "${#build_failures[@]}" -ne 0 ] || [ "${#test_failures[@]}" -ne 0 ]; then
  printf '\n.NET PREFLIGHT FAILED\n' >&2
  if [ "${#build_failures[@]}" -ne 0 ]; then
    printf 'Production build failures: %d/%d\n' "${#build_failures[@]}" "${#production_projects[@]}" >&2
    printf '  - %s\n' "${build_failures[@]}" >&2
  fi
  if [ "${#test_failures[@]}" -ne 0 ]; then
    printf 'Behavior test failures: %d/%d\n' "${#test_failures[@]}" "${#test_projects[@]}" >&2
    printf '  - %s\n' "${test_failures[@]}" >&2
  fi
  exit 1
fi

printf '\nPASS: all %d production .NET project(s) built and all %d test project(s) passed.\n' \
  "${#production_projects[@]}" "${#test_projects[@]}"
