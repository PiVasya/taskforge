#!/usr/bin/env python3
"""Check the one migration generator CLI without a real .NET toolchain."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
NORMAL = ROOT / 'scripts/generate-migrations.sh'


def run(script: Path, *args: str, env: dict[str, str] | None = None):
    return subprocess.run(['bash', str(script), *args], cwd=ROOT, env=env,
                          capture_output=True, text=True, check=False)


assert run(NORMAL, '--list').returncode == 0
assert 'TasksDbContext' in run(NORMAL, '--list').stdout
assert run(NORMAL).returncode == 2
assert run(NORMAL, 'Bad Name!', 'identity').returncode == 2
assert run(NORMAL, 'ValidMigration', 'invalid-target').returncode == 2

FAKE_DOTNET = '''#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$TF_FAKE_DOTNET_LOG"
if [[ "$1 $2 $3" == "ef migrations has-pending-model-changes" ]]; then
  case "${TF_FAKE_DOTNET_MODE:-none}" in
    none) echo "No changes have been made to the model since the last migration."; exit 0 ;;
    pending) echo "Changes have been made to the model since the last migration."; exit 1 ;;
    failure) echo "Unable to create a DbContext; build failed" >&2; exit 2 ;;
    failure_after_no_changes) echo "No changes have been made to the model since the last migration."; echo "EF environment failed" >&2; exit 2 ;;
  esac
fi
exit 0
'''

with tempfile.TemporaryDirectory() as tmp:
    root = Path(tmp)
    fake = root / 'dotnet'
    fake.write_text(FAKE_DOTNET, encoding='utf-8')
    fake.chmod(0o755)
    log = root / 'dotnet.log'
    env = os.environ.copy()
    env.update(PATH=f"{root}:{env.get('PATH', '')}", TF_FAKE_DOTNET_LOG=str(log))
    for mode in ('none', 'pending', 'failure', 'failure_after_no_changes'):
        env['TF_FAKE_DOTNET_MODE'] = mode
        log.write_text('')
        result = run(NORMAL, 'TestMigration', 'tasks', env=env)
        calls = log.read_text().splitlines()
        assert 'tool restore' in calls, (mode, calls)
        assert 'restore services/tasks/assignment-api/TaskForge.Tasks.Api.csproj' in calls, (mode, calls)
        adds = [x for x in calls if x.startswith('ef migrations add ')]
        if mode == 'none':
            assert result.returncode == 0 and not adds, (result.stderr, calls)
        elif mode == 'pending':
            assert result.returncode == 0 and len(adds) == 1, (result.stderr, calls)
        elif mode.startswith('failure'):
            assert result.returncode == 2 and not adds, (result.stderr, calls)
        assert all('services/identity/' not in x for x in calls), calls

    # Release builds must not inspect stale Debug bin/obj via EF --no-build.
    env['TF_FAKE_DOTNET_MODE'] = 'pending'
    env['DOTNET_BUILD_CONFIGURATION'] = 'Release'
    log.write_text('')
    result = run(NORMAL, 'ReleaseMigration', 'tasks', env=env)
    assert result.returncode == 0, result.stderr
    ef_calls = [x for x in log.read_text().splitlines() if x.startswith('ef ')]
    assert len(ef_calls) == 3, ef_calls
    assert all('--configuration Release' in x for x in ef_calls), ef_calls

print('PASS: migration generator target isolation, pending/no-change/error, pinned EF CLI')
