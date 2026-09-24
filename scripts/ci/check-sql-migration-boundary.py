#!/usr/bin/env python3
"""Source-only migration-boundary checks. Does not replace a .NET build or EF model validation."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ALLOWED_SOURCE_EDITS = {
    '00_AI_READ_THIS_FIRST.md',
    'README.md',
    'services/tasks/assignment-api/TaskForge.Tasks.Api.csproj',
    'services/execution/api/TaskForge.Execution.Api.csproj',
    'services/solutions/api/TaskForge.Solutions.Api.csproj',
    'services/tasks/assignment-api/Data/TasksDbContext.cs',
    'services/execution/api/Data/ExecutionDbContext.cs',
    'services/execution/api/Domain/ExecutionModels.cs',
    'services/solutions/api/Data/SolutionsDbContext.cs',
    'services/solutions/api/Domain/SolutionSubmission.cs',
}
SOURCE_RELOCATIONS = {
    'README.md': 'docs/README.md',
    'TASKFORGE_104_CI_LEGACY_HA_CLEANUP.md': 'docs/history/TASKFORGE_104_CI_LEGACY_HA_CLEANUP.md',
    'TASKFORGE_105_CLUSTER_HA_OBSERVABILITY.md': 'docs/history/TASKFORGE_105_CLUSTER_HA_OBSERVABILITY.md',
    'TASKFORGE_106_CLUSTER_PRIMARY_UI_BOT.md': 'docs/history/TASKFORGE_106_CLUSTER_PRIMARY_UI_BOT.md',
    'TASKFORGE_107_PRIMARY_SWITCH_LIVE_PROGRESS.md': 'docs/history/TASKFORGE_107_PRIMARY_SWITCH_LIVE_PROGRESS.md',
    'TASKFORGE_108_PRIMARY_SWITCH_LOCAL_PATRONI.md': 'docs/history/TASKFORGE_108_PRIMARY_SWITCH_LOCAL_PATRONI.md',
    'TASKFORGE_98_CI_FIX_AUDIT.md': 'docs/history/TASKFORGE_98_CI_FIX_AUDIT.md',
    'TASKFORGE_CLUSTER_DASHBOARD_UPDATE.md': 'docs/history/TASKFORGE_CLUSTER_DASHBOARD_UPDATE.md',
    'TASKFORGE_CLUSTER_DASHBOARD_VALIDATION.md': 'docs/history/TASKFORGE_CLUSTER_DASHBOARD_VALIDATION.md',
}
MIGRATION_DIRS = {
    'services/tasks/assignment-api/Migrations',
    'services/execution/api/Migrations',
    'services/solutions/api/Migrations',
}
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--after-user-migrations', action='store_true',
                    help='Allow only the three target snapshots and newly generated files in their migration directories.')
args = parser.parse_args()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit('FAIL: ' + message)


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding='utf-8-sig')


def manifest(path: str) -> dict[str, str]:
    result = {}
    for line in read(path).splitlines():
        digest, name = line.split('  ', 1)
        result[name] = digest
    return result


baseline = manifest('docs/sql/source-baseline.sha256')
protected = manifest('docs/sql/migrations-baseline.sha256')
for name, digest in baseline.items():
    relocated = SOURCE_RELOCATIONS.get(name, name)
    path = ROOT / relocated
    require(path.is_file(), 'Original file removed: ' + name)
    if relocated != name:
        require(not (ROOT / name).exists(), 'Relocated documentation also remains at root: ' + name)
    allowed_snapshot = (args.after_user_migrations and name.endswith('ModelSnapshot.cs')
                        and str(Path(name).parent) in MIGRATION_DIRS)
    if name not in ALLOWED_SOURCE_EDITS and not allowed_snapshot:
        require(hashlib.sha256(path.read_bytes()).hexdigest() == digest, 'Unexpected original-file edit: ' + name)

actual_migrations = {
    str(p.relative_to(ROOT))
    for p in ROOT.rglob('*')
    if p.is_file() and ('Migrations' in p.relative_to(ROOT).parts or p.name.endswith('ModelSnapshot.cs'))
    and not any(part in ('.git', 'bin', 'obj') for part in p.relative_to(ROOT).parts)
}
new_migrations = actual_migrations - set(protected)
if args.after_user_migrations:
    require(all(str(Path(p).parent) in MIGRATION_DIRS and p.endswith('.cs') for p in new_migrations),
            'New migrations are allowed only in the three intended contexts.')
else:
    require(not new_migrations, 'Migration files were added before the user migration boundary.')
require(set(protected) <= actual_migrations, 'Original migration/snapshot files were removed.')
if args.after_user_migrations:
    for directory in sorted(MIGRATION_DIRS):
        files = {p for p in new_migrations if str(Path(p).parent) == directory}
        primary = {p for p in files if not p.endswith('.Designer.cs') and not p.endswith('ModelSnapshot.cs')}
        require(len(primary) == 1, 'Expected one new user-generated migration in ' + directory)
        migration = next(iter(primary))
        designer = migration[:-3] + '.Designer.cs'
        require(files == {migration, designer}, 'Expected a migration and its Designer file in ' + directory)
        snapshots = [p for p in protected if str(Path(p).parent) == directory and p.endswith('ModelSnapshot.cs')]
        require(len(snapshots) == 1, 'Expected one existing snapshot in ' + directory)
        snapshot = snapshots[0]
        require(hashlib.sha256((ROOT / snapshot).read_bytes()).hexdigest() != protected[snapshot],
                'The user-generated migration did not update its snapshot: ' + snapshot)
    print('PASS: three user migration pairs and three updated snapshots are present; semantic review is still required')
root_markdown = {p.name for p in ROOT.iterdir() if p.is_file() and p.suffix.lower() in ('.md', '.markdown')}
require(root_markdown == {'00_AI_READ_THIS_FIRST.md'}, 'Only 00_AI_READ_THIS_FIRST.md may be Markdown at the root.')
print(f'PASS: {len(baseline)} original files retained ({len(SOURCE_RELOCATIONS)} documentation relocations); {len(protected)} original migration/snapshot files checked')
print('PASS: no root Markdown except 00_AI_READ_THIS_FIRST.md')

sql_root = ROOT / 'services/tasks/assignment-api/Domain/Sql'
classes = set()
for p in sql_root.glob('*.cs'):
    text = p.read_text()
    require('TestsJson' not in text, 'SQL domain must not depend on Assignment.TestsJson: ' + p.name)
    classes.update(re.findall(r'public sealed class (Sql\w+)\s*:\s*ISql(?:Immutable|Mutable)Entity', text))
require(classes == {
    'SqlDataset', 'SqlDatasetVersion', 'SqlEngineProfile', 'SqlAssignmentSpec',
    'SqlAssignmentSpecVersion', 'SqlAssignmentEngineTarget', 'SqlDatasetEngineValidation', 'SqlExpectedArtifact'
}, 'SQL aggregate set does not match the migration handoff.')
ctx = read('services/tasks/assignment-api/Data/TasksDbContext.cs')
require(ctx.count('SqlDomainSaveGuard.Prepare(this)') == 2, 'Sync/async SaveChanges guards must both be present.')
for name in classes:
    require(f'DbSet<{name}>' in ctx, 'Missing DbSet: ' + name)
config = read('services/tasks/assignment-api/Data/Sql/SqlModelConfiguration.cs')
require('DeleteBehavior.Cascade' not in config, 'SQL history must not use cascade deletion.')
for name in ('DraftVersionId', 'PublishedVersionId'):
    require(f'new {{ x.AssignmentId, x.{name} }}' in config, 'A publication pointer is missing its assignment-scoped FK.')
require('SetAfterSaveBehavior(PropertySaveBehavior.Throw)' in config, 'Immutable EF property behavior is missing.')
print('PASS: separate SQL domain, eight mappings, immutable guards and scoped publication FKs')

execution = read('services/execution/api/Domain/ExecutionModels.cs')
for field in ('Kind', 'Target', 'PayloadVersion', 'PayloadJson', 'LeaseToken', 'LeaseExpiresAt', 'ClaimedByWorkerId', 'DeduplicationKey'):
    require(re.search(r'\b' + field + r'\s*\{', execution) is not None, 'Missing execution field: ' + field)
require('public Guid? SubmissionId' in execution, 'Preview/materialization must not require a graded submission.')
require('= ExecutionJobKinds.Legacy;' in execution, 'Legacy job compatibility default is missing.')
solutions = read('services/solutions/api/Domain/SolutionSubmission.cs')
for field in ('ExecutionTarget', 'SqlSpecVersionId', 'SqlEngineProfileId'):
    require(field in solutions, 'Missing submission binding: ' + field)
print('PASS: explicit execution routing metadata and submission revision binding')

for service, context in [('tasks/assignment-api', 'TasksDbContext'), ('execution/api', 'ExecutionDbContext'), ('solutions/api', 'SolutionsDbContext')]:
    factory = read(f'services/{service}/Data/{context}Factory.cs')
    require(f'IDesignTimeDbContextFactory<{context}>' in factory, 'Missing design-time factory: ' + context)
    require('UseNpgsql(' in factory, 'Design-time provider differs from the runtime provider: ' + context)
    for forbidden in ('Migrate(', 'MigrateAsync(', 'EnsureCreated(', 'EnsureCreatedAsync(', '.Open(', 'WebApplication.'):
        require(forbidden not in factory, 'Side effect in design-time factory: ' + context)
manifest_data = json.loads(read('.config/dotnet-tools.json'))
require(manifest_data['tools']['dotnet-ef']['version'] == '10.0.8', 'EF CLI version must match the project design package.')
print('PASS: offline design-time factories and pinned EF tool')
print('PASS: source-only checks; C# compilation and EF model checks must be run separately')
