#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  echo 'ERROR: .NET SDK is required for TaskForge .NET behavior tests.' >&2
  exit 2
fi
if ! dotnet --list-sdks | grep -Eq '^10\.'; then
  echo 'ERROR: .NET 10 SDK is required for TaskForge .NET behavior tests.' >&2
  exit 2
fi

mapfile -t projects < <(find services -type f -name '*Tests.csproj' | sort)
if [ "${#projects[@]}" -eq 0 ]; then
  echo 'No .NET test projects found.' >&2
  exit 1
fi

failures=()
printf 'Running %d .NET test project(s)\n' "${#projects[@]}"
for project in "${projects[@]}"; do
  printf '\n========== DOTNET TEST: %s ==========\n' "$project"
  if dotnet test "$project" -c Release --nologo --verbosity minimal; then
    printf 'PASS: %s\n' "$project"
  else
    failures+=("$project")
    printf 'FAIL: %s\n' "$project" >&2
  fi
done

if [ "${#failures[@]}" -ne 0 ]; then
  printf '\nFAIL: %d/%d .NET test project(s) failed:\n' "${#failures[@]}" "${#projects[@]}" >&2
  printf '  - %s\n' "${failures[@]}" >&2
  exit 1
fi

printf '\nPASS: all %d .NET test project(s) passed.\n' "${#projects[@]}"
