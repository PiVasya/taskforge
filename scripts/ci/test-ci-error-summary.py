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

# Exercise the actual Python HTTPRedirectHandler argument contract. The mock
# redirect fixture above does not invoke redirect_request itself.
redirect_handler = module.NoRedirect()
assert redirect_handler.redirect_request(
    Request('https://api.github.com/jobs/99/logs'), None, 302,
    'Found', {'Location': 'https://signed.example/job99'}, 'https://signed.example/job99'
) is None

# The frontend build failure in CI is a file path + ESLint diagnostic, not a
# C#/TS compiler error, and both lines must remain visible in the summary.
with tempfile.TemporaryDirectory() as scratch:
    frontend_log = Path(scratch) / 'frontend' / 'console.log'
    frontend_log.parent.mkdir()
    frontend_log.write_text('''Failed to compile.

[eslint]
src/features/course-assignments/components/CourseFlowEditor.jsx
  Line 2360:6:  React Hook React.useEffect has a missing dependency: 'viewKey'.  react-hooks/exhaustive-deps
Error: Process completed with exit code 1.
''', encoding='utf-8')
    build_errors = module.read_build_errors(Path(scratch))
    assert 'Failed to compile.' in build_errors, build_errors
    assert 'src/features/course-assignments/components/CourseFlowEditor.jsx' in build_errors, build_errors
    assert any('Line 2360:6:' in error and 'viewKey' in error for error in build_errors), build_errors
print('ESLint source location and React Hooks failure appear in the combined diagnostics')

# Send a real HTTP 302 through urllib's NoRedirect handler, then verify that
# the signed endpoint never receives the GitHub token. A separate failed job
# must not prevent another job's evidence from being included.
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
import urllib.request

api_authorization = []
signed_authorization = []
class GitHubFixture(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        api_authorization.append(self.headers.get('Authorization'))
        if '/runs/123/jobs?' in self.path:
            body = b'''{"jobs":[{"id":99,"name":"Frontend production build","conclusion":"failure","steps":[{"name":"Build","conclusion":"failure"}]},{"id":100,"name":"Restricted job","conclusion":"failure","steps":[{"name":"Test","conclusion":"failure"}]},{"id":101,"name":"Direct job log","conclusion":"failure","steps":[{"name":"Test","conclusion":"failure"}]}]}'''
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
        elif self.path.endswith('/jobs/99/logs'):
            body = b''
            self.send_response(302)
            self.send_header('Location', 'https://signed.example/job99')
        elif self.path.endswith('/jobs/100/logs'):
            body = b'forbidden'
            self.send_response(403)
        elif self.path.endswith('/jobs/101/logs'):
            body = b'System.InvalidOperationException: Synchronous operations are disallowed.\n'
            self.send_response(200)
        else:
            body = b'not found'
            self.send_response(404)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

real_urlopen = urllib.request.urlopen

def fake_signed_urlopen(request, timeout=0):
    if isinstance(request, Request) and request.full_url == 'https://signed.example/job99':
        signed_authorization.append(request.get_header('Authorization'))
        return MockResponse(b'''Failed to compile.
[eslint]
src/features/course-assignments/components/CourseFlowEditor.jsx
  Line 2360:6: React Hook React.useEffect has a missing dependency: 'viewKey'. react-hooks/exhaustive-deps
Error: Process completed with exit code 1.
''')
    return real_urlopen(request, timeout=timeout)

with ThreadingHTTPServer(('127.0.0.1', 0), GitHubFixture) as server:
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with patch.dict(os.environ, {
            'GH_TOKEN': 'secret-do-not-send-to-redirect',
            'GITHUB_REPOSITORY': 'foo/bar',
            'GITHUB_RUN_ID': '123',
            'GITHUB_API_URL': f'http://127.0.0.1:{server.server_port}',
        }):
            with patch.object(module.urllib.request, 'urlopen', fake_signed_urlopen):
                jobs, error = module.collect_actions_job_errors()
                assert error is None, error
                assert len(jobs) == 3, jobs
                assert any('viewKey' in text for text in jobs[0][2]), jobs
                assert any('CourseFlowEditor.jsx' in text for text in jobs[0][2]), jobs
                assert 'HTTPError' in jobs[1][2][0], jobs
                assert any('Synchronous operations' in text for text in jobs[2][2]), jobs
                with tempfile.TemporaryDirectory() as scratch:
                    summary = module.write_summary(Path(scratch))
                    assert 'Frontend production build' in summary
                    assert 'CourseFlowEditor.jsx' in summary and 'viewKey' in summary
                    assert 'Log download unavailable (HTTPError)' in summary
                    assert 'System.InvalidOperationException' in summary
    finally:
        server.shutdown()
        thread.join(timeout=5)
assert api_authorization and all(value == 'Bearer secret-do-not-send-to-redirect' for value in api_authorization)
assert signed_authorization == [None, None], signed_authorization
print('Real HTTP 302, error isolation, direct 200 logs, ESLint context and token isolation verified')

with patch.dict(os.environ, {'GH_TOKEN': 'token', 'GITHUB_REPOSITORY': 'foo/bar', 'GITHUB_RUN_ID': '123'}):
    with patch.object(module.urllib.request, 'urlopen', side_effect=RuntimeError('simulated API outage')):
        jobs, error = module.collect_actions_job_errors()
        assert jobs == [] and 'RuntimeError' in error, (jobs, error)
        with tempfile.TemporaryDirectory() as scratch:
            report = module.write_summary(Path(scratch))
            assert 'GitHub job-list API unavailable (RuntimeError)' in report, report
print('GitHub API outage leaves a readable CI report instead of an exception')

with patch.dict(os.environ, {
    'TASKFORGE_CI_NEEDS_JSON': '{"frontend-tests":{"result":"cancelled"}}',
    'GH_TOKEN': '',
}):
    with tempfile.TemporaryDirectory() as scratch:
        report = module.write_summary(Path(scratch))
        assert '**CANCELLED**' in report, report
        assert 'Some gates failed without a .NET TRX report.' in report, report
        assert 'No failed validation gates' not in report, report

sample = [f'error CS{i:04d}: diagnostic {i}\n' for i in range(251)]
truncated = module.extract_error_lines(sample, limit=60)
assert len(truncated) == 61 and 'complete job log' in truncated[-1], truncated
print('Cancelled gates and diagnostic truncation are explicitly reported')
