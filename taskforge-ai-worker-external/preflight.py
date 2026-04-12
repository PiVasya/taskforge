"""Runtime preflight checks for the external AI worker.

Useful before local testing and as a lightweight container healthcheck.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import tempfile
from dataclasses import dataclass, asdict
from typing import Any

from config import (
    ACTIVE_PROVIDER,
    API_BASE,
    API_KEY,
    CHAT_STRICT_MODE,
    DRAFT_SUBSTAGES,
    EXTERNAL_AI_API_KEY,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_MODEL,
    OPENROUTER_PLUGINS,
    POLL_INTERVAL,
    RUNNER_CONTAINER_IMAGE,
    RUNNER_CONTAINER_RUNTIME,
    SANDBOX_RUNNER,
    SANDBOX_RUNNER_MODE,
    SCHEMA_VALIDATION_MODE,
    STRUCTURED_OUTPUTS,
)


@dataclass
class CheckMessage:
    level: str
    code: str
    message: str


def _is_writable_dir(path: str) -> bool:
    try:
        os.makedirs(path, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=path, delete=True):
            pass
        return True
    except Exception:
        return False


def collect_messages(env: dict[str, str] | None = None) -> list[CheckMessage]:
    env = env or os.environ
    messages: list[CheckMessage] = []

    if not API_BASE:
        messages.append(CheckMessage("error", "api_base_missing", "TASKFORGE_API_BASE is empty."))
    if not API_KEY:
        messages.append(CheckMessage("error", "internal_key_missing", "TASKFORGE_INTERNAL_KEY is empty."))

    if ACTIVE_PROVIDER != "ollama":
        if not EXTERNAL_AI_BASE_URL:
            messages.append(CheckMessage("error", "external_base_url_missing", "TASKFORGE_EXTERNAL_AI_BASE_URL is empty."))
        if not EXTERNAL_AI_MODEL:
            messages.append(CheckMessage("error", "external_model_missing", "TASKFORGE_EXTERNAL_AI_MODEL is empty."))
        if not EXTERNAL_AI_API_KEY:
            messages.append(CheckMessage("error", "external_api_key_missing", "TASKFORGE_EXTERNAL_AI_API_KEY is empty."))

    if SCHEMA_VALIDATION_MODE not in {"strict", "soft", "off"}:
        messages.append(CheckMessage("error", "schema_validation_invalid", f"Unsupported TASKFORGE_AI_SCHEMA_VALIDATION={SCHEMA_VALIDATION_MODE!r}."))

    if POLL_INTERVAL <= 0:
        messages.append(CheckMessage("error", "poll_interval_invalid", "POLL_INTERVAL_SECONDS must be > 0."))

    if SANDBOX_RUNNER:
        if SANDBOX_RUNNER_MODE not in {"process", "container"}:
            messages.append(CheckMessage("error", "sandbox_mode_invalid", f"Unsupported TASKFORGE_AI_SANDBOX_MODE={SANDBOX_RUNNER_MODE!r}."))
        if SANDBOX_RUNNER_MODE == "container":
            if RUNNER_CONTAINER_RUNTIME != "docker":
                messages.append(CheckMessage("error", "container_runtime_invalid", f"Unsupported TASKFORGE_AI_RUNNER_RUNTIME={RUNNER_CONTAINER_RUNTIME!r}."))
            elif shutil.which("docker") is None:
                messages.append(CheckMessage("error", "docker_missing", "Docker CLI is not available, but container sandbox mode is enabled."))
            if not RUNNER_CONTAINER_IMAGE:
                messages.append(CheckMessage("error", "container_image_missing", "TASKFORGE_AI_RUNNER_CONTAINER_IMAGE is empty."))

    log_dir = env.get("TASKFORGE_AI_LOG_DIR", "/app/logs")
    if not _is_writable_dir(log_dir):
        messages.append(CheckMessage("error", "log_dir_unwritable", f"Log directory is not writable: {log_dir}"))

    if STRUCTURED_OUTPUTS and ACTIVE_PROVIDER == "ollama":
        messages.append(CheckMessage("warning", "structured_outputs_with_ollama", "Structured outputs are enabled, but Ollama fallback may ignore json_schema."))
    if DRAFT_SUBSTAGES and not STRUCTURED_OUTPUTS:
        messages.append(CheckMessage("warning", "draft_substages_without_structured_outputs", "Draft substages are enabled without structured outputs; repair rate may increase."))
    if not CHAT_STRICT_MODE:
        messages.append(CheckMessage("warning", "chat_strict_disabled", "TASKFORGE_AI_CHAT_STRICT_MODE is disabled."))
    if ACTIVE_PROVIDER != "ollama" and "openrouter.ai" in EXTERNAL_AI_BASE_URL and not OPENROUTER_PLUGINS:
        messages.append(CheckMessage("warning", "openrouter_plugins_empty", "OpenRouter is enabled without plugins; JSON healing/compression are disabled."))

    return messages


def build_report(env: dict[str, str] | None = None) -> dict[str, Any]:
    messages = collect_messages(env)
    errors = [asdict(m) for m in messages if m.level == "error"]
    warnings = [asdict(m) for m in messages if m.level == "warning"]
    return {
        "ok": not errors,
        "provider": ACTIVE_PROVIDER,
        "model": EXTERNAL_AI_MODEL if ACTIVE_PROVIDER != "ollama" else env.get("OLLAMA_MODEL", "qwen3:14b") if env else "qwen3:14b",
        "sandbox": {
            "enabled": SANDBOX_RUNNER,
            "mode": SANDBOX_RUNNER_MODE,
            "runtime": RUNNER_CONTAINER_RUNTIME,
            "image": RUNNER_CONTAINER_IMAGE,
        },
        "features": {
            "structuredOutputs": STRUCTURED_OUTPUTS,
            "schemaValidation": SCHEMA_VALIDATION_MODE,
            "chatStrictMode": CHAT_STRICT_MODE,
            "draftSubstages": DRAFT_SUBSTAGES,
        },
        "errors": errors,
        "warnings": warnings,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="External AI worker preflight checks")
    parser.add_argument("--json", action="store_true", help="Print JSON report")
    parser.add_argument("--healthcheck", action="store_true", help="Emit compact output suitable for container healthchecks")
    args = parser.parse_args()

    report = build_report()
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    elif args.healthcheck:
        if report["ok"]:
            print("ok")
        else:
            first = report["errors"][0]["code"] if report["errors"] else "unknown"
            print(f"fail:{first}")
    else:
        print("External worker preflight")
        print(f"- provider: {report['provider']}")
        print(f"- model: {report['model']}")
        print(f"- structured outputs: {report['features']['structuredOutputs']}")
        print(f"- schema validation: {report['features']['schemaValidation']}")
        print(f"- sandbox: enabled={report['sandbox']['enabled']} mode={report['sandbox']['mode']}")
        for item in report["warnings"]:
            print(f"WARN {item['code']}: {item['message']}")
        for item in report["errors"]:
            print(f"ERROR {item['code']}: {item['message']}")
        print("Preflight: OK" if report["ok"] else "Preflight: FAILED")
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
