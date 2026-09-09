#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_config
clusterctl validate >/dev/null
python3 - "$TASKFORGE_CLUSTER_CONFIG" <<'PY'
import json,sys,urllib.request,urllib.error
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
voters=[n for n in cfg['nodes'] if n.get('dcs_voter')]
healthy=[]
rows=[]
for n in voters:
    url=f"http://{n['wireguard']['ip']}:{n['etcd']['client_port']}/health"
    status=0; body=''
    try:
        with urllib.request.urlopen(url,timeout=2.5) as r:
            status=r.status; body=r.read().decode('utf-8','replace')[:300]
    except urllib.error.HTTPError as e:
        status=e.code; body=e.read().decode('utf-8','replace')[:300]
    except Exception as e:
        body=f'{type(e).__name__}: {e}'
    ok=status==200 and ('true' in body.lower() or 'health' in body.lower())
    if ok: healthy.append(n['id'])
    rows.append({'node':n['id'],'url':url,'http':status,'healthy':ok,'body':body})
quorum=len(voters)//2+1
print(json.dumps({'voters':len(voters),'quorum':quorum,'healthy_count':len(healthy),'healthy_nodes':healthy,'members':rows},ensure_ascii=False,indent=2))
raise SystemExit(0 if len(healthy)>=quorum else 1)
PY
