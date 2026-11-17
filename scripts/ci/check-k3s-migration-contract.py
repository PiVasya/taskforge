#!/usr/bin/env python3
"""Check that K3s/YSQL schema mutations are restricted to isolated one-off Jobs."""
from pathlib import Path
import sys

root=Path(__file__).resolve().parents[2]
paths=[
 'services/identity/api',
 'services/education/api',
 'services/content/api',
 'services/tasks/assignment-api',
 'services/solutions/api',
 'services/execution/api',
]
try:
 for d in paths:
  source=(root/d/'Program.cs').read_text(encoding='utf-8')
  special=(root/d/'Data/SingleJobYugabyteHistoryRepository.cs').read_text(encoding='utf-8')
  assert source.count('options.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository,')==1, d
  before=source.split('options.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository,',1)[0]
  assert 'builder.Configuration.GetValue("Database:RequireYugabyte", false)' in before[-340:], d
  assert 'builder.Configuration.GetValue("Database:MigrationJobOnly", false)' in before[-340:], d
  assert 'NpgsqlHistoryRepository' in special and 'IMigrationsDatabaseLock' in special, d
  assert 'LOCK TABLE' not in special and 'ACCESS EXCLUSIVE' not in special.split('public sealed class',1)[-1], d
  assert 'MigrateAsync()' in source, d
  print('OK: dedicated YSQL migration Job only:',d)
 job_source=(root/'tools/k3s/manifest.mjs').read_text(encoding='utf-8')
 assert "boolEnv('Database__MigrationJobOnly',migration)" in job_source
 assert "boolEnv('Database__RequireYugabyte',true)" in job_source
 local=(root/'infrastructure/k8s/taskforge-yugabyte/local/run-local.sh').read_text(encoding='utf-8')
 assert 'Applying migration Job: $job' in local
 assert 'kubectl -n taskforge-k3s-lab wait --for=condition=complete "job/$job"' in local
 assert '02-migrations.json.yaml"\nfor db' not in local
 print('OK: single-run jobs, sequential local application, no unsafe API auto migrations')
except Exception as e:
 print('FAIL:',e,file=sys.stderr)
 sys.exit(1)
