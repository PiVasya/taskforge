"""Logging setup for the AI worker."""

import logging
from typing import Any

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [worker] %(levelname)s %(message)s",
    datefmt="%H:%M:%S",
)

logger: logging.Logger = logging.getLogger("taskforge-ai-worker")


def log(*parts: Any) -> None:
    """Info-level log with space-joined parts."""
    logger.info(" ".join(str(p) for p in parts))


def log_debug(*parts: Any) -> None:
    """Debug-level log with space-joined parts."""
    logger.debug(" ".join(str(p) for p in parts))
