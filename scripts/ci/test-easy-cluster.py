#!/usr/bin/env python3
from __future__ import annotations
import base64
import copy
import importlib.util
import json
from pathlib import Path
import re
import sys
import tempfile

ROOT=Path(__file__).resolve().parents[2]
PATH=ROOT/'deploy/cluster/manager.py'
spec=importlib.util.spec_from_file_location('tf_easy_manager_test',PATH)
assert spec and spec.loader
mod=importlib.util.module_from_spec(spec); sys.modules[spec.name]=mod; spec.loader.exec_module(mod)

with tempfile.TemporaryDirectory(prefix='tf-easy-cluster-') as td:
    tmp=Path(td)
    raw=json.loads((ROOT/'deploy/cluster/inventory.example.json').read_text())
    for i,node in enumerate(raw['nodes'],1):
        node['public_host']=f'203.0.113.{i}'
        node['wireguard']['public_key']=base64.b64encode(bytes([i])*32).decode()
    inv=tmp/'inventory.json'; inv.write_text(json.dumps(raw))
    loaded=mod.load_inventory(inv)
    assert loaded['mode']=='replica' and len(loaded['nodes'])==3
    assert mod.node_by_id(loaded,'C')['app']['profile']=='lite'
    assert mod.node_by_id(loaded,'C')['app']['can_be_primary'] is False
    mod.RUNTIME=tmp/'runtime'; mod.NODE_ID_FILE=mod.RUNTIME/'node-id'
    local=mod.render_local(loaded,mod.node_by_id(loaded,'B'))
    text=local.read_text()
    assert 'POSTGRES_BIND=127.0.0.1' in text
    assert 'MINIO_BIND=127.0.0.1' in text
    wg=mod.wireguard_config(loaded,mod.node_by_id(loaded,'A'),'private')
    assert 'Address = 10.80.0.1/24' in wg
    assert 'AllowedIPs = 10.80.0.2/32' in wg and 'AllowedIPs = 10.80.0.3/32' in wg

    quorum=copy.deepcopy(raw); quorum['mode']='quorum'; inv.write_text(json.dumps(quorum))
    loaded=mod.load_inventory(inv)
    assert len([n for n in loaded['nodes'] if n.get('quorum_voter')])==3
    q=tmp/'cluster.json'; mod.render_quorum(loaded,q)
    rendered=json.loads(q.read_text())
    nodes={n['id']:n for n in rendered['nodes']}
    assert nodes['A']['app']['profile']=='full' and nodes['A']['app']['can_be_primary'] is True
    assert nodes['B']['app']['profile']=='full' and nodes['B']['app']['can_be_primary'] is True
    assert nodes['C']['app']['profile']=='lite' and nodes['C']['app']['can_be_primary'] is False
    assert nodes['C']['dcs_voter'] is True
    assert 'browser-api' in nodes['C']['app']['exclude_services']
    assert 'image-analyzer' in nodes['C']['app']['exclude_services']

    d=copy.deepcopy(quorum['nodes'][0])
    d.update({'id':'D','priority':70,'public_host':'203.0.113.4','quorum_voter':False})
    d['wireguard']={'ip':'10.80.0.4','listen_port':51824,'public_key':base64.b64encode(bytes([4])*32).decode()}
    d['app']={'profile':'full','can_be_primary':True,'assist_on_failover':False,'exclude_services':[],'assist_exclude_services':[]}
    quorum['nodes'].append(d); inv.write_text(json.dumps(quorum)); loaded=mod.load_inventory(inv)
    q2=tmp/'cluster4.json'; mod.render_quorum(loaded,q2)
    assert [n['id'] for n in json.loads(q2.read_text())['nodes'] if n['dcs_voter']]==['A','B','C']

bootstrap=(ROOT/'deploy/prod/bootstrap.sh').read_text()
ensure=(ROOT/'deploy/cluster/ops/host/ensure-host.sh').read_text()
easy=(ROOT/'deploy/cluster/easy.sh').read_text()
entry=(ROOT/'deploy/prod/cluster.sh').read_text()
compose=(ROOT/'deploy/prod/compose.sh').read_text()
storage=(ROOT/'deploy/prod/compose/00-storage.yaml').read_text()
assert 'socat' in ensure and 'wireguard-tools' in ensure and 'ufw' in ensure
assert 'Docker is missing; installing it automatically' in ensure
assert 'cmd_migrate_local' in easy and 'cmd_upgrade_v39' in easy
assert 'current_primary_id' in easy
assert 'proxy_reconcile' in easy or 'install_proxy' in easy

# Under set -u reject dependent same-line local assignments.
for line_no,line in enumerate(easy.splitlines(),1):
    if not re.match(r'^\s*local\b',line): continue
    declaration=line.split(';',1)[0]
    assignments=list(re.finditer(r'\b([A-Za-z_][A-Za-z0-9_]*)=',declaration))
    for match in assignments:
        var=match.group(1); tail=declaration[match.end():]
        ref=re.compile(r'\$'+re.escape(var)+r'\b|\$\{'+re.escape(var)+r'(?:\}|[:?+\-/])')
        if ref.search(tail): raise AssertionError(f'dependent local assignment at easy.sh:{line_no}: {line}')

assert 'upgrade-v39' in entry and 'finalize-v40' in entry
assert '--ensure' in bootstrap
assert 'profile_pull_services' in compose and "profile == 'lite'" in compose
assert 'TASKFORGE_LAYOUT_ROOT' in compose
assert 'TASKFORGE_POSTGRES_INIT_DIR' in compose and 'TASKFORGE_POSTGRES_INIT_DIR' in storage
assert 'status|doctor) exec_root' in entry
assert 'standby streaming from=' in easy
assert 'MinIO replication rule missing' in easy
assert 'doctor is diagnostic' in easy
print('TaskForge v40 easy cluster manager tests OK')
