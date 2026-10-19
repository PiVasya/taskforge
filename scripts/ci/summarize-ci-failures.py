#!/usr/bin/env python3
"""Combine all CI gate results and .NET test failures into one GitHub job summary."""

from __future__ import annotations

from collections import Counter, defaultdict
from pathlib import Path
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


def load_gates() -> dict[str, str]:
    try:
        data = json.loads(os.environ.get('TASKFORGE_CI_NEEDS_JSON', '{}'))
    except json.JSONDecodeError:
        data = {}
    return {name: details.get('result', 'unknown') for name, details in data.items() if isinstance(details, dict)}


def read_trx_errors(folder: Path) -> tuple[int, int, dict[str, list[str]]]:
    failures: dict[str, list[str]] = defaultdict(list)
    passed = 0
    count = 0
    for path in sorted(folder.rglob('*.trx')):
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as exc:
            failures['Unreadable test report'].append(f'{path.name}: {type(exc).__name__}: {exc}')
            continue
        # TRX XML uses a namespace whose exact version may change.
        for result in root.iter():
            if result.tag.rsplit('}', 1)[-1] != 'UnitTestResult':
                continue
            outcome = result.attrib.get('outcome', '')
            name = result.attrib.get('testName', 'unknown test')
            if outcome == 'Passed':
                passed += 1
                count += 1
            elif outcome == 'Failed':
                count += 1
                messages = [node.text or '' for node in result.iter() if node.tag.rsplit('}', 1)[-1] == 'Message']
                message = next((text.strip() for text in messages if text.strip()), 'Test failed (no error message)')
                # Keep stack snippets in the uploaded artifact; group by failure type here.
                reason = message.splitlines()[0][:240]
                failures[reason].append(f'{name} ({path.name})')
    return passed, count, failures


def read_build_errors(folder: Path) -> list[str]:
    errors: list[str] = []
    seen: set[str] = set()
    indicator = re.compile(r'(?i)(?:\berror\s+(?:CS|MSB|NETSDK|NU|TS)\d+\s*:|\berror:\s|\bFAIL BUILD:|\bFAIL TEST:)')
    for path in sorted(folder.rglob('console.log')):
        try:
            with path.open(encoding='utf-8', errors='replace') as file:
                for line in file:
                    if not indicator.search(line):
                        continue
                    clean = re.sub(r'\x1b\[[0-9;]*m', '', line).strip()[:350]
                    if clean not in seen:
                        seen.add(clean)
                        errors.append(clean)
        except OSError:
            continue
    return errors



def collect_actions_job_errors() -> tuple[list[tuple[str, str, list[str]]], str | None]:
    """Read failed job logs after all required workflow jobs have completed.

    GitHub's log endpoint returns a signed redirect. Never send GITHUB_TOKEN to
    the redirect destination; the download URL is already authorized.
    """
    token = os.environ.get('GH_TOKEN')
    repo = os.environ.get('GITHUB_REPOSITORY')
    run_id = os.environ.get('GITHUB_RUN_ID')
    if not token or not repo or not run_id:
        return [], None

    base = os.environ.get('GITHUB_API_URL', 'https://api.github.com').rstrip('/')
    headers = {'Authorization': f'Bearer {token}', 'Accept': 'application/vnd.github+json',
               'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'TaskForge-CI-Report'}

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, msg, newurl):
            return None

    def get_api(path: str, redirect: bool = False):
        url = base + path
        request = urllib.request.Request(url, headers=headers)
        if not redirect:
            with urllib.request.urlopen(request, timeout=20) as response:
                return response.read()
        opener = urllib.request.build_opener(NoRedirect())
        try:
            opener.open(request, timeout=20).close()
            return b''
        except urllib.error.HTTPError as ex:
            if ex.code not in (301, 302, 303, 307, 308):
                raise
            location = ex.headers.get('Location', '')
            if not location.startswith('https://'):
                raise ValueError('GitHub job logs did not return an HTTPS redirect') from ex
            unsigned = urllib.request.Request(location, headers={'User-Agent': 'TaskForge-CI-Report'})
            with urllib.request.urlopen(unsigned, timeout=30) as response:
                # Scan the entire job log, not just its beginning. Retain only
                # unique diagnostic lines so huge Docker builds stay bounded.
                lines = []
                seen = set()
                for raw_line in response:
                    line = raw_line.decode('utf-8', 'replace')
                    if not failure_line.search(line):
                        continue
                    line = line.strip()[:400]
                    if line not in seen:
                        seen.add(line)
                        lines.append(line)
                    if len(lines) >= 250:
                        break
                return '\n'.join(lines).encode('utf-8')

    prefix = '/repos/' + urllib.parse.quote(repo, safe='/') + '/actions'
    jobs: list[dict] = []
    try:
        for page in range(1, 11):
            response = json.loads(get_api(f'{prefix}/runs/{run_id}/jobs?per_page=100&page={page}'))
            batch = response.get('jobs', [])
            jobs.extend(batch)
            if len(batch) < 100:
                break
    except (OSError, ValueError, urllib.error.URLError) as ex:
        return [], f'GitHub job-list API unavailable: {type(ex).__name__}'

    failure_line = re.compile(
        r'(?i)(?:##\[error\]|\berror\s+(?:CS|MSB|NETSDK|NU|TS)\d+\b|'
        r'\b(?:FAIL TEST|FAIL BUILD|Failed!|\[FAIL\]|AssertionError:|Traceback \(most recent|'
        r'System\.[\w.]+Exception:|\bError: Process completed|\bpanic:|\bFATAL:))'
    )
    report = []
    for job in jobs:
        if job.get('conclusion') not in ('failure', 'timed_out', 'cancelled'):
            continue
        job_id = job.get('id')
        name = str(job.get('name', 'unknown job'))
        failed_steps = ', '.join(str(step.get('name', 'step')) for step in job.get('steps', [])
                                 if step.get('conclusion') in ('failure', 'timed_out', 'cancelled'))
        evidence: list[str] = []
        try:
            content = get_api(f'{prefix}/jobs/{job_id}/logs', redirect=True).decode('utf-8', 'replace')
            seen: set[str] = set()
            for line in content.splitlines():
                if not failure_line.search(line):
                    continue
                clean = re.sub(r'\x1b\[[0-9;]*m', '', line).strip()
                clean = re.sub(r'(?i)(bearer\s+)\S+', r'\1[redacted]', clean)
                # Never show arbitrary multi-line request bodies in a public summary.
                clean = clean[:350]
                if clean not in seen:
                    seen.add(clean)
                    evidence.append(clean)
                if len(evidence) >= 60:
                    break
        except (OSError, ValueError, urllib.error.URLError) as ex:
            evidence = [f'Log download unavailable ({type(ex).__name__}); open the job directly.']
        report.append((name, failed_steps, evidence))
    return report, None


def write_summary(folder: Path) -> str:
    gates = load_gates()
    job_errors, job_api_error = collect_actions_job_errors()
    passed, total, test_errors = read_trx_errors(folder)
    build_errors = read_build_errors(folder)
    lines = ['# TaskForge: all CI errors in one report', '']
    if gates:
        lines += ['## Every validation gate', '', '| Gate | Result |', '|---|---|']
        for name, status in sorted(gates.items(), key=lambda item: (item[1] not in ('failure', 'cancelled'), item[0])):
            lines.append(f'| `{name}` | **{status.upper()}** |')
        lines.append('')
    if total:
        lines += [f'## .NET test failures: {total - passed}/{total}', '']
    elif not list(folder.rglob('*.trx')):
        lines += ['No TRX files were uploaded for this run (the failing step may have stopped at compilation).', '']
    if test_errors:
        for reason, names in sorted(test_errors.items(), key=lambda item: (-len(item[1]), item[0])):
            lines += [f'### {len(names)} failure(s): {reason.replace("|", "\\|")}', '']
            lines += [f'- `{name}`' for name in names[:100]]
            if len(names) > 100:
                lines.append(f'- ... and {len(names) - 100} more tests (see downloaded TRX)')
            lines.append('')
    if build_errors:
        lines += [f'## Build/test command errors ({len(build_errors)} distinct lines)', '']
        lines += [f'- `{text.replace("`", "\'")}`' for text in build_errors[:120]]
        if len(build_errors) > 120:
            lines.append(f'- ... {len(build_errors) - 120} additional lines in the log artifacts')
        lines.append('')
    if job_errors:
        lines += [f'## All failed GitHub Actions jobs ({len(job_errors)})', '']
        for job_name, failed_steps, evidence in job_errors:
            lines += [f'### {job_name}', '']
            if failed_steps:
                lines.append(f'Failed step(s): {failed_steps}')
            if evidence:
                lines += [''] + [f'- `{item.replace("`", "'")}`' for item in evidence[:60]]
            else:
                lines.append('No error-pattern lines found; open the job log for full details.')
            lines.append('')
    if job_api_error:
        lines += [job_api_error, '']
    if not test_errors and not build_errors and gates and not any(status == 'failure' for status in gates.values()):
        lines += ['No failed validation gates or .NET tests were found.', '']
    if not test_errors and not build_errors and any(status == 'failure' for status in gates.values()):
        lines += ['Some gates failed without a .NET TRX report. Open those job logs; they are still listed above.', '']
    return '\n'.join(lines) + '\n'


def main() -> int:
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ.get('TASKFORGE_CI_RESULTS_DIR', '.'))
    summary = write_summary(folder)
    print(summary)
    target = os.environ.get('GITHUB_STEP_SUMMARY')
    if target:
        with open(target, 'a', encoding='utf-8') as file:
            file.write(summary)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
