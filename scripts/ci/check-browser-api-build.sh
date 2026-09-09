#!/usr/bin/env bash
set -euo pipefail

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$ROOT"

PROJECT="services/browser/api/TaskForge.Browser.Api.csproj"
DOCKERFILE="services/browser/api/Dockerfile"
RID="linux-x64"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/taskforge-browser-api-build.XXXXXX")"
OUT="$WORK/publish"
PACKAGES="$WORK/nuget-packages"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT" "$PACKAGES"

# Playwright PDF dimensions document px/in/cm/mm. Do not regress to unsupported pt.
if grep -Eq 'Width = .*pt|Height = .*pt|size:[^;]*pt' services/browser/api/Services/SiteInspectionService.cs; then
  echo "Browser API build check failed: unsupported 'pt' PDF dimension found; use px/in/cm/mm." >&2
  exit 1
fi

# project.assets.json and the NuGet global-packages folder must have the same
# lifetime for a --no-restore publish. Keeping only the package folder in a
# BuildKit cache mount can leave a cached assets file without its packages on a
# fresh builder and produces NETSDK1064.
if grep -Eq -- '--mount=type=cache[^[:cntrl:]]*target=/root/\.nuget/packages' "$DOCKERFILE"; then
  echo "Browser API build check failed: Dockerfile keeps NuGet packages only in a BuildKit cache mount." >&2
  echo "Persist the restore output in the build layer before using publish --no-restore." >&2
  exit 1
fi

export NUGET_PACKAGES="$PACKAGES"

printf '[browser-build] restore %s for %s with isolated NuGet packages\n' "$PROJECT" "$RID"
dotnet restore "$PROJECT" --runtime "$RID"

printf '[browser-build] publish %s for %s\n' "$PROJECT" "$RID"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  --no-restore \
  -o "$OUT" \
  /p:DebugType=None \
  /p:DebugSymbols=false

test -f "$OUT/TaskForge.Browser.Api" || {
  echo "Browser API build check failed: self-contained executable was not produced." >&2
  exit 1
}

printf '[browser-build] URL policy regression checks\n'
dotnet run --project tools/browser-url-policy-check/TaskForge.Browser.UrlPolicyCheck.csproj -c Release

printf '[browser-build] session access regression checks\n'
dotnet run --project tools/browser-session-access-check/TaskForge.Browser.SessionAccessCheck.csproj -c Release

printf '[browser-build] publish ok\n'
