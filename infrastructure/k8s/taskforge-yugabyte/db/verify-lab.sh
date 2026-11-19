#!/usr/bin/env bash
set -Eeuo pipefail
# Verifies cluster placement, RF3 metadata and YSQL peer visibility.
for cmd in kubectl python3; do command -v "$cmd" >/dev/null || { echo "Missing: $cmd" >&2; exit 2; }; done
NAMESPACE="${YB_NAMESPACE:-taskforge-db}"
RELEASE="tf-yugabyte"
PREFIX="taskforge-yb"
for component in yb-master yb-tserver; do
  pods="$(kubectl -n "$NAMESPACE" get pods -l "app.kubernetes.io/name=$component,release=$RELEASE" -o json)"
  ROLE="$component" PODS="$pods" python3 - <<'PY'
import json,os,sys
role=os.environ['ROLE']
items=json.loads(os.environ['PODS'])['items']
ready=lambda p: any(c.get('type')=='Ready' and c.get('status')=='True' for c in p.get('status',{}).get('conditions',[]))
active=[p for p in items if ready(p) and p.get('spec',{}).get('nodeName')]
nodes=[p['spec']['nodeName'] for p in active]
if len(active)!=3 or len(set(nodes))!=3:
    sys.exit(f'ERROR: {role} has {len(active)} ready pods on {len(set(nodes))} physical nodes, require exactly 3 distinct nodes. Placement: {nodes}')
print(f'{role}: three ready pods on distinct nodes: {nodes}')
PY
done
kubectl -n "$NAMESPACE" exec "$PREFIX-yb-master-0" -- \
  /home/yugabyte/bin/yb-admin --master_addresses \
  "$PREFIX-yb-master-0.$PREFIX-yb-masters.$NAMESPACE.svc.cluster.local:7100,$PREFIX-yb-master-1.$PREFIX-yb-masters.$NAMESPACE.svc.cluster.local:7100,$PREFIX-yb-master-2.$PREFIX-yb-masters.$NAMESPACE.svc.cluster.local:7100" \
  get_universe_config > /tmp/taskforge-yb-config-$$.txt
trap 'rm -f /tmp/taskforge-yb-config-$$.txt' EXIT
if ! grep -Eq 'num_replicas: 3|replication_factor: 3' /tmp/taskforge-yb-config-$$.txt; then
  echo 'ERROR: cannot prove RF=3 from Yugabyte universe config. Raw config:' >&2
  cat /tmp/taskforge-yb-config-$$.txt >&2
  exit 1
fi
count="$(kubectl -n "$NAMESPACE" exec "$PREFIX-yb-tserver-0" -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -Atqc 'SELECT COUNT(*) FROM yb_servers()')"
[[ "$count" == '3' ]] || { echo "ERROR: YSQL sees $count tablet servers, expected 3" >&2; exit 1; }
echo 'PASS: RF3 configured; 3 masters/3 tservers on 3 separate nodes; YSQL sees 3 data nodes.'
echo 'This verifies metadata/topology, NOT a destructive node-failure test; do that in an isolated lab.'
