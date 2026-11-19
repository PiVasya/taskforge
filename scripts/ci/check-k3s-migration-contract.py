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
 # Migration jobs must not depend on a live Redis backplane or its namespace;
 # runtime API replicas still require those settings in multi-node mode.
 signalr=(root/'services/shared/Cluster/DistributedSignalR.cs').read_text(encoding='utf-8')
 migration_guard='if (configuration.GetValue("Database:MigrationJobOnly", false)) return builder;'
 runtime_guard='if (!configuration.GetValue("Cluster:MultiNode", false)) return builder;'
 namespace_guard='var ns = configuration["Cluster:Namespace"]?.Trim();'
 redis_guard='var redis = configuration.GetConnectionString("Redis")'
 assert signalr.index(migration_guard)<signalr.index(runtime_guard)<signalr.index(namespace_guard)<signalr.index(redis_guard), 'Migration-only SignalR bypass must precede runtime validation'
 assert 'builder.AddStackExchangeRedis(redis' in signalr
 assert 'Multi-node SignalR requires Cluster__Namespace' in signalr
 assert 'Multi-node SignalR requires a shared Redis connection' in signalr
 manifest=(root/'tools/k3s/manifest.mjs').read_text(encoding='utf-8')
 assert "{name:'Cluster__Namespace',value:options.namespace}" in manifest, 'Every multi-node pod requires a namespace'
 assert 'env:configEnv(service,migration,options)' in manifest
 localgen=(root/'tools/k3s/local/generate.mjs').read_text(encoding='utf-8')
 assert 'Cluster__Namespace:LOCAL_NAMESPACE' in localgen, 'Local K3s ConfigMap requires a namespace'
 print('OK: migration Jobs bypass SignalR/Redis; API pods retain strict isolated Redis channel namespaces')
 print('OK: single-run jobs, sequential local application, no unsafe API auto migrations')
except Exception as e:
 print('FAIL:',e,file=sys.stderr)
 sys.exit(1)
