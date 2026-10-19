#!/usr/bin/env python3
"""Read-only correlation of [TFQUEUE] events from one or more cluster diagnostic bundles."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
from io import TextIOWrapper
from pathlib import Path
import re
import tarfile
import zipfile
from typing import Iterable, Iterator, TextIO

SERVICES = ("execution-api", "execution-worker", "solutions-api")
STAMP = re.compile(r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)")
PAIR = re.compile(r"(?:^|\s)([A-Za-z][A-Za-z0-9]*)=([^\s]*)")


@dataclass(frozen=True)
class Event:
    timestamp: str
    service: str
    event: str
    job: str
    submission: str
    worker: str
    line: str


def relevant(name: str) -> bool:
    low = name.lower()
    return low.endswith(".log") and any(service in Path(low).name for service in SERVICES)


def inputs(paths: list[Path]) -> Iterator[tuple[str, TextIO]]:
    for path in paths:
        if path.is_dir():
            for file in path.rglob("*.log"):
                if relevant(file.name) and file.is_file():
                    with file.open(encoding="utf-8", errors="replace") as stream:
                        yield str(file), stream
        elif path.suffixes[-2:] == [".tar", ".gz"] or path.suffix == ".tgz":
            with tarfile.open(path, "r:gz") as archive:
                for member in archive:
                    if member.isfile() and relevant(member.name) and member.size <= 128 * 1024 * 1024:
                        stream = archive.extractfile(member)
                        if stream is not None:
                            with stream, TextIOWrapper(stream, encoding="utf-8", errors="replace") as text:
                                yield f"{path.name}/{member.name}", text
        elif path.suffix.lower() == ".zip":
            with zipfile.ZipFile(path) as archive:
                for member in archive.infolist():
                    if relevant(member.filename) and member.file_size <= 128 * 1024 * 1024:
                        with archive.open(member) as stream, TextIOWrapper(stream, encoding="utf-8", errors="replace") as text:
                            yield f"{path.name}/{member.filename}", text
        elif path.is_file():
            with path.open(encoding="utf-8", errors="replace") as text:
                yield str(path), text
        else:
            raise FileNotFoundError(f"No such diagnostic archive or directory: {path}")


def parse_stream(lines: Iterable[str], service: str) -> Iterator[Event]:
    for text in lines:
        mark = text.find("[TFQUEUE] ")
        if mark < 0:
            continue
        payload = text[mark + len("[TFQUEUE] "):].strip()
        if not payload:
            continue
        name, _, data = payload.partition(" ")
        # A snapshot can contain eight embedded job=... pairs under samples=.
        # Those are not the primary event identity; keep them searchable as text.
        attributes = dict(PAIR.findall(data.split(" samples=", 1)[0]))
        stamp = STAMP.match(text)
        yield Event(
            timestamp=stamp.group(1) if stamp else "unknown",
            service=service,
            event=name,
            job=attributes.get("job", ""),
            submission=attributes.get("submission", ""),
            worker=attributes.get("worker", attributes.get("caller", "")),
            line=payload[:1200],
        )


def collect(paths: list[Path]) -> list[Event]:
    events: list[Event] = []
    for filename, source in inputs(paths):
        service = next((s for s in SERVICES if s in filename.lower()), "unknown")
        events.extend(parse_stream(source, service))
    return sorted(events, key=lambda x: x.timestamp)


def summarize(events: list[Event], limit: int) -> list[str]:
    counts = Counter(e.event for e in events)
    report = [f"TFQUEUE events: {len(events)}", "Event types: " + (", ".join(f"{k}={v}" for k, v in counts.most_common()) or "none")]
    jobs: dict[str, list[Event]] = defaultdict(list)
    for e in events:
        if e.job:
            jobs[e.job].append(e)
    report.append(f"Correlated jobs: {len(jobs)}")
    incomplete = []
    terminal = {"COMPLETED", "WATCHDOG_EXPIRED", "DELIVERY_FINALIZED", "WORK_FINISHED", "COMPLETE_ACK"}
    for job, timeline in jobs.items():
        if any(e.event == "CLAIMED" for e in timeline) and not any(e.event in terminal for e in timeline):
            incomplete.append((job, timeline))
    report.append(f"Jobs claimed with no terminal event IN THESE LOGS: {len(incomplete)} (not proof of a stuck job)")
    for job, timeline in incomplete[:limit]:
        latest = timeline[-1]
        workers = ",".join(dict.fromkeys(e.worker for e in timeline if e.worker))
        report.append(f"  {job} last={latest.timestamp} event={latest.event} workers={workers}")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=Path, help=".tar.gz archives, .zip archives, extracted directories or .log files from nodes A/B")
    parser.add_argument("--id", help="filter by job or submission UUID (or a UUID prefix of at least 8 characters)")
    parser.add_argument("--limit", type=int, default=30, help="max lines of timeline and incomplete-job list")
    args = parser.parse_args()
    if args.id and len(args.id) < 8:
        parser.error("--id must contain at least eight characters")
    events = collect(args.paths)
    print("\n".join(summarize(events, max(0, min(1000, args.limit)))))
    if args.id:
        needle = args.id.lower()
        matches = [e for e in events if needle in e.job.lower() or needle in e.submission.lower() or needle in e.line.lower()]
        print(f"\nTimeline for {args.id} ({len(matches)} events, showing {args.limit}):")
        for e in matches[:max(0, min(1000, args.limit))]:
            print(f"{e.timestamp} [{e.service}] {e.line}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
