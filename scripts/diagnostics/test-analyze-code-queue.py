#!/usr/bin/env python3
"""Offline parser regression tests for code-queue diagnostic correlation."""
import importlib.util
from io import StringIO
from pathlib import Path
import tempfile
import tarfile
import unittest

spec = importlib.util.spec_from_file_location("queue_report", Path(__file__).with_name("analyze-code-queue.py"))
report = importlib.util.module_from_spec(spec)
import sys
sys.modules[spec.name] = report
spec.loader.exec_module(report)


class QueueReportTests(unittest.TestCase):
    def test_multiline_dotnet_logs_correlate_by_job_and_submission(self):
        payload = StringIO("""2026-10-19T20:00:00.123Z info: TaskForge.Worker.Worker[0]
2026-10-19T20:00:00.124Z       [TFQUEUE] CLAIM_ACK worker=B/container/123 job=abcdefab-1234 submission=bcdefabc-1234 attempt=1
2026-10-19T20:00:01.125Z       [TFQUEUE] WORK_FAILED worker=B/container/123 job=abcdefab-1234 submission=bcdefabc-1234 stage=runner
2026-10-19T20:00:02.126Z       ordinary unrelated log
""")
        events = list(report.parse_stream(payload, "execution-worker"))
        self.assertEqual(2, len(events))
        self.assertEqual("CLAIM_ACK", events[0].event)
        self.assertEqual("abcdefab-1234", events[0].job)
        self.assertEqual("B/container/123", events[0].worker)
        self.assertEqual("WORK_FAILED", events[1].event)

    def test_reads_tar_gz_without_extracting(self):
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "source.log"
            log.write_text("2026-10-19T20:00:00.000Z [TFQUEUE] CLAIMED worker=A/c/1 job=abcdefab submission=bcdefabc\n")
            archive = Path(folder) / "diagnostics.tar.gz"
            with tarfile.open(archive, "w:gz") as tar:
                tar.add(log, "taskforge/containers/taskforge-prod-execution-api-1.log")
            entries = report.collect([archive])
            self.assertEqual(1, len(entries))
            self.assertEqual("execution-api", entries[0].service)

    def test_snapshot_embedded_samples_do_not_overwrite_event_identity(self):
        events = list(report.parse_stream([
            "2026-10-19T20:00:00Z [TFQUEUE] SNAPSHOT_ALERT api=B queued=2 samples=job=abcdefab;sub=ab12 | job=12345678;sub=ff23"
        ], "execution-api"))
        self.assertEqual("", events[0].job)
        self.assertIn("12345678", events[0].line)

    def test_incomplete_claim_only_is_called_out_as_not_proof(self):
        claims = list(report.parse_stream(["2026-10-19T20:00:00Z [TFQUEUE] CLAIMED worker=A job=abcdefab submission=bcdefabc"], "execution-api"))
        self.assertTrue(any("not proof" in item for item in report.summarize(claims, 10)))


if __name__ == "__main__":
    unittest.main()
