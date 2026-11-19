#!/usr/bin/env python3
"""Regress the exact failure: old tracked paths survived overwriting a ZIP."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('k3s_branch_sync', ROOT / 'SYNC_TO_CLUSTER_V2.py')
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def git(root: Path, *args: str):
    return subprocess.run(['git','-C',str(root),*args], check=True, stdout=subprocess.PIPE).stdout.decode()


def put(root: Path, name: str, content: str):
    p=root/name
    p.parent.mkdir(parents=True,exist_ok=True)
    p.write_text(content,encoding='utf-8')


class BranchSyncTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(prefix='tf-k3s-sync-')
        self.addCleanup(self.tmp.cleanup)
        self.base=Path(self.tmp.name)
        self.src=self.base/'source'
        self.dst=self.base/'checkout'
        self.src.mkdir()
        self.dst.mkdir()
        for path in (*module.REQUIRED, 'SYNC_TO_CLUSTER_V2.py', 'services/identity/api/Program.cs'):
            put(self.src,path,'fresh source '+path)
        git(self.dst,'init','-q','-b','cluster-v2')
        git(self.dst,'config','user.email','ci@example.invalid')
        git(self.dst,'config','user.name','CI test')
        for path in ('services/ai/worker/Agent.cs', 'apps/web-ct/Dockerfile',
                     'services/tasks/quiz-api/Program.cs','deploy/prod/compose.sh',
                     'deploy/cluster/enable.sh','.github/workflows/develop-build.yml',
                     'README_K3S_BRANCH.md'):
            put(self.dst,path,'old source '+path)
        put(self.dst,'.gitignore','*.sql\n')
        git(self.dst,'add','-A')
        git(self.dst,'commit','-q','-m','previous experimental branch')

    def test_sync_stages_deletion_of_all_legacy_paths_and_new_files(self):
        incoming,obsolete=module.sync(self.src,self.dst)
        self.assertGreaterEqual(incoming,len(module.REQUIRED)+2)
        self.assertGreaterEqual(obsolete,5)
        for name in module.LEGACY_PATHS:
            self.assertFalse((self.dst/name).exists(), name)
        staged=git(self.dst,'diff','--cached','--name-status')
        self.assertIn('D\tservices/ai/worker/Agent.cs',staged)
        self.assertIn('D\tdeploy/prod/compose.sh',staged)
        self.assertIn('A\ttools/k3s/local/generate.mjs',staged)
        self.assertIn('A\tinfrastructure/k8s/taskforge-yugabyte/db/create-databases.sql',staged)
        self.assertIn('A\tinfrastructure/k8s/taskforge-yugabyte/db/create-k3s-lab-databases.sql',staged)
        self.assertEqual((self.dst/'README_K3S_BRANCH.md').read_text(),
                         (self.src/'README_K3S_BRANCH.md').read_text())

    def test_dry_run_never_mutates_checkout(self):
        module.sync(self.src,self.dst,dry_run=True)
        self.assertTrue((self.dst/'services/ai/worker/Agent.cs').exists())
        self.assertEqual(git(self.dst,'status','--porcelain').strip(),'')

    def test_protect_develop_and_dirty_checkouts(self):
        git(self.dst,'branch','-m','develop')
        with self.assertRaisesRegex(ValueError,'REFUSING'):
            module.sync(self.src,self.dst)
        git(self.dst,'branch','-m','cluster-v2')
        put(self.dst,'some-untracked-secret.env','PRIVATE')
        with self.assertRaisesRegex(ValueError,'dirty'):
            module.sync(self.src,self.dst)
        self.assertTrue((self.dst/'some-untracked-secret.env').exists())

    def test_source_and_target_must_be_different(self):
        with self.assertRaisesRegex(ValueError,'SEPARATE'):
            module.sync(self.dst,self.dst)


if __name__=='__main__':
    unittest.main(verbosity=2)
