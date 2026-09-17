#!/usr/bin/env python3
import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
SCRIPT=ROOT/'scripts/sql/prepare-runtime.py'
spec=importlib.util.spec_from_file_location('prepare_runtime',SCRIPT)
prepare=importlib.util.module_from_spec(spec);spec.loader.exec_module(prepare)
PREFERRED='mysql@sha256:3466ba4a4828aa8d46fb7c3bc16b67b781c98413cf4ea0fac6feaa6e881faa26'
R70_OL9='mysql@sha256:dab7049abafe3a0e12cbe5e49050cf149881c0cd9665c289e5808b9dad39c9e0'
CPUV1='mysql@sha256:f7a8e140a7d6d1e6e0c99eeb0489c50a186ee4ac44ff55323a176529b9a43d33'
assert prepare.MYSQL_PREFERRED_IMAGE==PREFERRED
assert prepare.MYSQL_CPUV1_IMAGE==CPUV1
assert prepare.MYSQL_CERTIFIED_DIGESTS==frozenset({PREFERRED.split('@',1)[1],R70_OL9.split('@',1)[1],CPUV1.split('@',1)[1]})
assert prepare.MYSQL_SELECTABLE_DIGESTS==frozenset({PREFERRED.split('@',1)[1],CPUV1.split('@',1)[1]})

def fake_pin(image,pull):
    digest=image.split('@',1)[1] if '@' in image else 'sha256:'+'9'*64
    return image,digest

def run_prepare(needs_cpuv1, observed_failure=False, override='', existing_mysql='mysql@sha256:'+'e'*64, existing_family=''):
    tmp=tempfile.TemporaryDirectory(); root=Path(tmp.name); env=root/'sql-runtime.env'; base=root/'taskforge.env'
    env.write_text('\n'.join([
        'SQL_SANDBOX_MARKER='+'a'*64,
        'SQL_POSTGRES_PASSWORD='+'b'*64,
        'SQL_MYSQL_PASSWORD='+'c'*64,
        'SQL_POSTGRES_IMAGE=postgres@sha256:'+'d'*64,
        'SQL_MYSQL_IMAGE='+existing_mysql,
        *(['SQL_MYSQL_RUNTIME_FAMILY='+existing_family] if existing_family else []),
    ])+'\n')
    base.write_text(('SQL_MYSQL_IMAGE='+override+'\n') if override else '')
    argv=['prepare-runtime.py','--env-file',str(env),'--base-env',str(base),'--node','B','--profile','full','--pull']
    stdout=io.StringIO()
    with patch.object(sys,'argv',argv), patch.object(prepare,'pin',side_effect=fake_pin) as pin, \
         patch.object(prepare,'host_needs_cpuv1',return_value=needs_cpuv1), \
         patch.object(prepare,'observed_x86_v2_failure',return_value=observed_failure), \
         contextlib.redirect_stdout(stdout):
        prepare.main()
    return tmp, prepare.read_env(env), pin, stdout.getvalue()

tmp,current,pin,out=run_prepare(False)
try:
    assert current['SQL_MYSQL_IMAGE']==PREFERRED
    assert current['SQL_MYSQL_RUNTIME_VARIANT']=='preferred'
    assert current['SQL_MYSQL_RUNTIME_FAMILY']==prepare.MYSQL_RUNTIME_FAMILY
    assert current['SQL_POSTGRES_IMAGE']=='postgres@sha256:'+'d'*64
    assert pin.call_args_list[1].args[0]==PREFERRED
    assert 'preferred-x86-64-v2-runtime' in out
finally: tmp.cleanup()

tmp,current,pin,out=run_prepare(True)
try:
    assert current['SQL_MYSQL_IMAGE']==CPUV1
    assert current['SQL_MYSQL_RUNTIME_VARIANT']=='cpuv1'
    assert pin.call_args_list[1].args[0]==CPUV1
    assert 'host-cpu-capability' in out
finally: tmp.cleanup()

tmp,current,pin,out=run_prepare(False, observed_failure=True)
try:
    assert current['SQL_MYSQL_IMAGE']==CPUV1
    assert pin.call_args_list[1].args[0]==CPUV1
    assert 'observed-x86-64-v2-failure' in out
finally: tmp.cleanup()


tmp,current,pin,out=run_prepare(True, existing_mysql=CPUV1, existing_family=prepare.MYSQL_RUNTIME_FAMILY)
try:
    assert current['SQL_MYSQL_IMAGE']==CPUV1
    assert current['SQL_MYSQL_RUNTIME_VARIANT']=='cpuv1'
    assert 'stable-hardware-selection' in out
finally: tmp.cleanup()

tmp,current,pin,out=run_prepare(True, existing_mysql=PREFERRED, existing_family=prepare.MYSQL_RUNTIME_FAMILY)
try:
    assert current['SQL_MYSQL_IMAGE']==CPUV1
    assert 'corrected-stale-hardware-selection' in out
finally: tmp.cleanup()


tmp,current,pin,out=run_prepare(False, existing_mysql=R70_OL9, existing_family='mysql-8.4.0-ol8-ol9-v1')
try:
    assert current['SQL_MYSQL_IMAGE']==PREFERRED
    assert current['SQL_MYSQL_RUNTIME_VARIANT']=='preferred'
    assert 'runtime-family-upgrade' in out
finally: tmp.cleanup()

override='registry.example/mysql-custom@sha256:'+'f'*64
tmp,current,pin,out=run_prepare(True, observed_failure=True, override=override)
try:
    assert current['SQL_MYSQL_IMAGE']==override
    assert current['SQL_MYSQL_RUNTIME_VARIANT']=='operator'
    assert current['SQL_MYSQL_RUNTIME_FAMILY']=='operator-custom'
    assert pin.call_args_list[1].args[0]==override
    assert 'operator-override' in out
finally: tmp.cleanup()

for rel in ('deploy/prod/compose/35-sql.yaml','deploy/dev/compose/35-sql.yaml'):
    text=(ROOT/rel).read_text()
    assert PREFERRED in text,rel
    mysql=text.split('  sql-mysql:',1)[1].split('\n  sql-worker:',1)[0]
    assert 'restart: "on-failure:5"' in mysql,rel

assert PREFERRED in (ROOT/'scripts/sql/test-engines.sh').read_text()

print('PASS: SQL runtime converges modern nodes on published MySQL 8.4.11, preserves OL8 cpuv1 fallback, and upgrades r70 family state safely')
