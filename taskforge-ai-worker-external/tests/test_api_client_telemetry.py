import sys
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import api_client  # type: ignore


def test_complete_sends_worker_telemetry():
    captured = {}
    def fake_post(path, payload, expected=None, quiet=False):
        captured["path"] = path
        captured["payload"] = payload
        class Resp:
            status_code = 200
            text = ""
        return Resp()

    result = {
        "status": "passed",
        "score": 0.9,
        "__workerTelemetry": {"llm": {"totalTokens": 42}, "jobType": "assistant_chat_turn"},
        "assistantMessage": "ok",
        "actions": [],
    }
    with patch.object(api_client, "post", side_effect=fake_post):
        api_client.complete("job-1", result)
    assert captured["path"].endswith("/api/internal/ai/jobs/job-1/complete")
    assert captured["payload"]["telemetryJson"] is not None
    assert "__workerTelemetry" not in captured["payload"]["resultJson"]
