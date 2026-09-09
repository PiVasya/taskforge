#!/usr/bin/env python3
"""Offline structural checks, not a substitute for .NET/engine/HTTP integration tests."""
from pathlib import Path
import hashlib,json,re
import yaml
ROOT=Path(__file__).resolve().parents[2]

def require(condition,message):
    if not condition:raise SystemExit('FAIL: '+message)

def main():
    protected={}
    for row in (ROOT/'docs/sql/user-212-protected.sha256').read_text().splitlines():
        digest,name=row.split('  ',1);p=ROOT/name
        require(p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==digest,'User migration/model changed: '+name)
        protected[name]=digest
    actual={str(p.relative_to(ROOT)) for p in ROOT.rglob('*.cs') if 'Migrations' in p.parts or p.name.endswith('ModelSnapshot.cs')}
    expected={n for n in protected if '/Migrations/' in n or n.endswith('ModelSnapshot.cs')}
    require(actual==expected,'Migration/snapshot set differs from user develop(212)')
    for name in json.loads((ROOT/'docs/sql/user-212-files.json').read_text()):require((ROOT/name).is_file(),'Original source missing: '+name)
    require({p.name for p in ROOT.glob('*.md')}=={'00_AI_READ_THIS_FIRST.md'},'Root Markdown policy')
    paths=['services/shared/Sql/SqlContracts.cs','services/tasks/assignment-api/Endpoints/Sql/SqlEndpoints.cs',
       'services/execution/api/Endpoints/Sql/SqlExecutionEndpoints.cs','services/solutions/api/Endpoints/Sql/SqlSolutionsEndpoints.cs',
       'services/execution/sql-worker/cmd/sql-worker/main.go','services/execution/sql-worker/internal/native/native.go',
       'services/execution/sql-worker/internal/sqlworker/adapters.go','apps/web/src/features/sql-task/SqlTaskEditor.jsx',
       'apps/web/src/features/sql-task/SqlTaskSolve.jsx','deploy/dev/compose/35-sql.yaml','deploy/prod/compose/35-sql.yaml']
    for p in paths:require((ROOT/p).is_file(),'SQL runtime component missing: '+p)
    worker=ROOT/'services/execution/sql-worker'
    require(not list(worker.rglob('*.py')), 'Python was reintroduced into the SQL worker')
    require(not (worker/'requirements.txt').exists(), 'Retired Python requirements remain')
    docker=(worker/'Dockerfile').read_text()
    require('CGO_ENABLED=1' in docker and 'libpq5 libmariadb3 libsqlite3-0' in docker,'Missing explicit Go/native runtime dependencies')
    require('ENTRYPOINT ["/app/sql-worker"]' in docker and 'FROM runtime AS release' in docker,'Production must select the Go runtime, not the test target')
    require('python:' not in docker and 'pip install' not in docker,'Python runtime image/dependencies are forbidden')
    for relative in ('deploy/dev/compose/35-sql.yaml','deploy/prod/compose/35-sql.yaml'):
        compose=yaml.safe_load((ROOT/relative).read_text())
        service=compose['services']['sql-worker']
        require(service['healthcheck']['test']==['CMD','/app/sql-worker','health'],'SQL healthcheck must use the Go binary')
        require(service['pids_limit']==256,'Go helper thread budget drift')
        require(service['labels']['taskforge.ha.critical']=='false','SQL must not become HA-critical')
        for name,network in (('sql-postgres','sql-postgres-net'),('sql-mysql','sql-mysql-net')):
            engine=compose['services'][name]
            require(engine['networks']==[network] and compose['networks'][network]['internal'],'Sandbox DB network escaped isolation')
            require(not engine.get('ports'),'Sandbox DB ports must not be published')
    for relative in ('scripts/check-sql-update.sh','scripts/sql/test-engines.sh'):
        text=(ROOT/relative).read_text()
        require('PYTHONPATH=' not in text and '-m unittest' not in text,'Retired Python SQL test gate remains')
    for relative in ('.github/workflows/develop-build.yml','.github/workflows/develop-full-rebuild.yml'):
        workflow=yaml.safe_load((ROOT/relative).read_text())
        job=workflow['jobs']['sql-runtime-check']
        require(any(str(step.get('uses','')).startswith('actions/setup-go@') for step in job['steps']),'SQL CI must install Go')
        text=json.dumps(job)
        require('test-engines.sh' in text and 'check-sql-update.sh' in text,'SQL CI gates were removed')
    source=(worker/'internal/sqlworker/harden_linux.go').read_text()
    require('SECCOMP_FILTER_FLAG_TSYNC' in source,'Go seccomp must cover every thread')
    require('CLONE_THREAD' in source,'Go runtime thread-only clone policy missing')
    old=(ROOT/'services/execution/api/Endpoints/InternalExecution/InternalExecutionEndpoints.cs').read_text()
    require(old.count('x.Kind == ExecutionJobKinds.Legacy') >= 2 and 'job.Kind != ExecutionJobKinds.Legacy' in old,'Legacy claim/reaper/completion must exclude SQL')
    for area in ('tasks/assignment-api/Services/Sql','solutions/api/Services/Sql','execution/api/Services/Sql'):
        for p in (ROOT/'services'/area).glob('*.cs'):require('TestsJson' not in p.read_text(),'SQL must not use TestsJson')
    print(f'PASS: {len(expected)} user migration/snapshot files and {len(protected)-len(expected)} domain/model files unchanged from develop(212)')
    print('PASS: original file retention; dedicated SQL API/worker/UI; root documentation policy; Go/native runtime and CI/Compose invariants')
    print('OFFLINE ONLY: run check-sql-update.sh and the real engine gate before release.')
if __name__=='__main__':main()
