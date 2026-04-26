from __future__ import annotations

import json
import os
from datetime import datetime, timezone
from typing import Any

from config import LOG_FILE


def preview_text(value: str | None, limit: int = 500) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if len(text) <= limit else text[:limit] + "..."


def log_event(event: str, **payload: Any) -> None:
    os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
    record = {"ts": datetime.now(timezone.utc).isoformat(), "event": event, **payload}
    line = json.dumps(record, ensure_ascii=False)
    print(line, flush=True)
    with open(LOG_FILE, "a", encoding="utf-8") as f:
        f.write(line + "\n")
