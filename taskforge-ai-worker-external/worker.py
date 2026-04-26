from __future__ import annotations

import argparse
import json
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from datetime import datetime, timezone
from typing import Any, Dict, Optional

from agent_core.runtime import AgentRuntime
from api_client import AgentApiClient, sleep_seconds
from config import (
    AGENT_API_BASE_URL,
    AGENT_CONCURRENCY,
    AGENT_IDLE_LOG_SECONDS,
    AGENT_POLL_SECONDS,
    AGENT_WORKER_ID,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_MODEL,
)
from log import log_event


def utc_ts() -> float:
    return datetime.now(timezone.utc).timestamp()


def run_id_for(job: Dict[str, Any]) -> str:
    payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
    return str(job.get("id") or job.get("runId") or payload.get("runId") or payload.get("conversationId") or "local-run")


def job_type_for(job: Dict[str, Any]) -> str:
    payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
    return str(job.get("type") or job.get("jobType") or payload.get("type") or payload.get("jobType") or "assistant_chat_turn")


def process_job(job: Dict[str, Any], runtime: Optional[AgentRuntime] = None) -> Dict[str, Any]:
    runtime = runtime or AgentRuntime()
    job_type = job_type_for(job)
    if job_type in {"assistant_chat_turn", "chat_turn", "agent_chat_turn"}:
        return runtime.handle_chat_turn(job)
    if job_type in {"agent_context_refresh", "context_refresh", "course_context_refresh"}:
        return runtime.refresh_context(job)
    if job_type in {"agent_scenario_run", "scenario_run"}:
        return runtime.run_scenario(job)
    raise RuntimeError(f"Unsupported agent job type: {job_type}")


def process_claimed_job(api: AgentApiClient, runtime: AgentRuntime, job: Dict[str, Any]) -> None:
    run_id = run_id_for(job)
    started = utc_ts()
    log_event("agent-run-claimed", run_id=run_id, job_type=job_type_for(job))
    try:
        api.heartbeat(run_id)
        api.append_step(run_id, {
            "kind": "context",
            "status": "running",
            "title": "AI подтягивает контекст",
            "summary": "Смотрю сообщение, память чата и доступные курсы.",
        })
        result = process_job(job, runtime=runtime)
        route = ((result.get("debug") or {}).get("route") or {}) if isinstance(result, dict) else {}
        if route:
            api.append_step(run_id, {
                "kind": "route",
                "status": "completed",
                "title": "Выбран сценарий",
                "scenarioId": route.get("scenario_id") or route.get("scenarioId"),
                "summary": route.get("reason") or "ScenarioRouter выбрал подходящий pipeline.",
            })
        elapsed = round(utc_ts() - started, 3)
        failed = result.get("status") == "failed"
        api.append_step(run_id, {
            "kind": "scenario_result",
            "status": "failed" if failed else result.get("status"),
            "scenarioId": result.get("scenario_id"),
            "elapsedSeconds": elapsed,
            "title": "AI-run завершился ошибкой" if failed else "Собран структурированный результат",
            "summary": result.get("assistant_message"),
        })
        if failed:
            api.fail_run(run_id, {
                "message": result.get("assistant_message") or "Реальный LLM-вызов не завершился.",
                "result": result,
                "retryable": True,
            })
            log_event("agent-run-failed", run_id=run_id, elapsed_seconds=elapsed, status=result.get("status"))
            return
        api.complete_run(run_id, result)
        log_event("agent-run-completed", run_id=run_id, elapsed_seconds=elapsed, status=result.get("status"))
    except Exception as exc:
        error = {"message": str(exc), "retryable": False, "jobType": job_type_for(job)}
        api.fail_run(run_id, error)
        log_event("agent-run-failed", run_id=run_id, error=str(exc))


def _process_job_in_slot(job: Dict[str, Any]) -> None:
    process_claimed_job(AgentApiClient(), AgentRuntime(), job)


def worker_loop() -> None:
    runtime = AgentRuntime()
    api = AgentApiClient()
    log_event(
        "agent-worker-started",
        worker_id=AGENT_WORKER_ID,
        api_configured=api.configured,
        api_base_url=AGENT_API_BASE_URL or None,
        llm_base_url=EXTERNAL_AI_BASE_URL,
        model=EXTERNAL_AI_MODEL,
        concurrency=AGENT_CONCURRENCY,
    )

    if AGENT_CONCURRENCY <= 1:
        last_idle_log = 0.0
        while True:
            try:
                job = api.claim_next()
                if not job:
                    now = utc_ts()
                    if now - last_idle_log >= AGENT_IDLE_LOG_SECONDS:
                        log_event(
                            "agent-worker-idle",
                            api_configured=api.configured,
                            message="No runnable agent run found; worker is connected and idle.",
                        )
                        last_idle_log = now
                    sleep_seconds(AGENT_POLL_SECONDS)
                    continue
                process_claimed_job(api, runtime, job)
            except Exception as exc:
                log_event("agent-worker-loop-error", error=str(exc))
                sleep_seconds(AGENT_POLL_SECONDS)

    last_idle_log = 0.0
    active = set()
    with ThreadPoolExecutor(max_workers=AGENT_CONCURRENCY, thread_name_prefix="agent-worker") as executor:
        while True:
            try:
                completed = {future for future in active if future.done()}
                for future in completed:
                    active.remove(future)
                    error = future.exception()
                    if error:
                        log_event("agent-worker-slot-error", error=str(error))

                claimed_any = False
                while len(active) < AGENT_CONCURRENCY:
                    job = api.claim_next()
                    if not job:
                        break
                    claimed_any = True
                    active.add(executor.submit(_process_job_in_slot, job))

                if not active and not claimed_any:
                    now = utc_ts()
                    if now - last_idle_log >= AGENT_IDLE_LOG_SECONDS:
                        log_event(
                            "agent-worker-idle",
                            api_configured=api.configured,
                            message="No runnable agent run found; worker is connected and idle.",
                        )
                        last_idle_log = now
                    sleep_seconds(AGENT_POLL_SECONDS)
                    continue

                if active:
                    wait(active, timeout=AGENT_POLL_SECONDS, return_when=FIRST_COMPLETED)
            except Exception as exc:
                log_event("agent-worker-loop-error", error=str(exc))
                sleep_seconds(AGENT_POLL_SECONDS)


def smoke_payload() -> Dict[str, Any]:
    return {
        "id": "smoke-run-1",
        "type": "assistant_chat_turn",
        "payload": {
            "conversationId": "smoke-conv-1",
            "rawText": "Проанализируй курс Python Start и найди переход к if",
            "courseCatalog": [{"id": "course-demo", "title": "Python Start", "assignmentCount": 3}],
            "courseContexts": [{
                "course": {"id": "course-demo", "title": "Python Start"},
                "assignments": [
                    {"id": "a1", "title": "Привет", "description": "Выведи текст на экран через print."},
                    {"id": "a2", "title": "Число", "description": "Считай число input и выведи его."},
                    {"id": "a3", "title": "Первое условие", "description": "Считай число. Если число больше 10, выведи Большое число."},
                ],
            }],
            "memory": {"summary": "Пользователь улучшает курс маленькими обучающими задачами."},
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="TaskForge external AI agent worker")
    parser.add_argument("--smoke", action="store_true", help="run one local smoke scenario and print JSON")
    parser.add_argument("--job-json", help="process one local job JSON string and print JSON")
    args = parser.parse_args()
    if args.smoke:
        result = process_job(smoke_payload())
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    if args.job_json:
        job = json.loads(args.job_json)
        result = process_job(job)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    worker_loop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
