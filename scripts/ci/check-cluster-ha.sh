#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

python3 -m py_compile deploy/cluster/clusterctl.py deploy/cluster/agent.py deploy/cluster/manager.py scripts/ci/test-cluster-runtime.py scripts/ci/test-easy-cluster.py
python3 scripts/ci/test-cluster-runtime.py
python3 scripts/ci/test-easy-cluster.py
for f in deploy/cluster/*.sh; do bash -n "$f"; done
bash -n deploy/prod/compose.sh
bash -n deploy/prod/deploy.sh
bash -n deploy/prod/bootstrap.sh
bash -n deploy/prod/cluster.sh
bash -n deploy/cluster/easy.sh
bash -n scripts/prod/prepare-env.sh
bash -n scripts/prod/check-prod-config.sh

secret_env="$(mktemp)"
trap 'rm -f "$secret_env"' EXIT
printf 'TASKFORGE_INTERNAL_KEY=%s\n' "$(printf 'i%.0s' {1..64})" > "$secret_env"
actual_secret="$(TASKFORGE_ENV_FILE="$secret_env" bash -c 'source deploy/cluster/common.sh; cluster_derived_secret taskforge-patroni-rest-v2')"
expected_secret="$(python3 - <<'PY_SECRET_TEST'
import hashlib,hmac
key='i'*64
print(hmac.new(key.encode(), b'taskforge-patroni-rest-v2', hashlib.sha512).hexdigest()[:64])
PY_SECRET_TEST
)"
[ "$actual_secret" = "$expected_secret" ] || { echo 'cluster shell/Python secret derivation mismatch' >&2; exit 1; }
rm -f "$secret_env"
trap - EXIT

python3 deploy/cluster/clusterctl.py --config deploy/cluster/cluster.example.json validate --allow-placeholders
python3 deploy/cluster/manager.py --inventory deploy/cluster/inventory.example.json validate --allow-placeholders

python3 - <<'PY'
from pathlib import Path
import json
import yaml

cluster_compose=Path('deploy/cluster/compose.cluster.yaml').read_text()
yaml.safe_load(cluster_compose)
assert '/postgres-ha:' in cluster_compose
assert 'postgres-cluster-data' in cluster_compose
assert 'profiles:' in cluster_compose and 'dcs-voter' in cluster_compose
assert 'TASKFORGE_HA_DYNAMIC_READY: "true"' in cluster_compose

cfg=json.loads(Path('deploy/cluster/cluster.example.json').read_text())
assert len(cfg['nodes']) >= 3
voters=[n for n in cfg['nodes'] if n.get('dcs_voter')]
assert len(voters) >= 3 and len(voters) % 2 == 1
assert cfg['nodes'][2]['web']['https_port'] == 8443
assert cfg['nodes'][2]['postgres']['host_port'] != cfg['nodes'][0]['postgres']['host_port']
assert cfg['etcd_image'] == 'gcr.io/etcd-development/etcd:v3.6.14'
assert 'minio_replication_drain_timeout_seconds' not in cfg

patroni=Path('infrastructure/postgres-ha/Dockerfile').read_text()
assert 'PATRONI_VERSION=4.1.5' in patroni
assert 'patroni[etcd3]' in patroni

env=Path('deploy/prod/.env.example').read_text()
for forbidden in ['HA_SHARED_KEY=', 'HA_REPLICATION_PASSWORD=', 'HA_PEER_SSH_', 'HA_NODE_A_', 'HA_NODE_B_']:
    assert forbidden not in env, forbidden
assert 'deploy/cluster/cluster.json' in env

agent=Path('deploy/cluster/agent.py').read_text()
for token in ['/switchover', 'traffic-ready', 'maximum_lag_on_failback_bytes', 'last_app_reconcile', 'def reconcile_apps', 'critical_services_ready']:
    assert token in agent, token

assert 'minio_script =' not in agent, 'automatic failback must not depend on every optional MinIO node'
assert '/run/taskforge-ha' not in agent

assert not Path('deploy/ha').exists(), 'legacy fixed two-node HA directory must not return'
assert not Path('scripts/ci/check-ha-two-node.sh').exists(), 'legacy two-node HA CI helper must not return'
assert Path('deploy/cluster/cleanup-legacy-ha.sh').is_file(), 'legacy HA cleanup helper is required'
assert Path('deploy/cluster/apply-topology.sh').is_file(), 'topology reload helper is required'
apply_topology=Path('deploy/cluster/apply-topology.sh').read_text()
assert 'systemctl restart taskforge-cluster.service' in apply_topology

migrate=Path('deploy/cluster/migrate-primary.sh').read_text()
join=Path('deploy/cluster/join-node.sh').read_text()
rollback=Path('deploy/cluster/rollback-bootstrap.sh').read_text()
common=Path('deploy/cluster/common.sh').read_text()
import_secrets=Path('deploy/cluster/import-secrets.sh').read_text()
clusterctl=Path('deploy/cluster/clusterctl.py').read_text()
assert '"traffic_steering": "off"' in clusterctl
assert '"header": ({"Host": [domain]}' in clusterctl
assert '"host_header": domain or None' in clusterctl
assert 'CORE_READY_SERVICES' in agent and 'critical_services_ready()' in agent
assert 'cluster_compose down --remove-orphans' not in migrate
assert 'cluster_compose down --remove-orphans' not in join
assert 'bootstrap-rolled-back' in migrate and '--reset-cluster-data' in migrate
assert 'bootstrap-rolled-back' in rollback
assert 'cluster_project_owner' in common and 'cluster_fix_runtime_owner' in common
assert 'chown "$(cluster_project_owner)" "$TASKFORGE_ENV_FILE"' in import_secrets
assert 'chown_to_deployment_owner' in clusterctl
easy=Path('deploy/cluster/easy.sh').read_text()
manager=Path('deploy/cluster/manager.py').read_text()
compose=Path('deploy/prod/compose.sh').read_text()
storage=Path('deploy/prod/compose/00-storage.yaml').read_text()
bootstrap=Path('deploy/prod/bootstrap.sh').read_text()
ensure_host=Path('deploy/cluster/ensure-host.sh').read_text()
import_secrets=Path('deploy/cluster/import-secrets.sh').read_text()
export_secrets=Path('deploy/cluster/export-secrets.sh').read_text()
for token in ['cmd_adopt', 'cmd_join', 'cmd_sync_minio', 'cmd_promote', 'cmd_doctor', 'cmd_repair', 'cmd_quorum']:
    assert token in easy, token
for token in ['prepare-descriptor', 'render-quorum', 'render-local', 'replica', 'quorum']:
    assert token in manager, token
assert '.runtime/cluster/local.env' in compose and '.runtime/cluster/cpu.env' in compose
assert '${MINIO_IMAGE:-minio/minio:' in storage
assert 'usermod -aG docker' in ensure_host
assert 'socat' in ensure_host and 'wireguard-tools' in ensure_host
assert '--ensure' in bootstrap
assert 'chown -R "$(cluster_project_owner)" "$TASKFORGE_ROOT/.runtime"' in import_secrets
assert 'chown "$(cluster_project_owner)" "$out"' in export_secrets
print('TaskForge N-node cluster invariants OK')
PY
