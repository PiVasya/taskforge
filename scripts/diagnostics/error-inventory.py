#!/usr/bin/env python3
"""Read a TaskForge diagnostics archive and group all actionable HTTP/log errors.

No third-party packages or extraction of the complete archive are required.
The report aggregates errors; it never copies request/response bodies or credentials.
"""
from __future__ import annotations

from collections import Counter, defaultdict
import re
from pathlib import Path
import sys
import tarfile

GATEWAY = re.compile(r'\bhost=(\S+)\s+method=(\S+)\s+path="([^"]+)"\s+status=(\d{3})\b')
GUID = re.compile(r'(?i)(?<![a-z0-9])[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![a-z0-9])')
EXCEPTION = re.compile(r'\b([A-Za-z][A-Za-z.]+(?:Exception|Error))\s*:\s*([^\r\n]*)')
SERVICE_NAME = re.compile(r'(?:^|/)taskforge-prod-([\w-]+)-\d+\.log$')


def normalize_path(path: str) -> str:
    path = path.split('?', 1)[0]
    path = GUID.sub(':guid', path)
    if len(path) > 140:
        path = path[:140] + '...'
    return path


def report(archive: Path) -> str:
    routes_500 = Counter()
    routes_400 = Counter()
    statuses = Counter()
    events: dict[str, Counter] = defaultdict(Counter)
    outbound = Counter()
    source_logs = 0
    time_start = ''
    time_end = ''

    def process(service: str, lines) -> None:
        nonlocal time_start, time_end
        # Keep a tiny rolling context: a .NET fail logger line precedes its
        # exception, often by an intervening TFDBG trace line.
        pending_service = None
        pending_context = ''
        remaining = 0
        for raw in lines:
            line = raw.decode('utf-8', 'replace') if isinstance(raw, bytes) else raw
            if len(line) > 18000:
                line = line[:18000]
            stamp = line[:19]
            if re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d\d:\d\d:\d\d', stamp):
                if not time_start or stamp < time_start:
                    time_start = stamp
                if stamp > time_end:
                    time_end = stamp
            if service == 'gateway':
                if 'TFDBG GATEWAY request_id=' not in line:
                    continue
                m = GATEWAY.search(line)
                if not m:
                    continue
                host, method, path, status = m.groups()
                statuses[(host, status)] += 1
                # Raw probes/404s and auth 401s are separate from server failures.
                key = (host, method, normalize_path(path), status)
                if int(status) >= 500:
                    routes_500[key] += 1
                elif int(status) >= 400:
                    routes_400[key] += 1
                continue

            if service == 'observability-api' and 'TFDBG OUT EXCEPTION' in line:
                m = re.search(r'\burl=https?://[^/\s]+(/[^\s?]*)', line)
                if m:
                    outbound[m.group(1)] += 1

            if 'fail: ' in line or 'crit: ' in line:
                pending_service = service
                pending_context = line.split('fail: ', 1)[-1].split('crit: ', 1)[-1].strip()[:80]
                remaining = 16
                continue

            if pending_service is not None:
                reason = EXCEPTION.search(line)
                if reason:
                    ex_type = reason.group(1).rsplit('.', 1)[-1]
                    message = reason.group(2)
                    message = re.sub(r'https?://[^\s)]+', '<url>', message)
                    message = re.sub(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', '<ip>', message)
                    message = GUID.sub(':guid', message)
                    key = f'{pending_context} -> {ex_type}: {message[:120]}'
                    events[service][key] += 1
                    pending_service = None
                else:
                    remaining -= 1
                    if remaining == 0:
                        events[service][f'{pending_context} -> exception details unavailable'] += 1
                        pending_service = None

            if re.search(r'(?:\bERROR:|\bFATAL:|\[error\]|\blevel=error\b)', line):
                # Exclude healthy analyzer logs that mention errors=0 and expected
                # compile failures from user-submitted code.
                text = re.sub(r'^.*?\b(?:ERROR:|FATAL:|\[error\]|level=error)\s*', '', line).strip()
                if not text:
                    continue
                text = re.sub(r'https?://[^\s)]+', '<url>', text)
                text = GUID.sub(':guid', text)
                text = re.sub(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', '<ip>', text)
                events[service][text[:180]] += 1

    if archive.is_dir():
        files = sorted(archive.rglob('taskforge-prod-*-1.log'))
        for file in files:
            m = SERVICE_NAME.search(str(file))
            if not m:
                continue
            source_logs += 1
            with file.open('rb') as handle:
                process(m.group(1), handle)
    else:
        with tarfile.open(archive, mode='r:gz') as tar:
            for member in tar:
                m = SERVICE_NAME.search(member.name)
                if not m or not member.isfile():
                    continue
                handle = tar.extractfile(member)
                if handle is None:
                    continue
                source_logs += 1
                with handle:
                    process(m.group(1), handle)

    lines = [
        'TASKFORGE: ALL OBSERVED SERVER ERRORS (GROUPED)',
        '=' * 70,
        f'Source: {archive.name}',
        f'Log period (UTC, approximate): {time_start} - {time_end}',
        f'Container logs inspected: {source_logs}',
        'HTTP requests and log records are counted separately. One request may',
        'produce multiple exception log records. This is a snapshot, not a live scan.',
        '',
        '1. ALL HTTP 5xx FROM GATEWAY',
        '-' * 70,
    ]
    if routes_500:
        for (host, method, path, status), count in routes_500.most_common():
            lines.append(f'  {count:5d}  HTTP {status} {method} {path}  (host={host})')
    else:
        lines.append('  None in this archive.')
    lines += ['', '2. INTERNAL FAILURES GROUPED BY SERVICE', '-' * 70]
    if not events:
        lines.append('  None found.')
    for service, counters in sorted(events.items()):
        if not counters:
            continue
        lines.append(f'  {service} ({sum(counters.values())} error-level log records)')
        for error, count in counters.most_common(50):
            lines.append(f'    {count:5d}  {error}')
        if len(counters) > 50:
            lines.append(f'    ... {len(counters) - 50} additional signatures')
    lines += ['', '3. OUTBOUND OBSERVABILITY REQUEST EXCEPTIONS', '-' * 70]
    for path, count in outbound.most_common():
        lines.append(f'  {count:5d}  {path}')
    if not outbound:
        lines.append('  None.')
    lines += ['', '4. NON-5xx CLIENT ERRORS (AUTH / 404 / PROBES)', '-' * 70]
    for (host, method, path, status), count in routes_400.most_common(50):
        lines.append(f'  {count:5d}  HTTP {status} {method} {path}  (host={host})')
    if len(routes_400) > 50:
        lines.append(f'  ... {len(routes_400) - 50} additional unique client-error routes')
    lines += [
        '', '5. LIMITATIONS', '-' * 70,
        '  - Browser JavaScript console errors are not included in server Docker logs.',
        '  - A single node archive cannot establish error counts on other nodes.',
        '  - User-code compilation errors, routine TLS probes and bot 404s are not',
        '    automatically classified as application crashes.',
        '  - Counts from .NET fail logs may contain duplicates of gateway 5xx.',
        '',
    ]
    return '\n'.join(lines)


def main(argv: list[str]) -> int:
    if len(argv) not in (2, 3):
        print(f'Usage: {argv[0]} taskforge-diagnostics.tar.gz [report.txt]', file=sys.stderr)
        return 2
    archive = Path(argv[1])
    if not archive.exists():
        print(f'Not found: {archive}', file=sys.stderr)
        return 2
    text = report(archive)
    if len(argv) == 3:
        Path(argv[2]).write_text(text, encoding='utf-8')
    else:
        print(text)
    return 0


if __name__ == '__main__':
    raise SystemExit(main(sys.argv))
