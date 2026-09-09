#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if ! command -v python3 >/dev/null 2>&1; then
  printf 'ERROR: python3 is required for dependency checks. No migration was generated.\n' >&2
  exit 2
fi
python3 ./scripts/ci/check-sql-ef-dependencies.py
if ! command -v dotnet >/dev/null 2>&1; then
  printf 'ERROR: .NET 10 SDK is required. C# checks were not run; no migration was generated.\n' >&2
  exit 2
fi

PROJECT=./tools/sql-domain-check/TaskForge.Sql.DomainCheck.csproj
dotnet --version
dotnet tool restore
dotnet restore "$PROJECT" --force-evaluate
python3 ./scripts/ci/check-sql-ef-dependencies.py --resolved
dotnet build "$PROJECT" -c Release --no-restore -warnaserror:MSB3277
dotnet run --project "$PROJECT" -c Release --no-build --no-restore
