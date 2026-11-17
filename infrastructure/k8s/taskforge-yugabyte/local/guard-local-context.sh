#!/usr/bin/env bash
set -Eeuo pipefail
# Refuse to operate against any remote cluster, including existing production.
# A dedicated KUBECONFIG copy is mandatory: never overwrite ~/.kube/config.
[[ "${TASKFORGE_LOCAL_K3S_ACK:-}" == 'local-development-only' ]] || {
  echo 'BLOCKED: export TASKFORGE_LOCAL_K3S_ACK=local-development-only' >&2; exit 2;
}
[[ -n "${KUBECONFIG:-}" && -f "${KUBECONFIG:-/nonexistent}" ]] || {
  echo 'BLOCKED: set KUBECONFIG to a dedicated copy of /etc/rancher/k3s/k3s.yaml' >&2; exit 2;
}
command -v kubectl >/dev/null || { echo 'Missing kubectl' >&2; exit 2; }
command -v python3 >/dev/null || { echo 'Missing python3' >&2; exit 2; }
kubectl config view --minify -o json | python3 -c '
import json,sys,urllib.parse
j=json.load(sys.stdin)
cs=j.get("clusters",[])
if len(cs)!=1:sys.exit("BLOCKED: kubeconfig must select exactly one cluster")
s=cs[0]["cluster"].get("server","")
u=urllib.parse.urlparse(s)
if u.scheme!="https" or u.hostname not in ("127.0.0.1","::1","localhost"):
 sys.exit("BLOCKED: Kubernetes API endpoint is not loopback: "+str(u.hostname))
print("Local K3s API:",u.scheme+"://"+u.hostname+":"+str(u.port))
'
kubectl get nodes -o json | python3 -c '
import json,sys
items=json.load(sys.stdin).get("items",[])
ready=[n for n in items if not n.get("spec",{}).get("unschedulable") and any(x.get("type")=="Ready" and x.get("status")=="True" for x in n.get("status",{}).get("conditions",[]))]
if len(items)!=1 or len(ready)!=1: sys.exit("BLOCKED: sandbox requires exactly one Ready K3s node")
print("Local node:",ready[0]["metadata"]["name"])
'
for ns in taskforge-k3s-lab taskforge-k3s-lab-db; do
  if kubectl get namespace "$ns" >/dev/null 2>&1; then
    use="$(kubectl get namespace "$ns" -o jsonpath='{.metadata.labels.taskforge\.io/use}')"
    case "$ns:$use" in
      'taskforge-k3s-lab:k3s-isolated-lab'|'taskforge-k3s-lab-db:k3s-isolated-db') ;;
      *) echo "BLOCKED: namespace $ns exists but is not owned by this lab" >&2; exit 3 ;;
    esac
  fi
done
