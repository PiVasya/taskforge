#!/usr/bin/env python3
"""Validate rendered isolated K3s manifests, not a removed develop Compose file."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
LAB_NAMESPACE = 'taskforge-k3s-lab'
DB_NAMESPACE = 'taskforge-k3s-lab-db'
LAB_REGISTRY = 'ghcr.io/pivasya/taskforge-k3s-lab/'


def require(ok: bool, issue: str) -> None:
    if not ok:
        raise AssertionError(issue)


def inspect_profile(profile: str) -> None:
    with tempfile.TemporaryDirectory(prefix='taskforge-k3s-release-') as temp:
        target = Path(temp) / profile
        subprocess.run(['node', 'tools/k3s/local/generate.mjs', profile, str(target)],
                       cwd=ROOT, check=True, capture_output=True, text=True)
        resources = []
        for file in sorted(target.glob('*.json.yaml')):
            resources.extend(json.loads(segment) for segment in
                             file.read_text(encoding='utf-8').strip().split('\n---\n'))
        require(resources, f'{profile}: missing rendered resources')
        names = {(item['kind'], item['metadata']['name']) for item in resources}
        require(len(names) == len(resources), f'{profile}: duplicate resource kind/name')
        for item in resources:
            kind = item['kind']
            name = item['metadata']['name']
            metadata = item['metadata']
            if kind == 'Namespace':
                require(name == LAB_NAMESPACE, f'{profile}: unexpected namespace {name}')
                continue
            require(metadata.get('namespace') == LAB_NAMESPACE,
                    f'{profile}: {name} escapes isolated namespace')
            if kind == 'Service':
                require(item['spec'].get('type', 'ClusterIP') == 'ClusterIP',
                        f'{profile}: public service {name}')
                require(not item['spec'].get('externalIPs'), f'{profile}: external service IP {name}')
            if kind == 'Deployment':
                require(item['spec']['replicas'] == 1, f'{profile}: unexpected replication {name}')
            if kind in ('Job', 'Deployment'):
                pod = item['spec']['template']['spec']
                require(not pod.get('hostNetwork') and not pod.get('hostPID') and
                        not pod.get('hostIPC'), f'{profile}: host namespace exposure {name}')
                require(not pod.get('hostPath'), f'{profile}: hostPath on {name}')
                for volume in pod.get('volumes', []):
                    require('hostPath' not in volume, f'{profile}: host-mounted volume {name}')
                for c in pod.get('containers', []):
                    require(not c.get('securityContext', {}).get('privileged', False),
                            f'{profile}: privileged container {name}')
                    require(not c['image'].endswith(':develop') and
                            not c['image'].endswith(':latest'), f'{profile}: shared image tag {name}')
                    if name.endswith('-api') or name.startswith('migrate-') or name in ('front', 'rating-worker'):
                        require(c['image'].startswith(LAB_REGISTRY),
                                f'{profile}: production image in {name}')
        policy = next((r for r in resources if r['kind'] == 'NetworkPolicy'), None)
        require(policy is not None and policy['spec']['policyTypes'] == ['Egress'],
                f'{profile}: no egress isolation policy')
        rules = policy['spec']['egress']
        require(len(rules) == 3, f'{profile}: unexpected egress rules')
        require(rules[0]['to'] == [{'podSelector': {}}],
                f'{profile}: egress not restricted to own pods')
        require(rules[1]['to'] == [{'namespaceSelector':
                {'matchLabels': {'kubernetes.io/metadata.name': DB_NAMESPACE}}}],
                f'{profile}: unexpected YSQL destination')
        require(rules[1]['ports'] == [{'protocol': 'TCP', 'port': 5433}],
                f'{profile}: incorrect YSQL port')
        require(rules[2]['to'] == [{'namespaceSelector':
                {'matchLabels': {'kubernetes.io/metadata.name': 'kube-system'}}}],
                f'{profile}: unexpected DNS destination')
        require(rules[2]['ports'] == [{'protocol': 'UDP', 'port': 53},
                                     {'protocol': 'TCP', 'port': 53}],
                f'{profile}: incorrect DNS ports')
        config = next((r['data'] for r in resources
                       if r['kind'] == 'ConfigMap' and
                       r['metadata']['name'] == 'taskforge-app-config'), None)
        require(config is not None, f'{profile}: missing API configuration')
        for field, expected in {
            'TASKFORGE_DEBUG_LOGS': '1',
            'Cluster__Namespace': LAB_NAMESPACE,
            'Cluster__MultiNode': 'true',
            'Database__RequireYugabyte': 'true',
            'Database__MigrateOnStartup': 'false',
            'Database__EnsureCreated': 'false',
            'S3__Bucket': 'taskforge-k3s-lab-files',
        }.items():
            require(config.get(field) == expected,
                    f'{profile}: unsafe/missing {field}: {config.get(field)!r}')
        gateway = next((r['data']['site.conf'] for r in resources
                        if r['kind'] == 'ConfigMap' and
                        r['metadata']['name'] == 'local-gateway-config'), None)
        require(gateway is not None and
                'location /api/ { return 404; }' in gateway and
                '/api/internal/' not in gateway,
                f'{profile}: gateway may expose internal owner endpoints')
        print(f'PASS: {profile} isolated K3s release boundaries ({len(resources)} resources)')


def check() -> None:
    root_markdown = {p.name for p in ROOT.iterdir()
                     if p.is_file() and p.suffix.lower() in ('.md', '.markdown')}
    require(root_markdown == {'00_AI_READ_THIS_FIRST.md'},
            f'Root documentation violates project contract: {root_markdown}')
    require((ROOT / 'docs/operations/README_K3S_BRANCH.md').is_file(),
            'Missing isolated branch installation guide')
    require('http2 on;' not in (ROOT / 'apps/gateway/templates/https.conf').read_text(),
            'Gateway TLS template re-enables HTTP/2 against current mitigation')
    for profile in ('core', 'all-api'):
        inspect_profile(profile)


if __name__ == '__main__':
    try:
        check()
    except (AssertionError, OSError, subprocess.CalledProcessError, KeyError, ValueError) as exc:
        print('FAIL: K3s release boundaries:', exc, file=sys.stderr)
        sys.exit(1)
