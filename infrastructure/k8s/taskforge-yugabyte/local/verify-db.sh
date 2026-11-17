#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
for cmd in kubectl python3; do command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 2; }; done
NAMESPACE="taskforge-k3s-lab-db"
RELEASE=tf-k3s-lab-yb
for COMPONENT in yb-master yb-tserver; do
  pods="$(kubectl -n "$NAMESPACE" get pods -l "app.kubernetes.io/name=$COMPONENT,release=$RELEASE" -o json)"
  PODS="$pods" ROLE="$COMPONENT" python3 - <<'PY'
import json,os,sys
p=json.loads(os.environ['PODS'])['items']
ready=[x for x in p if x.get('spec',{}).get('nodeName') and any(c.get('type')=='Ready' and c.get('status')=='True' for c in x.get('status',{}).get('conditions',[]))]
if len(p)!=1 or len(ready)!=1:sys.exit('ERROR: '+os.environ['ROLE']+' must have ONE Ready pod, got '+str(len(ready)))
print(os.environ['ROLE']+': Ready on '+ready[0]['spec']['nodeName'])
PY
done
T=taskforge-k3s-lab-yb-yb-tserver-0
M=taskforge-k3s-lab-yb-yb-master-0
MASTER="${M}.taskforge-k3s-lab-yb-yb-masters.${NAMESPACE}.svc.cluster.local:7100"
config="$(kubectl -n "$NAMESPACE" exec "$M" -- /home/yugabyte/bin/yb-admin --master_addresses "$MASTER" get_universe_config)"
if ! grep -Eq 'num_replicas: 1|replication_factor: 1' <<<"$config"; then
  echo 'ERROR: failed to establish RF=1' >&2; echo "$config" >&2; exit 1
fi
count="$(kubectl -n "$NAMESPACE" exec "$T" -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -Atqc 'SELECT count(*) FROM yb_servers()')"
[[ "$count" == "1" ]] || { echo "ERROR: expected 1 YSQL data node, got $count" >&2; exit 1; }
version="$(kubectl -n "$NAMESPACE" exec "$T" -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -Atqc 'SELECT version()')"
[[ "$version" == *-YB-* ]] || { echo 'ERROR: connected database does not identify as YugabyteDB YSQL' >&2; exit 1; }
echo 'PASS: K3s has one RF1 YugabyteDB universe, actual YSQL query works.'
