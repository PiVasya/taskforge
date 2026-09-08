#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
fail(){ echo "FAIL: $*" >&2; exit 1; }
pass(){ echo "PASS: $*"; }

bash -n apps/gateway/99-select-config.sh
bash -n deploy/prod/deploy.sh
bash -n deploy/prod/compose.sh
pass 'gateway/deploy/compose runtime scripts parse'

grep -Fq 'BROWSER_API_UPSTREAM' apps/gateway/99-select-config.sh || fail 'browser upstream runtime variable missing'
grep -Fq 'http://${BROWSER_API_UPSTREAM}' apps/gateway/snippets/api-routes.conf || fail 'browser routes are not runtime-routable'
grep -Fq '^/api/admin/(activity|system-status|cluster|analytics)' apps/gateway/snippets/api-routes.conf || fail 'admin cluster endpoint is not routed to observability'
pass 'lite browser delegation is wired'

grep -Fq 'ServiceUrl(cfg, "ImageAnalyzer", "http://image-analyzer:8080")' services/tasks/assignment-api/Services/Image/AssignmentApiImageService.cs || fail 'tasks image analyzer runtime routing missing'
grep -Fq 'ServiceUrl(cfg, "ImageAnalyzer", "http://image-analyzer:8080")' services/tasks/assignment-api/Services/Image/AssignmentApiImageService.Storage.cs || fail 'tasks storage image analyzer runtime routing missing'
pass 'lite image-analyzer delegation is wired'

grep -Fq 'ClusterTelemetryService' services/observability/api/Program.cs || fail 'cluster telemetry service not registered'
grep -Fq 'app.MapGet("/api/admin/system-status"' services/observability/api/Endpoints/SystemStatus/SystemStatusEndpoints.cs || fail 'admin cluster status endpoint missing'
grep -Fq 'SeedExpectedAgents' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'offline expected-node seeding missing'
grep -Fq 'quorumTotal' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'quorum total is not exposed'
grep -Fq 'ReadTopologyMeta' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'offline node topology metadata missing'
grep -Fq 'MaxSeenEventIds = 20_000' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'cluster event de-duplication cache is unbounded'
grep -Fq 'MarkEventSeen' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'bounded event de-duplication path missing'
grep -Fq 'BuildPublicUpdate' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'public updater projection missing'
grep -Fq 'hotStartReady' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'standby hot readiness missing from aggregation'
grep -Fq 'watchtowerRunning' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'Watchtower health missing from aggregation'
! grep -Fq '["update"] = SanitizeNode' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'raw updater payload is exposed to browser'
grep -Fq '_agents.TryRemove("url:" + baseUrl' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'transient remote-agent placeholder cleanup missing'
grep -Fq 'it will be retried' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'cluster notification retry missing'
grep -Fq 'OperationCanceledException' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'node/support-bot timeout handling missing'
grep -Fq 'cluster.node_down' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'node-down event synthesis missing'
grep -Fq 'cluster.node_recovered' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'node-recovered event synthesis missing'
grep -Fq 'bundleVersion' services/observability/api/Services/Cluster/ClusterTelemetryService.cs || fail 'server bundle version projection missing'
pass 'observability aggregation, bounded events and safe updater projection are wired'

grep -Fq '/api/internal/cluster/events' services/bots/support-bot/Program.cs || fail 'support-bot cluster event endpoint missing'
grep -Fq '_deliveredClusterEvents' services/bots/support-bot/Worker.cs || fail 'support-bot event de-duplication missing'
pass 'support-bot cluster notifications are wired'

# The dashboard is modular: validate features where they now live rather than
# pinning a page title or requiring all JSX in one historical file.
page=apps/web/src/pages/admin/AdminSystemStatusPage.jsx
features=apps/web/src/features/cluster
grep -Fq 'ClusterMap' "$page" || fail 'cluster admin map is missing'
grep -Fq 'ImageMatrix' "$page" || fail 'human image comparison is missing'
grep -Fq 'hotStartReady' "$features/ClusterMap.jsx" || fail 'hot-start readiness badge missing'
grep -Fq 'quorumTotal' "$page" || fail 'voter availability ratio UI missing'
grep -Fq 'Watchtower' "$features/ClusterNodeDetails.jsx" || fail 'updater status is not visible'
grep -Fq 'bundleRevision' "$features/ClusterNodeDetails.jsx" || fail 'server revision is not visible'
grep -Fq 'pg.replicas' "$features/ClusterNodeDetails.jsx" || fail 'primary replica detail is missing'
grep -Fq 'bytes(r.lag_bytes)' "$features/ClusterNodeDetails.jsx" || fail 'replication lag detail missing'
grep -Fq 'path="/admin/cluster"' apps/web/src/App.jsx || fail 'canonical cluster route missing'
grep -Fq 'Navigate to="/admin/cluster"' apps/web/src/App.jsx || fail 'legacy cluster route redirect missing'
grep -Fq "api.get('/api/admin/cluster'," apps/web/src/api/systemStatus.js || fail 'admin page calls the wrong endpoint'
! grep -Eq 'sha256:|image_fingerprint|imageFingerprint|digest' "$page" "$features/ClusterTables.jsx" || fail 'technical image identifier leaked into UI'
node --test apps/web/scripts/cluster-tests/*.test.mjs
pass 'modular cluster dashboard, image states, layout and metric formatting are checked'

[ "$(cat deploy/prod/VERSION)" = 40 ] || fail 'embedded production bundle is not v40'
[ "$(cat deploy/prod/REVISION)" = 2 ] || fail 'embedded production bundle is not v40 revision 2'
grep -Fq 'hot_start_ready' deploy/cluster/agent.py || fail 'embedded Node Agent is stale'
grep -Fq 'container_fingerprint' deploy/cluster/agent.py || fail 'embedded image comparison still follows mutable tags'
grep -Fq 'reconcile_watchtower(True)' deploy/cluster/agent.py || fail 'standby Watchtower is not continuously enabled'
grep -Fq 'WATCHTOWER_INCLUDE_STOPPED: ${WATCHTOWER_INCLUDE_STOPPED:-true}' deploy/prod/compose/80-watchtower.yaml || fail 'embedded Watchtower does not update stopped containers'
grep -Fq 'WATCHTOWER_REVIVE_STOPPED: ${WATCHTOWER_REVIVE_STOPPED:-false}' deploy/prod/compose/80-watchtower.yaml || fail 'embedded Watchtower may revive standby containers'
grep -Fq "profile == 'lite'" deploy/prod/compose.sh || fail 'embedded profile-aware pull missing lite exclusions'
grep -Fq 'TASKFORGE_LAYOUT_ROOT' deploy/prod/compose.sh || fail 'embedded compose wrapper cannot run from source-tree layout'
grep -Fq 'TASKFORGE_POSTGRES_INIT_DIR' deploy/prod/compose/00-storage.yaml || fail 'embedded PostgreSQL init mount still uses fragile relative path'
grep -Fq 'Replica standby deploy complete; application containers remain prepared/stopped.' deploy/prod/deploy.sh || fail 'source production deploy can still start a replica standby app stack'
! grep -Fq 'deploy/cluster/status.sh' deploy/prod/deploy.sh || fail 'source production deploy still references removed legacy cluster status helper'
pass 'source archive contains matching v40-r2 warm-standby deployment bundle'

python3 - <<'PY_NO_DUP_DICTS'
import ast, pathlib
for path in pathlib.Path('deploy/cluster').rglob('*.py'):
    tree=ast.parse(path.read_text(encoding='utf-8'), filename=str(path))
    for node in ast.walk(tree):
        if not isinstance(node,ast.Dict):
            continue
        seen={}
        for key in node.keys:
            if isinstance(key,ast.Constant) and isinstance(key.value,(str,int,float,bool,type(None))):
                value=key.value
                if value in seen:
                    raise SystemExit(f'duplicate Python dict key {value!r} in {path}:{getattr(key,"lineno","?")}')
                seen[value]=getattr(key,'lineno','?')
PY_NO_DUP_DICTS
pass 'embedded cluster Python contains no duplicate dictionary keys'

grep -Fq '"nofailover": not node.can_be_primary' deploy/cluster/clusterctl.py || fail 'C witness nofailover protection missing'
patroni_block="$(sed -n '/patroni = {/,/patroni_path =/p' deploy/cluster/clusterctl.py)"
[ "$(grep -c '^        "tags": {' <<<"$patroni_block")" -eq 1 ] || fail 'embedded Patroni config contains duplicate tags mappings'
pass 'C cannot become PostgreSQL primary and Patroni tags are not overwritten'

echo 'All TaskForge v40-r2 cluster-control source checks passed.'
