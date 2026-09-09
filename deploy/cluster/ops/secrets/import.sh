#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
archive="${1:-}"
[ -s "$archive" ] || cluster_die "usage: sudo ./cluster/ops/secrets/import.sh taskforge-cluster-secrets.enc"
command -v openssl >/dev/null 2>&1 || cluster_die "openssl is required"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
openssl enc -d -aes-256-cbc -pbkdf2 -iter 250000 -in "$archive" | tar -C "$tmp" -xzf -
[ -s "$tmp/env" ] || cluster_die "encrypted bundle does not contain env"
if [ -f "$TASKFORGE_ENV_FILE" ]; then
  cp -p "$TASKFORGE_ENV_FILE" "$TASKFORGE_ENV_FILE.before-cluster-secrets.$(date -u +%Y%m%d-%H%M%S)"
fi
install -m 600 "$tmp/env" "$TASKFORGE_ENV_FILE"
chown "$(cluster_project_owner)" "$TASKFORGE_ENV_FILE"
# The encrypted bundle may have been exported from a different filesystem path.
# Rewrite only the two file locations that are intentionally node-local.
python3 - "$TASKFORGE_ENV_FILE" "$TASKFORGE_ROOT" <<'PY_ENV_PATHS'
from pathlib import Path
import sys
path=Path(sys.argv[1])
root=Path(sys.argv[2])
values={
    'CODE_ANALYZER_PRIVATE_KEY_PATH': str(root / '.runtime/code-analyzer-keys/code-analyzer-private.pem'),
    'CODE_ANALYZER_PUBLIC_KEY_PATH': str(root / '.runtime/code-analyzer-keys/code-analyzer-public.pem'),
}
lines=path.read_text(encoding='utf-8-sig').splitlines()
seen=set()
out=[]
for raw in lines:
    if '=' in raw and not raw.lstrip().startswith('#'):
        key=raw.split('=',1)[0].strip()
        if key in values:
            out.append(f'{key}={values[key]}')
            seen.add(key)
            continue
    out.append(raw)
for key,value in values.items():
    if key not in seen:
        out.append(f'{key}={value}')
path.write_text('\n'.join(out).rstrip()+'\n', encoding='utf-8')
PY_ENV_PATHS
cluster_runtime_prepare
[ ! -d "$tmp/tls" ] || cp -a "$tmp/tls/." "$TASKFORGE_CLUSTER_RUNTIME/tls/"
chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/tls/privkey.pem" 2>/dev/null || true
private="$(cluster_read_env CODE_ANALYZER_PRIVATE_KEY_PATH)"
public="$(cluster_read_env CODE_ANALYZER_PUBLIC_KEY_PATH)"
if [ -s "$tmp/code-analyzer/private.pem" ] && [ -n "$private" ]; then
  install -d -m 700 "$(dirname "$private")"
  install -m 600 "$tmp/code-analyzer/private.pem" "$private"
fi
if [ -s "$tmp/code-analyzer/public.pem" ] && [ -n "$public" ]; then
  install -d -m 755 "$(dirname "$public")"
  install -m 644 "$tmp/code-analyzer/public.pem" "$public"
fi
if [ -s "$tmp/registry-config.json" ]; then
  install -m 600 "$tmp/registry-config.json" "$TASKFORGE_ROOT/config.json"
  chown "$(cluster_project_owner)" "$TASKFORGE_ROOT/config.json"
fi
cluster_fix_runtime_owner
# Import runs through sudo. Return all runtime files to the deployment account,
# not only .runtime/cluster, so check.sh can create caches immediately.
if [ -d "$TASKFORGE_ROOT/.runtime" ]; then
  chown -R "$(cluster_project_owner)" "$TASKFORGE_ROOT/.runtime"
  chmod 700 "$TASKFORGE_ROOT/.runtime" 2>/dev/null || true
fi
echo "TaskForge secrets imported. Run ./check.sh and then bash ./cluster.sh apply NODE_ID."
