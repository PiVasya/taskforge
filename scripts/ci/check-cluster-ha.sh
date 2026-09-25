#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

PYTHONPYCACHEPREFIX="$(mktemp -d)" python3 -m py_compile \
  deploy/cluster/clusterctl.py deploy/cluster/agent.py deploy/cluster/manager.py \
  scripts/ci/test-cluster-runtime.py scripts/ci/test-easy-cluster.py scripts/ci/test-ha-agent-state.py
rm -rf "${PYTHONPYCACHEPREFIX:-}" 2>/dev/null || true

python3 scripts/ci/test-cluster-runtime.py
python3 scripts/ci/test-easy-cluster.py
python3 scripts/ci/test-ha-agent-state.py
bash scripts/check-cluster-control-v40.sh

find deploy/cluster -type f -name '*.sh' -print0 | while IFS= read -r -d '' f; do bash -n "$f"; done
bash -n deploy/prod/compose.sh
bash -n deploy/prod/deploy.sh
bash -n deploy/prod/bootstrap.sh
bash -n deploy/prod/cluster.sh

python3 deploy/cluster/clusterctl.py --config deploy/cluster/cluster.example.json validate --allow-placeholders
python3 deploy/cluster/manager.py --inventory deploy/cluster/inventory.example.json validate --allow-placeholders

python3 - <<'PY'
from pathlib import Path
import ast, json, yaml

yaml.safe_load(Path('deploy/cluster/compose.cluster.yaml').read_text())
for path in Path('deploy/cluster').rglob('*.py'):
    tree=ast.parse(path.read_text(encoding='utf-8'), filename=str(path))
    for node in ast.walk(tree):
        if not isinstance(node,ast.Dict): continue
        seen=set()
        for key in node.keys:
            if isinstance(key,ast.Constant) and isinstance(key.value,(str,int,float,bool,type(None))):
                assert key.value not in seen, f'duplicate dict key {key.value!r} in {path}:{key.lineno}'
                seen.add(key.value)

cfg=json.loads(Path('deploy/cluster/cluster.example.json').read_text())
nodes={n['id']:n for n in cfg['nodes']}
assert [n['id'] for n in cfg['nodes'] if n.get('dcs_voter')] == ['A','B','C']
assert nodes['A']['app']['profile']=='full' and nodes['A']['app']['can_be_primary'] is True
assert nodes['B']['app']['profile']=='full' and nodes['B']['app']['can_be_primary'] is True
assert nodes['C']['app']['profile']=='lite' and nodes['C']['app']['can_be_primary'] is False
assert {'browser-api','image-analyzer'} <= set(nodes['C']['app']['exclude_services'])

compose=Path('deploy/prod/compose.sh').read_text()
assert 'TASKFORGE_LAYOUT_ROOT' in compose
assert 'profile_pull_services' in compose and "profile == 'lite'" in compose
assert 'TASKFORGE_POSTGRES_INIT_DIR' in compose
storage=Path('deploy/prod/compose/00-storage.yaml').read_text()
assert 'TASKFORGE_POSTGRES_INIT_DIR' in storage
watch=Path('deploy/prod/compose/80-watchtower.yaml').read_text()
assert 'WATCHTOWER_INCLUDE_STOPPED' in watch and 'WATCHTOWER_REVIVE_STOPPED' in watch
agent=Path('deploy/cluster/agent.py').read_text()
for token in ['reconcile_watchtower(True)','reconcile_watchtower(False)','hot_start_ready','--pull", "never"','unassigned_application_containers']:
    assert token in agent, token
assert 'with self.lock:\n            return self.refresh_telemetry(force=False)' not in agent
observability=Path('services/observability/api/Services/Cluster/ClusterTelemetryService.cs').read_text()
for token in ['baseUrl + "/ha/live"','TelemetryPollSeconds','DownAfterSeconds','LastLiveSuccessUtc']:
    assert token in observability, token
assert 'configuration.GetValue("ClusterTelemetry:DownAfterSeconds", 60)' in observability
integrations=Path('deploy/prod/compose/50-integrations.yaml').read_text()
for token in ['ClusterTelemetry__TelemetryPollSeconds','ClusterTelemetry__DownAfterSeconds','ClusterTelemetry__LiveTimeoutSeconds','ClusterTelemetry__TelemetryTimeoutSeconds']:
    assert token in integrations, token
assert 'Requires=docker.service' not in Path('deploy/cluster/ops/quorum/install-service.sh').read_text()
print('TaskForge v40 N-node HA invariants OK')
PY

echo 'TaskForge v40 HA checks OK'
