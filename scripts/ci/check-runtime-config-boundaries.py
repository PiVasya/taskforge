#!/usr/bin/env python3
"""Static checks for deployment/configuration contracts only.

Business behavior belongs in executable tests. This file intentionally checks only
Compose/.env wiring where the configuration text itself is the contract.
"""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("runtime config boundary failed: " + message)


def read(relative: str) -> str:
    path = ROOT / relative
    require(path.is_file(), f"missing {relative}")
    return path.read_text(encoding="utf-8")


for environment in ("dev", "prod"):
    core = read(f"deploy/{environment}/compose/20-core-services.yaml")
    env = read(f"deploy/{environment}/.env.example")

    for marker in (
        "AiAccounts__UnlimitedTaskEnergy:",
        "AiAccounts__UnlimitedTaskRateLimit:",
        "AiAccounts__UnlimitedTaskAttempts:",
        "AiAccounts__IgnoreTaskAttemptTimeLimits:",
        "AiAccounts__TaskRateLimitMultiplier:",
    ):
        require(core.count(marker) >= 2, f"{environment}: tasks/solutions AI policy wiring missing {marker}")
    require(core.count("AiAccounts__UnlimitedLoginRateLimit:") >= 1,
            f"{environment}: identity AI login policy wiring missing")

    for marker in (
        "AI_ACCOUNTS_UNLIMITED_TASK_ENERGY=true",
        "AI_ACCOUNTS_UNLIMITED_TASK_RATE_LIMIT=true",
        "AI_ACCOUNTS_UNLIMITED_TASK_ATTEMPTS=true",
        "AI_ACCOUNTS_IGNORE_TASK_ATTEMPT_TIME_LIMITS=true",
        "AI_ACCOUNTS_UNLIMITED_LOGIN_RATE_LIMIT=true",
        "AI_ACCOUNTS_TASK_RATE_LIMIT_MULTIPLIER=20",
    ):
        require(marker in env, f"{environment}: .env.example missing {marker}")

    execution = read(f"deploy/{environment}/compose/30-execution.yaml")
    require("Judge__RunnerAttempts: ${JUDGE_RUNNER_ATTEMPTS:-3}" in execution,
            f"{environment}: bounded runner retry setting missing")
    csharp = execution.split("  csharp-runner:", 1)[-1].split("\n  cpp-runner:", 1)[0]
    for marker in ("soft: 4096", "hard: 4096", "${CSHARP_RUNNER_MEM_LIMIT:-1024m}"):
        require(marker in csharp, f"{environment}: C# runner resource boundary missing {marker}")
    for marker in ("CSHARP_RUNNER_MEM_LIMIT=1024m", "JUDGE_RUNNER_ATTEMPTS=3"):
        require(marker in env, f"{environment}: .env.example missing {marker}")

    require("TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS:-1}" in read(f"deploy/{environment}/compose/10-apps-gateway.yaml")
            or "TASKFORGE_DEBUG_LOGS" in core,
            f"{environment}: debug logging wiring unexpectedly disappeared")

print("Runtime configuration boundaries OK")
