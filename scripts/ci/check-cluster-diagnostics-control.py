#!/usr/bin/env python3
"""Structural contract for admin-triggered cluster diagnostics wiring."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
checks = {
    "observability control": (ROOT / "services/observability/api/Services/Cluster/ClusterDiagnosticsControl.cs", [
        'StartDiagnosticsAsync', '/ha/diagnostics', 'X-TaskForge-Cluster-Token',
        'taskforge-cluster-agent-diagnostics-v1', 'UnixDomainSocketEndPoint', 'DIAGNOSTICS_AGENT_TOO_OLD', '[TFDIAG API START]', '[TFDIAG API DOWNLOAD]',
        'RunDiagnosticsArchiveLoopAsync', '[TFDIAG MINIO STORED]' 
    ]),
    "MinIO diagnostics archive store": (ROOT / "services/observability/api/Services/Cluster/ClusterDiagnosticsArchiveStore.cs", [
        'ClusterDiagnosticsStorage:RetentionDays', 'ClusterDiagnosticsStorage:Prefix',
        'PutObjectAsync', 'Headers["Content-Length"]', 'AutoResetStreamPosition = false',
        'ListObjectsV2Async', 'CleanupExpiredAsync', 'WriteDownloadAsync'
    ]),
    "local telemetry hairpin avoidance": (ROOT / "services/observability/api/Services/Cluster/ClusterTelemetryService.cs", [
        'ResolveLocalAgentUrl', 'payload["topology"]', 'local["wireguard"]?["ip"]',
        'AgentUrls.Where(url => !IsLocalAgentUrl(url))', '_agents.TryRemove("url:" + _localAgentUrl'
    ]),
    "admin endpoints": (ROOT / "services/observability/api/Endpoints/SystemStatus/SystemStatusEndpoints.cs", [
        '/api/admin/cluster/diagnostics', '/api/admin/cluster/diagnostics/archives', '/download', 'ProxyDiagnosticsArchiveAsync', 'ProxyStoredDiagnosticsArchiveAsync'
    ]),
    "browser client": (ROOT / "apps/web/src/api/systemStatus.js", [
        'startClusterDiagnostics', 'getClusterDiagnosticsJob', 'downloadClusterDiagnostics', 'getClusterDiagnosticsArchives', 'downloadStoredClusterDiagnostics', "responseType: 'blob'"
    ]),
    "cluster dialog": (ROOT / "apps/web/src/features/cluster/ClusterDiagnosticsDialog.jsx", [
        'Собрать логи', "id: 'quick'", "id: 'standard'", "id: 'full'", 'diagnosticsEligibility', 'maxLogMb', 'since', 'Сохранённые архивы', 'getClusterDiagnosticsArchives'
    ]),
    "cluster page": (ROOT / "apps/web/src/pages/admin/AdminSystemStatusPage.jsx", [
        'ClusterDiagnosticsDialog', 'setDiagnosticsOpen(true)', 'Собрать логи'
    ]),
    "prod local socket wiring": (ROOT / "deploy/prod/compose/50-integrations.yaml", [
        'ClusterTelemetry__LocalAgentSocketPath: /run/taskforge-cluster-host/node-agent.sock',
        'ClusterDiagnosticsStorage__RetentionDays: ${CLUSTER_DIAGNOSTICS_RETENTION_DAYS:-7}',
        '/.runtime/cluster:/run/taskforge-cluster-host:ro'
    ]),
    "gateway streaming": (ROOT / "apps/gateway/snippets/api-routes.conf", [
        '^/api/admin/cluster/diagnostics/[^/]+/[^/]+/download$', 'proxy_buffering off;', 'proxy_read_timeout 30m;'
    ]),
    "debug streaming safety": (ROOT / "services/observability/api/Diagnostics/TaskForgeDebugDiagnostics.cs", [
        '/api/admin/cluster/diagnostics/', 'EndsWith("/download"', 'media.Contains("gzip"'
    ]),
}

for label, (path, needles) in checks.items():
    if not path.is_file():
        raise SystemExit(f"CLUSTER DIAGNOSTICS CONTROL: FAIL: missing {label}: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8", errors="replace")
    missing = [needle for needle in needles if needle not in text]
    if missing:
        raise SystemExit(f"CLUSTER DIAGNOSTICS CONTROL: FAIL: {label} missing {missing}")

print("CLUSTER DIAGNOSTICS CONTROL: PASS (admin API, MinIO retention/listing, UI downloads, streaming and debug-log safety are wired)")
