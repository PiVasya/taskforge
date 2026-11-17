#!/usr/bin/env bash
set -Eeuo pipefail
# Explicit opt-in only. Never installs into production and never touches GitHub.
if [[ "${TASKFORGE_YUGABYTE_LAB_ACK:-}" != 'yes-lab-only' ]]; then
  echo 'LAB ONLY. Set TASKFORGE_YUGABYTE_LAB_ACK=yes-lab-only to allow install.' >&2
  exit 2
fi
for cmd in kubectl helm python3; do command -v "$cmd" >/dev/null || { echo "Missing: $cmd" >&2; exit 2; }; done
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NAMESPACE="${YB_NAMESPACE:-taskforge-db}"
[[ "$NAMESPACE" =~ ^[a-z0-9][a-z0-9-]*[a-z0-9]$ ]] || { echo 'Invalid DB namespace' >&2; exit 2; }
# Do not fake RF3: require THREE distinct schedulable nodes explicitly labeled for DB.
node_json="$(kubectl get nodes -l taskforge.io/db-node=true -o json)"
NODE_JSON="$node_json" python3 - <<'PY'
import json,os,sys
nodes=json.loads(os.environ['NODE_JSON'])['items']
eligible=[n['metadata']['name'] for n in nodes if not n['spec'].get('unschedulable') and
          any(c.get('type')=='Ready' and c.get('status')=='True' for c in n.get('status',{}).get('conditions',[]))]
if len(eligible)<3:
    sys.exit(f'ERROR: 3 separate Ready, schedulable labeled nodes required. Found: {eligible}')
print('Eligible DB physical nodes:',', '.join(eligible))
PY
helm repo add yugabytedb https://charts.yugabyte.com --force-update
helm repo update yugabytedb
helm upgrade --install tf-yugabyte yugabytedb/yugabyte \
  --version 2026.1.3 --namespace "$NAMESPACE" --create-namespace \
  --values "$HERE/helm-values-lab.yaml" --wait --timeout 15m
"$HERE/verify-lab.sh"
echo 'LAB installed. Do NOT connect public internet or actual production clients.'
