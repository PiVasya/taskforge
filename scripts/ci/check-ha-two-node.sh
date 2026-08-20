#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

python3 -m py_compile deploy/ha/agent.py
python3 scripts/ci/test-ha-agent-state.py
for f in deploy/ha/*.sh; do bash -n "$f"; done
bash -n deploy/prod/compose.sh
bash -n deploy/prod/deploy.sh
bash -n scripts/prod/prepare-env.sh
bash -n scripts/prod/check-prod-config.sh

python3 - <<'PY'
from pathlib import Path
import yaml
storage=Path('deploy/prod/compose/00-storage.yaml').read_text()
assert 'wal_level=replica' in storage
assert 'synchronous_commit=on' in storage
assert 'synchronous_standby_names=' in storage
assert '${POSTGRES_RESTART_POLICY:-unless-stopped}' in storage
assert '${POSTGRES_BIND:-127.0.0.1}' in storage
assert '${MINIO_BIND:-127.0.0.1}' in storage
yaml.safe_load(storage)

env=Path('deploy/prod/.env.example').read_text()
for token in [
    'HA_ENABLED=false','HA_AUTO_FAILOVER=false','HA_AUTO_FAILBACK=true',
    'HA_ALLOW_UNFENCED_FAILOVER=false','HA_REJOIN_AFTER_SECONDS=120',
    'HA_MINIO_DRAIN_TIMEOUT_SECONDS=300',
    'POSTGRES_RESTART_POLICY=unless-stopped',
]:
    assert token in env, token

agent=Path('deploy/ha/agent.py').read_text()
for token in [
    'failover-blocked-no-fence','split-brain-detected','operator-recovery-required',
    'pg_basebackup','standby-streaming-lost','automatic-failback','apply-update',
    'HA_ALLOW_UNFENCED_FAILOVER','HA_RECOVER_SCRIPT','peer-recovery-requested',
    'wait_minio_replication','yield-aborted-restored',
]:
    assert token in agent, token
assert 'preferred-node-' not in agent, 'split brain must not be auto-resolved by preference'

minio=Path('deploy/ha/setup-minio-replication.sh').read_text()
assert minio.count('replicate add') == 2
assert 'mirror --overwrite' in minio

drain=Path('deploy/ha/minio-wait-replication.sh').read_text()
assert 'replicate backlog' in drain
assert 'replicate ls --status enabled' in drain
assert 'HA_MINIO_DRAIN_TIMEOUT_SECONDS' in drain
assert Path('deploy/ha/recover-provider.example.sh').is_file()

nginx=Path('apps/gateway/99-select-config.sh').read_text()
assert '/run/taskforge-ha/traffic-ready' in nginx
assert 'TASKFORGE_HA_DYNAMIC_READY' in nginx
print('TaskForge two-node HA invariants OK')
PY
