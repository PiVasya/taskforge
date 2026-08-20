#!/usr/bin/env bash
set -Eeuo pipefail

HA_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -x "$HA_DIR/../compose.sh" ]; then
  TASKFORGE_ROOT="$(cd "$HA_DIR/.." && pwd)"
  TASKFORGE_COMPOSE="$TASKFORGE_ROOT/compose.sh"
  TASKFORGE_ENV_FILE="${TASKFORGE_ENV_FILE:-$TASKFORGE_ROOT/.env}"
  TASKFORGE_SECRET_HELPER="$TASKFORGE_ROOT/generate-secrets.sh"
  TASKFORGE_CHECK_HELPER="$TASKFORGE_ROOT/check.sh"
  TASKFORGE_BACKUP_HELPER="$TASKFORGE_ROOT/backup.sh"
else
  TASKFORGE_ROOT="$(cd "$HA_DIR/../.." && pwd)"
  TASKFORGE_COMPOSE="$TASKFORGE_ROOT/deploy/prod/compose.sh"
  TASKFORGE_ENV_FILE="${TASKFORGE_ENV_FILE:-$TASKFORGE_ROOT/deploy/prod/.env}"
  TASKFORGE_SECRET_HELPER="$TASKFORGE_ROOT/scripts/prod/prepare-env.sh"
  TASKFORGE_CHECK_HELPER="$TASKFORGE_ROOT/scripts/prod/check-prod-config.sh"
  TASKFORGE_BACKUP_HELPER=""
fi
export TASKFORGE_ROOT TASKFORGE_ENV_FILE

ha_die(){ echo "error: $*" >&2; exit 2; }
ha_need_root(){ [ "$(id -u)" -eq 0 ] || ha_die "run this command with sudo/root"; }
ha_need_env(){ [ -f "$TASKFORGE_ENV_FILE" ] || ha_die "missing $TASKFORGE_ENV_FILE"; }
ha_read_env(){
  python3 - "$TASKFORGE_ENV_FILE" "$1" <<'PY'
from pathlib import Path
import sys
key=sys.argv[2]; value=''
for raw in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if not raw or raw.lstrip().startswith('#') or '=' not in raw: continue
    k,v=raw.split('=',1)
    if k.strip()==key: value=v.strip().strip('"').strip("'")
print(value)
PY
}
ha_set_env(){
  python3 - "$TASKFORGE_ENV_FILE" "$1" "$2" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]); key=sys.argv[2]; value=sys.argv[3]
lines=p.read_text(encoding='utf-8-sig').splitlines() if p.exists() else []
out=[]; found=False
for line in lines:
    if line.lstrip().startswith('#') or '=' not in line:
        out.append(line); continue
    k=line.split('=',1)[0].strip()
    if k==key:
        if not found: out.append(f'{key}={value}'); found=True
    else: out.append(line)
if not found: out.append(f'{key}={value}')
p.write_text('\n'.join(out).rstrip()+'\n',encoding='utf-8')
PY
}
ha_bool(){ case "${1,,}" in 1|true|yes|on) return 0;; *) return 1;; esac; }
ha_agent(){ python3 "$HA_DIR/agent.py" --env-file "$TASKFORGE_ENV_FILE" "$@"; }
ha_compose(){ "$TASKFORGE_COMPOSE" "$@"; }
ha_require_local_ip(){
  local ip="$1"
  command -v ip >/dev/null 2>&1 || ha_die "iproute2/ip command is required"
  ip -o addr show | grep -Eq "[[:space:]]${ip}/" || ha_die "WireGuard IP $ip is not present on this host; configure wg-taskforge first"
}
ha_check(){
  [ -x "$TASKFORGE_CHECK_HELPER" ] || ha_die "configuration checker not found: $TASKFORGE_CHECK_HELPER"
  "$TASKFORGE_CHECK_HELPER"
}
