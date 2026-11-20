#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -Eq '^10\.'; then
  echo 'ERROR: .NET 10 SDK is required for the six K3s API build/test gates.' >&2
  exit 2
fi

# Match the six DB-owning production API projects in k3s-sandbox-ci.yml.
projects=(
  'identity-api|services/identity/api/TaskForge.Identity.Api.csproj|services/identity/api/tests/TaskForge.Identity.Api.Tests/TaskForge.Identity.Api.Tests.csproj'
  'education-api|services/education/api/TaskForge.Education.Api.csproj|services/education/api/tests/TaskForge.Education.Api.Tests/TaskForge.Education.Api.Tests.csproj'
  'content-api|services/content/api/learning-content-service.csproj|'
  'tasks-api|services/tasks/assignment-api/TaskForge.Tasks.Api.csproj|services/tasks/assignment-api/tests/TaskForge.Tasks.Api.Tests/TaskForge.Tasks.Api.Tests.csproj'
  'solutions-api|services/solutions/api/TaskForge.Solutions.Api.csproj|services/solutions/api/tests/TaskForge.Solutions.Api.Tests/TaskForge.Solutions.Api.Tests.csproj'
  'execution-api|services/execution/api/TaskForge.Execution.Api.csproj|services/execution/api/tests/TaskForge.Execution.Api.Tests/TaskForge.Execution.Api.Tests.csproj'
)

output="$(mktemp -d -t taskforge-k3s-dotnet.XXXXXXXX)"
trap 'rm -rf -- "$output"' EXIT
for item in "${projects[@]}"; do
  IFS='|' read -r name project test_project <<< "$item"
  printf '\n========== K3S .NET RELEASE: %s ==========\n' "$name"
  dotnet publish "$project" -c Release -o "$output/$name" /p:UseAppHost=false
  if [[ -n "$test_project" ]]; then
    dotnet test "$test_project" -c Release --logger 'console;verbosity=normal'
  fi
done
printf '\nPASS: all six K3s API Release publications and five API test suites.\n'
