#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BOOTSTRAP="$ROOT/services/execution/runners/onec-runner/bootstrap.sh"
td="$(mktemp -d /tmp/taskforge-onec-bootstrap.XXXXXX)"
trap 'rm -rf "$td"' EXIT
mkdir -p "$td/bin" "$td/platform" "$td/runtime" "$td/cache" "$td/source"

cat > "$td/bin/xvfb-run" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
while [ "$#" -gt 0 ]; do
  case "$1" in
    -a) shift ;;
    -s) shift 2 ;;
    *) exec "$@" ;;
  esac
done
exit 2
SH
chmod +x "$td/bin/xvfb-run"

cat > "$td/source/installer.run" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
: "${ONEC_PLATFORM_ROOT:?}"
: "${ONEC_TEST_INSTALL_COUNT:?}"
printf 'install\n' >> "$ONEC_TEST_INSTALL_COUNT"
dir="$ONEC_PLATFORM_ROOT/x86_64/${ONEC_PLATFORM_VERSION}"
mkdir -p "$dir"
cat > "$dir/1cv8" <<'INNER'
#!/usr/bin/env bash
set -Eeuo pipefail
if [ "${1:-}" = CREATEINFOBASE ]; then
  spec="${2:-}"; path="${spec#File=}"; path="${path%%;*}"
  mkdir -p "$path"; printf 'fake-db\n' > "$path/1Cv8.1CD"; exit 0
fi
exit 0
INNER
chmod +x "$dir/1cv8"
SH
chmod +x "$td/source/installer.run"
sha="$(sha256sum "$td/source/installer.run" | awk '{print $1}')"
export PATH="$td/bin:$PATH"
export ONEC_PLATFORM_ROOT="$td/platform"
export ONEC_RUNTIME_ROOT="$td/runtime"
export ONEC_INSTALL_CACHE_ROOT="$td/cache"
export ONEC_PLATFORM_VERSION=8.3.27.9999
export ONEC_INSTALL_SOURCE=file
export ONEC_INSTALL_FILE="$td/source/installer.run"
export ONEC_INSTALL_SHA256="$sha"
export ONEC_INSTALL_COMPONENTS=client_full,ru
export ONEC_TEST_INSTALL_COUNT="$td/install-count"
# A different already-installed version must never win over the explicitly requested one.
mkdir -p "$td/platform/x86_64/9.9.9.9"
printf '#!/bin/sh\nexit 0\n' > "$td/platform/x86_64/9.9.9.9/1cv8"
chmod +x "$td/platform/x86_64/9.9.9.9/1cv8"

bash "$BOOTSTRAP" >/dev/null
[ "$(readlink -f "$td/platform/taskforge/1cv8")" = "$td/platform/x86_64/8.3.27.9999/1cv8" ]
[ -x "$td/platform/taskforge/1cv8" ]
[ -s "$td/platform/.taskforge-platform" ]
[ -s "$td/runtime/templates/code/1Cv8.1CD" ]
[ "$(wc -l < "$td/install-count")" -eq 1 ]

# The second run has no installer source at all. Matching persistent state must
# still be sufficient and must not invoke the installer again.
rm -f "$td/source/installer.run"
export ONEC_INSTALL_SOURCE=auto
export ONEC_INSTALL_URL=
bash "$BOOTSTRAP" >/dev/null
[ "$(wc -l < "$td/install-count")" -eq 1 ]

echo '1C bootstrap idempotence smoke OK'
