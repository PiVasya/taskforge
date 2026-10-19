#!/usr/bin/env python3
"""Behavior tests for the GitHub Actions cross-job error inventory."""

import importlib.util
import os
from pathlib import Path
import tempfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('ci_error_summary', ROOT / 'summarize-ci-failures.py')
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

with tempfile.TemporaryDirectory() as scratch:
    files = Path(scratch)
    for i in range(2):
        output = files / f'job{i}'
        output.mkdir()
        (output / f'test{i}.trx').write_text('''<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="StreamingCase%(i)d" outcome="Failed">
      <Output><ErrorInfo><Message>System.InvalidOperationException: Synchronous operations are disallowed.</Message>
      <StackTrace>at Caller()</StackTrace></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testName="PassingCase%(i)d" outcome="Passed" />
  </Results>
</TestRun>
''' % {'i': i}, encoding='utf-8')
        (output / 'console.log').write_text('''Build failed.
error CS1001: Identifier expected
FAIL TEST: services/solutions/api/tests/TaskForge.Solutions.Api.Tests
''', encoding='utf-8')
    os.environ['TASKFORGE_CI_NEEDS_JSON'] = '{"dotnet-behavior-tests":{"result":"failure"},"frontend-tests":{"result":"success"},"compose-check":{"result":"skipped"}}'
    content = module.write_summary(files)
    assert 'StreamingCase0' in content and 'StreamingCase1' in content, content
    assert '2/4' in content, content
    assert '2 failure(s): System.InvalidOperationException:' in content, content
    assert 'frontend-tests' in content and 'SUCCESS' in content, content
    assert 'dotnet-behavior-tests' in content and 'FAILURE' in content, content
    assert 'error CS1001' in content, content
    assert content.count('error CS1001') == 1, content

print('CI cross-job error inventory: 2 failed tests from separate job artifacts, compilation diagnostics and independent job states verified')

from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request
from io import BytesIO

class MockResponse:
    def __init__(self, data):
        self.stream = BytesIO(data)
    def read(self, length=-1):
        return self.stream.read(length)
    def __iter__(self):
        return iter(self.stream)
    def close(self):
        self.stream.close()
    def __enter__(self):
        return self
    def __exit__(self, *_):
        self.close()

class NoFollow:
    def open(self, request, timeout=0):
        raise HTTPError(request.full_url, 302, 'Found', {'Location': 'https://example.test/signed-job-log'}, None)

redirect_authorization = []
def fake_urlopen(request, timeout=0):
    assert isinstance(request, Request)
    if 'signed-job-log' in request.full_url:
        redirect_authorization.append(request.get_header('Authorization'))
        return MockResponse(b'2026-10-19T12:00:00Z ##[error]error CS1001: Identifier expected\n')
    if '/runs/123/jobs?' in request.full_url:
        return MockResponse(b'{"jobs":[{"id":99,"name":".NET tests","conclusion":"failure","steps":[{"name":"Build and test","conclusion":"failure"}]}]}')
    raise AssertionError(f'Unexpected URL: {request.full_url}')

with patch.dict(os.environ, {'GH_TOKEN': 'a-secret-token', 'GITHUB_REPOSITORY': 'foo/bar', 'GITHUB_RUN_ID': '123'}, clear=False):
    with patch.object(module.urllib.request, 'urlopen', fake_urlopen), patch.object(module.urllib.request, 'build_opener', return_value=NoFollow()):
        jobs, fetch_error = module.collect_actions_job_errors()
        assert fetch_error is None, fetch_error
        assert len(jobs) == 1 and 'CS1001' in jobs[0][2][0], jobs
        assert redirect_authorization == [None], redirect_authorization
print('GitHub REST collector: failed job detection, log annotations and redirect-token isolation verified')
