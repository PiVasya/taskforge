#!/usr/bin/env python3
"""Local mock-based regression checks for GitHub Actions' disposable YugabyteDB job."""
import os
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/ci/yugabyte-smoke.sh'
MOCK = r'''#!/bin/sh
printf '%s\n' "$*" >> "$MOCK_ROOT/calls.txt"
case "$1" in
  container)
    [ -f "$MOCK_ROOT/running" ] && exit 0 || exit 1
    ;;
  pull) exit 0 ;;
  run)
    case " $* " in *' --hostname taskforge-k3s-lab-ci-db '*) ;; *) echo 'bad container hostname' >&2; exit 7;; esac
    case " $* " in *' --publish 127.0.0.1:15433:5433 '*) ;; *) echo 'bad isolated port' >&2; exit 7;; esac
    : > "$MOCK_ROOT/running"
    echo mock-container-id
    exit 0
    ;;
  inspect)
    case " $* " in *'State.Status'*)
      if [ -f "$MOCK_ROOT/running" ]; then echo running; else echo exited; fi ;;
      *) echo 'status=running exit=0 oom_killed=false';;
    esac
    exit 0
    ;;
  stats) echo 'name=mock cpu=0% mem=0MB'; exit 0 ;;
  exec)
    case " $* " in
      *' bin/ysqlsh '*)
        last=''
        for arg in "$@"; do
          if [ "$last" = '--host' ] && [ "$arg" != 'taskforge-k3s-lab-ci-db' ]; then
            echo 'wrong YSQL host: must use Docker hostname' >&2
            exit 8
          fi
          last="$arg"
        done
        if [ "${MOCK_YSQL_FAIL:-0}" = '1' ]; then echo 'connection refused' >&2; exit 1; fi
        case " $* " in
          *'SELECT version()'*) echo 'PostgreSQL 15.2-YB-2026.1.2' ;;
          *'pg_catalog.pg_tables'*) echo 42 ;;
          *'__EFMigrationsHistory'*) echo 9 ;;
          *'SELECT n FROM __k3s_ci_write_probe'*) echo 1 ;;
          *'SELECT 1'*) echo 1 ;;
          *'--file'*) echo 'CREATE DATABASE' ;;
        esac
        exit 0
        ;;
      *' hostname '*) echo taskforge-k3s-lab-ci-db; exit 0 ;;
      *' status '*) echo 'mock status: running'; exit 0 ;;
      *) echo 'mock process'; exit 0 ;;
    esac
    ;;
  cp) exit 0 ;;
  logs)
    echo 'Starting the YugabyteDB Processes...'
    echo 'postgresql: fake diagnostic line'
    exit 0
    ;;
  rm)
    rm -f "$MOCK_ROOT/running"
    exit 0
    ;;
esac
echo 'unexpected Docker invocation' >&2
exit 9
'''


class YugabyteSmokeTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='tf-ysql-ci-')
        self.addCleanup(self.temp.cleanup)
        self.path = pathlib.Path(self.temp.name)
        bin_dir = self.path / 'bin'
        bin_dir.mkdir()
        docker = bin_dir / 'docker'
        docker.write_text(MOCK)
        docker.chmod(0o755)
        dotnet = bin_dir / 'dotnet'
        dotnet.write_text('''#!/bin/sh
case "$*" in
  *services/education/api/*)
    if [ "${MOCK_DOTNET_FAIL_EDU:-0}" = 1 ]; then
      echo 'Unhandled exception. System.InvalidOperationException: Multi-node SignalR requires Cluster__Namespace' >&2
      exit 9
    fi
    ;;
esac
echo mock dotnet build
''')
        dotnet.chmod(0o755)
        self.env = dict(os.environ, PATH=str(bin_dir)+os.pathsep+os.environ['PATH'],
                        MOCK_ROOT=str(self.path), RUNNER_TEMP=str(self.path),
                        GITHUB_STEP_SUMMARY=str(self.path/'summary.md'),
                        YB_CI_READY_SECONDS='2', YB_CI_RETRY_SECONDS='1')

    def run_phase(self, phase, success=True):
        r = subprocess.run(['bash', str(SCRIPT), phase], cwd=ROOT,
                           env=self.env, text=True, capture_output=True, timeout=90)
        if success:
            self.assertEqual(r.returncode, 0, r.stdout+'\n'+r.stderr)
        else:
            self.assertNotEqual(r.returncode, 0, r.stdout+'\n'+r.stderr)
        return r

    def test_successful_start_uses_docker_hostname_not_loopback(self):
        r = self.run_phase('start')
        self.assertIn('YSQL ready', r.stdout)
        cmds=(self.path/'calls.txt').read_text()
        self.assertIn('--host taskforge-k3s-lab-ci-db', cmds)
        self.assertNotIn('--host 127.0.0.1', cmds)
        self.assertIn('127.0.0.1:15433:5433', cmds)
        self.run_phase('cleanup')
        self.assertFalse((self.path/'running').exists())

    def test_full_pipeline_and_summary(self):
        for phase in ('start','create','migrate','verify','cleanup'):
            self.run_phase(phase)
        summary=(self.path/'summary.md').read_text()
        self.assertIn('YugabyteDB startup', summary)
        for db in ('identity','education','content','tasks','solutions','execution'):
            self.assertIn('taskforge_k3s_'+db, summary)
            self.assertIn('EF Core ('+db+')', summary)
        self.assertFalse((self.path/'running').exists())

    def test_failed_migration_preserves_root_error_outside_group_and_in_artifact(self):
        self.env['MOCK_DOTNET_FAIL_EDU']='1'
        r = self.run_phase('migrate', success=False)
        self.assertIn('EF Core | education', r.stdout)
        self.assertIn('EF migration FAILED for education', r.stdout)
        self.assertIn('InvalidOperationException: Multi-node SignalR requires Cluster__Namespace', r.stdout)
        self.assertIn('EF migrations failed for education (exit=9)', r.stderr)
        self.assertTrue((self.path/'taskforge-ysql-diagnostics/ef-education.log').is_file())
        self.assertIn('FAILED', (self.path/'summary.md').read_text())

    def test_readiness_failure_is_explicit_not_a_spinner_dump(self):
        self.env['MOCK_YSQL_FAIL']='1'
        r=self.run_phase('start', success=False)
        self.assertIn('YSQL readiness deadline',r.stderr)
        self.assertIn('connection refused', r.stdout)
        self.run_phase('diagnose')
        content=(self.path/'taskforge-ysql-diagnostics/summary.txt').read_text()
        self.assertIn('postgresql: fake diagnostic line',content)
        self.assertNotIn('Starting the YugabyteDB Processes',content)
        self.run_phase('cleanup')

    def test_refuses_to_reuse_existing_container(self):
        (self.path/'running').touch()
        r=self.run_phase('start',success=False)
        self.assertIn('refusing to reuse',r.stderr)

    def test_invalid_subcommand_and_config_rejected(self):
        r=self.run_phase('unknown',success=False)
        self.assertIn('Usage:',r.stderr)
        self.env['YB_CI_PORT']='543'
        r=self.run_phase('start',success=False)
        self.assertIn('Invalid local CI port',r.stderr)


if __name__=='__main__':
    unittest.main(verbosity=2)
