#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_env
node="${1:-B}"
peer_local_ip="${2:-10.80.0.2}"
source_ip="${3:-10.80.0.1}"
out="${4:-$TASKFORGE_ROOT/.runtime/ha/${node}.env}"
mkdir -p "$(dirname "$out")"
cp -p "$TASKFORGE_ENV_FILE" "$out"
chmod 600 "$out"
python3 - "$out" "$node" "$peer_local_ip" "$source_ip" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]); node=sys.argv[2]; local=sys.argv[3]; peer=sys.argv[4]
updates={
 'HA_ENABLED':'true','HA_NODE_ID':node,'HA_PREFERRED_NODE':'A',
 'HA_WG_IP':local,'HA_PEER_WG_IP':peer,'POSTGRES_BIND':local,
 'MINIO_BIND':local,'POSTGRES_RESTART_POLICY':'no','TASKFORGE_NODE_ROLE':'standby',
 'CODE_ANALYZER_PRIVATE_KEY_PATH':'','CODE_ANALYZER_PUBLIC_KEY_PATH':'',
}
lines=p.read_text(encoding='utf-8-sig').splitlines(); seen=set(); out=[]
for line in lines:
    if not line or line.lstrip().startswith('#') or '=' not in line:
        out.append(line); continue
    k=line.split('=',1)[0].strip()
    if k in updates:
        if k not in seen: out.append(f'{k}={updates[k]}'); seen.add(k)
    else: out.append(line)
for k,v in updates.items():
    if k not in seen: out.append(f'{k}={v}')
p.write_text('\n'.join(out).rstrip()+'\n',encoding='utf-8')
PY
echo "Peer env created: $out"
echo "Copy it to the second server as .env using scp/rsync over SSH, then run generate-secrets.sh --yes there."
