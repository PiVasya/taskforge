#!/usr/bin/env python3
from __future__ import annotations
import base64
import copy
import importlib.util
import json
from pathlib import Path
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
    assert loaded['mode']=='replica' and len(loaded['nodes'])==2
    mod.RUNTIME=tmp/'runtime'; mod.NODE_ID_FILE=mod.RUNTIME/'node-id'
    local=mod.render_local(loaded,mod.node_by_id(loaded,'B'))
    text=local.read_text()
    assert 'POSTGRES_BIND=127.0.0.1' in text
    assert 'MINIO_BIND=127.0.0.1' in text
    wg=mod.wireguard_config(loaded,mod.node_by_id(loaded,'A'),'private')
    assert 'Address = 10.80.0.1/24' in wg
    assert 'AllowedIPs = 10.80.0.2/32' in wg
    assert 'Endpoint = 203.0.113.2:51820' in wg

    c=copy.deepcopy(raw['nodes'][0])
    c.update({'id':'C','priority':80,'public_host':'203.0.113.3','quorum_voter':True})
    c['wireguard']={'ip':'10.80.0.3','listen_port':51937,'public_key':base64.b64encode(bytes([3])*32).decode()}
    c['web']={'http_port':8080,'https_port':8443}
    c['postgres']={'local_port':55432,'cluster_port':5432,'patroni_rest_port':18008}
    c['minio']={'local_port':19000,'console_port':19001,'cluster_port':9000}
    raw['nodes'].append(c); raw['mode']='quorum'; inv.write_text(json.dumps(raw))
    loaded=mod.load_inventory(inv)
    assert len([n for n in loaded['nodes'] if n.get('quorum_voter')])==3
    q=tmp/'cluster.json'; mod.render_quorum(loaded,q)
    rendered=json.loads(q.read_text())
    assert rendered['nodes'][2]['postgres']['host_port']==5432
    assert rendered['nodes'][2]['web']['https_port']==8443
    assert rendered['nodes'][2]['dcs_voter'] is True

    d=copy.deepcopy(c)
    d.update({'id':'D','priority':70,'public_host':'203.0.113.4','quorum_voter':False})
    d['wireguard']={'ip':'10.80.0.4','listen_port':51938,'public_key':base64.b64encode(bytes([4])*32).decode()}
    d['web']={'http_port':18080,'https_port':18443}
    d['postgres']={'local_port':65432,'cluster_port':5432,'patroni_rest_port':28008}
    d['minio']={'local_port':29000,'console_port':29001,'cluster_port':9000}
    raw['nodes'].append(d); inv.write_text(json.dumps(raw)); loaded=mod.load_inventory(inv)
    assert len(loaded['nodes'])==4
    q2=tmp/'cluster4.json'; mod.render_quorum(loaded,q2)
    assert [n['id'] for n in json.loads(q2.read_text())['nodes'] if n['dcs_voter']]==['A','B','C']

bootstrap=(ROOT/'deploy/prod/bootstrap.sh').read_text()
ensure=(ROOT/'deploy/cluster/ensure-host.sh').read_text()
easy=(ROOT/'deploy/cluster/easy.sh').read_text()
entry=(ROOT/'deploy/prod/cluster.sh').read_text()
assert 'socat' in ensure and 'wireguard-tools' in ensure
assert 'Docker is missing; installing it automatically' in ensure
assert 'auto_host_deps' in easy
assert 'cmd_migrate_local' in easy
assert 'local name bind_ip cluster_port local_port unit' in easy
assert 'unit="taskforge-${name}-wg-proxy.service"' in easy
assert 'local name="$1" bind_ip="$2" cluster_port="$3" local_port="$4" unit="taskforge-${name}-wg-proxy.service"' not in easy

# Under `set -u`, Bash expands every assignment in a single `local` command
# before the earlier assignment becomes visible.  Reject dependent same-line
# local assignments (the v29 adopt failure was exactly this pattern).
import re
for line_no, line in enumerate(easy.splitlines(), 1):
    if not re.match(r'^\s*local\b', line):
        continue
    declaration=line.split(';',1)[0]
    assignments=list(re.finditer(r'\b([A-Za-z_][A-Za-z0-9_]*)=', declaration))
    for match in assignments:
        var=match.group(1)
        tail=declaration[match.end():]
        ref=re.compile(r'\$'+re.escape(var)+r'\b|\$\{'+re.escape(var)+r'(?:\}|[:?+\-/])')
        if ref.search(tail):
            raise AssertionError(f'dependent local assignment at easy.sh:{line_no}: {line}')
assert 'migrate-local' in entry
assert '--ensure' in bootstrap
assert 'setup_docker_cli_config' in easy
assert 'export DOCKER_CONFIG="$dir"' in easy
assert "find \"$SCRIPT_DIR\" -type f -name '*.sh' -exec chmod u+x {} +" in entry
assert 'bash ./cluster.sh migrate-local' in entry
assert 'status|doctor) exec_root' in entry

assert 'pg_scalar' in easy and 'pg_fields' in easy
assert "select status from pg_stat_wal_receiver limit 1" in easy
assert "\\047" not in "\n".join(line for line in easy.splitlines() if 'pg_stat_wal_receiver' in line)
assert 'standby streaming from=' in easy
assert 'MinIO replication rule missing' in easy
assert "doctor is diagnostic" in easy
print('TaskForge auto-bootstrap invariants OK')

print('TaskForge easy cluster manager tests OK')
