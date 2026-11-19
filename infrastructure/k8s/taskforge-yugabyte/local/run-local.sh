#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
# Usage: TASKFORGE_LOCAL_K3S_ACK=local-development-only bash run-local.sh /path/to/patched/taskforge [core|all-api]
SOURCE="${1:-}"
PROFILE="${2:-core}"
[[ "${TASKFORGE_LOCAL_K3S_ACK:-}" == local-development-only ]] || { echo 'export TASKFORGE_LOCAL_K3S_ACK=local-development-only' >&2; exit 2; }
[[ -n "$SOURCE" && -f "$SOURCE/00_AI_READ_THIS_FIRST.md" ]] || { echo 'Point to a patched TaskForge source checkout' >&2; exit 2; }
[[ "$PROFILE" == core || "$PROFILE" == all-api ]] || { echo 'Choose core or all-api' >&2; exit 2; }
for cmd in kubectl node helm openssl; do command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 2; }; done
THIS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RENDER="$(mktemp -d)"
trap 'rm -rf "$RENDER"' EXIT
node "$SOURCE/tools/k3s/local/generate.mjs" "$PROFILE" "$RENDER"
# Never silently apply manifests from stale directories.
"$THIS/install-db.sh"
"$THIS/create-databases.sh"
"$THIS/smoke-write.sh"
"$THIS/init-secrets.sh"
kubectl apply -f "$RENDER/01-infra.json.yaml"
echo 'Waiting for Redis, RabbitMQ and MinIO...'
for dep in redis rabbitmq minio; do
  kubectl -n taskforge-k3s-lab rollout status "deployment/$dep" --timeout=240s || { kubectl -n taskforge-k3s-lab get pods; exit 1; }
done
# MinIO bucket used by TaskForge. Idempotent, without public credentials in YAML.
kubectl -n taskforge-k3s-lab apply -f "$THIS/minio-bucket-job.yaml"
kubectl -n taskforge-k3s-lab wait --for=condition=complete job/taskforge-k3s-lab-minio-bucket --timeout=180s || { kubectl -n taskforge-k3s-lab logs job/taskforge-k3s-lab-minio-bucket; exit 1; }
echo 'Applying one-shot EF migrations (fresh local YSQL database only).'
# Job pod templates are immutable. Recreate only COMPLETED sandbox migration Jobs
# on re-deploy so a new local image runs its latest EF migrations.
for db in identity education content tasks solutions execution ai support minecraft files notifications observability; do
  if [[ "$PROFILE" == core ]]; then
    case "$db" in identity|education|content|tasks|solutions|execution) ;; *) continue ;; esac
  fi
  job="migrate-$db-api-lab1"
  if kubectl -n taskforge-k3s-lab get job "$job" >/dev/null 2>&1; then
    active="$(kubectl -n taskforge-k3s-lab get job "$job" -o jsonpath='{.status.active}')"
    [[ -z "$active" || "$active" == 0 ]] || { echo "Refusing to delete active migration Job $job" >&2; exit 3; }
    kubectl -n taskforge-k3s-lab delete job "$job" --wait=true
  fi
done
# Migrate ONE logical service database at a time. Running many concurrent EF
# DDL migrations against a tiny RF1 YSQL cluster can race on catalog metadata.
# The migration lock override is safe ONLY because each DB has one Job at a time.
for db in identity education content tasks solutions execution ai support minecraft files notifications observability; do
  if [[ "$PROFILE" == core ]]; then
    case "$db" in identity|education|content|tasks|solutions|execution) ;; *) continue ;; esac
  fi
  job="migrate-$db-api-lab1"
  echo "Applying migration Job: $job"
  python3 - "$RENDER/02-migrations.json.yaml" "$job" <<'PYDB' | kubectl -n taskforge-k3s-lab apply -f -
import json,sys
objects=[json.loads(x) for x in open(sys.argv[1],encoding='utf-8').read().strip().split('\n---\n')]
for obj in objects:
    if obj['kind']=='Job' and obj['metadata']['name']==sys.argv[2]:
        print(json.dumps(obj))
        break
else:
    sys.exit('No manifest for requested migration Job '+sys.argv[2])
PYDB
  kubectl -n taskforge-k3s-lab wait --for=condition=complete "job/$job" --timeout=600s || { kubectl -n taskforge-k3s-lab logs "job/$job" --tail=200 || true; exit 1; }
done
kubectl -n taskforge-k3s-lab apply -f "$RENDER/03-apis.json.yaml"
kubectl -n taskforge-k3s-lab apply -f "$RENDER/04-web.json.yaml"
# Images are imported into containerd with a local-only tag. A repeated apply
# does not change the PodSpec, so restart Deployments to use the rebuilt image.
for dep in identity-api education-api content-api tasks-api solutions-api execution-api front; do
  kubectl -n taskforge-k3s-lab rollout restart "deployment/$dep"
done
for dep in identity-api education-api content-api tasks-api solutions-api execution-api front local-gateway; do
  echo "Checking deployment/$dep"
  kubectl -n taskforge-k3s-lab rollout status "deployment/$dep" --timeout=300s || { kubectl -n taskforge-k3s-lab get pods; exit 1; }
done
echo
printf '%s\n' 'K3s starter deployed. In a new terminal run:' \
  '  kubectl -n taskforge-k3s-lab port-forward --address 127.0.0.1 svc/local-gateway 18080:8080' \
  'Open http://127.0.0.1:18080' \
  'DB check: kubectl -n taskforge-k3s-lab-db exec taskforge-k3s-lab-yb-yb-tserver-0 -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d taskforge_k3s_tasks -c '\''SELECT count(*) FROM "Assignments";'\''' \
  'This single-node RF1 environment is NOT production or fault-tolerant.'
