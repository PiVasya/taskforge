#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
# Installs a real single-node YugabyteDB into the current K3s context. Explicit
# acknowledgment required to prevent accident on the live multi-node universe.
if [[ "${TASKFORGE_LOCAL_K3S_ACK:-}" != "local-development-only" ]]; then
  echo "ERROR: export TASKFORGE_LOCAL_K3S_ACK=local-development-only" >&2
  exit 2
fi
for cmd in kubectl helm python3; do command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 2; }; done
THIS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NAMESPACE="taskforge-k3s-lab-db"
[[ "$NAMESPACE" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ ]] || { echo 'Invalid namespace' >&2; exit 2; }
# Fail closed when connected to a multi-node or unexpected cluster.
node_json="$(kubectl get nodes -o json)"
NODE_JSON="$node_json" python3 - <<'PY'
import json,os,sys
items=json.loads(os.environ['NODE_JSON'])['items']
ready=[x for x in items if not x.get('spec',{}).get('unschedulable') and
       any(c.get('type')=='Ready' and c.get('status')=='True' for c in x.get('status',{}).get('conditions',[]))]
if len(items)!=1 or len(ready)!=1:
    sys.exit('ERROR: this lab installs only on exactly ONE Ready K3s node. Detected '+str(len(items))+' nodes')
print('Single node:',ready[0]['metadata']['name'])
PY
# Any existing release from Stage 5 is left untouched; distinct name/namespace.
kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl label namespace "$NAMESPACE" 'taskforge.io/use=k3s-isolated-db' --overwrite >/dev/null
helm repo add yugabytedb https://charts.yugabyte.com --force-update
helm repo update yugabytedb
helm upgrade --install tf-k3s-lab-yb yugabytedb/yugabyte \
  --version "${YB_CHART_VERSION:-2026.1.2}" --namespace "$NAMESPACE" --create-namespace \
  --values "$THIS/helm-values-single.yaml" --wait --timeout 20m
"$THIS/verify-db.sh"
echo 'YugabyteDB RF1 running inside the actual local K3s cluster.'
