from __future__ import annotations

import argparse
import sys

from config import (
    AGENT_API_BASE_URL,
    EXTERNAL_AI_API_KEY,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_MODEL,
)
from scenarios.registry import build_default_registry


def main() -> int:
    parser = argparse.ArgumentParser(description="TaskForge external AI worker preflight")
    parser.add_argument("--healthcheck", action="store_true")
    parser.add_argument("--strict", action="store_true", help="fail when TASKFORGE_EXTERNAL_AI_API_KEY is empty")
    args = parser.parse_args()

    if not EXTERNAL_AI_BASE_URL:
        print("TASKFORGE_EXTERNAL_AI_BASE_URL is empty", file=sys.stderr)
        return 1
    if not EXTERNAL_AI_MODEL:
        print("TASKFORGE_EXTERNAL_AI_MODEL is empty", file=sys.stderr)
        return 1
    if args.strict and not EXTERNAL_AI_API_KEY:
        print("TASKFORGE_EXTERNAL_AI_API_KEY is empty", file=sys.stderr)
        return 1

    registry = build_default_registry()
    if not registry.ids():
        print("Scenario registry is empty", file=sys.stderr)
        return 1

    print(
        "TaskForge AI worker configured: "
        f"base_url={EXTERNAL_AI_BASE_URL}, model={EXTERNAL_AI_MODEL}, "
        f"api_key_set={bool(EXTERNAL_AI_API_KEY)}, agent_api_configured={bool(AGENT_API_BASE_URL)}, "
        f"scenarios={','.join(registry.ids())}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
