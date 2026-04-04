"""TaskForge AI Worker — thin orchestrator.

All business logic lives in dedicated modules:
  config, log, text_utils, api_client, runners, payload,
  validators, mutation, ollama, prompt_builder, reviews,
  batch_pipeline, fallbacks, repair.

This file contains only the job-routing dispatcher and the main poll loop.
"""

import time
from typing import Any, Dict

from config import (
    API_BASE,
    API_KEY,
    WORKER_ID,
    OLLAMA_MODEL,
    POLL_INTERVAL,
    MAX_JOB_RETRIES,
    RETRYABLE_STAGE_DELAY_SECONDS,
)
from log import log, logger
from api_client import pull_job, heartbeat, complete, fail
from payload import parse_payload, sanitize_result_payload
from validators import run_self_check
from ollama import call_ollama
from prompt_builder import (
    build_prompt,
    build_course_profile_prompt,
    build_gap_analysis_prompt,
    build_batch_plan_prompt,
    build_brief_prompt,
    build_brief_repair_prompt,
    build_reference_pack_prompt,
    build_batch_review_prompt,
)
from reviews import (
    run_structural_review,
    fallback_pedagogy_review,
    run_style_review,
    run_similarity_review,
    run_runtime_review,
    run_test_strength_review,
    run_brief_review,
    run_batch_context_review,
)
from batch_pipeline import (
    run_batch_review,
    run_student_journey_review,
    run_batch_publish_prepare,
    run_batch_planner_feedback,
)
from fallbacks import (
    fallback_result,
    fallback_course_profile,
    fallback_gap_analysis,
)
from repair import try_improve_generation, fallback_repair_result


class RetryableStageError(RuntimeError):
    def __init__(self, message: str, retry_delay_seconds: int | None = None):
        super().__init__(message)
        self.retry_delay_seconds = retry_delay_seconds or RETRYABLE_STAGE_DELAY_SECONDS


def _preview(value: Any, limit: int = 120) -> str:
    if value is None:
        return "-"
    text = str(value).replace("\n", " ").replace("\r", " ").strip()
    if len(text) <= limit:
        return text
    return text[: limit - 3] + "..."


def _job_context(job: Dict[str, Any], payload: Dict[str, Any] | None = None) -> str:
    payload = payload or {}
    parts = [
        f"job={job.get('id')}",
        f"type={job.get('type')}",
    ]
    stage_code = job.get("stageCode") or payload.get("stageCode")
    stage_label = job.get("stageLabel") or payload.get("stageLabel")
    if stage_code or stage_label:
        parts.append(f"stage={stage_code or '-'}")
        parts.append(f"stageLabel={_preview(stage_label, 80)}")
    target_type = job.get("targetEntityType")
    target_id = job.get("targetEntityId")
    if target_type or target_id:
        parts.append(f"target={target_type or '-'}:{target_id or '-'}")
    course_id = job.get("courseId") or payload.get("courseId")
    if course_id:
        parts.append(f"course={course_id}")
    for key, label in (("batchId", "batch"), ("batchItemId", "item"), ("draftId", "draft")):
        value = payload.get(key)
        if value:
            parts.append(f"{label}={value}")
    prompt = payload.get("prompt")
    if isinstance(prompt, str) and prompt.strip():
        parts.append(f"prompt={_preview(prompt, 90)}")
    return " ".join(parts)


def _result_summary(result: Dict[str, Any] | None) -> str:
    if not isinstance(result, dict):
        return f"type={type(result).__name__}"
    keys = sorted(result.keys())[:12]
    parts = [f"keys={keys}"]
    status = result.get("status")
    if status is not None:
        parts.append(f"status={status}")
    score = result.get("score")
    if score is not None:
        parts.append(f"score={score}")
    summary = result.get("summary") or result.get("message") or result.get("title")
    if summary is not None:
        parts.append(f"summary={_preview(summary, 100)}")
    return " ".join(parts)


def _log_stage(event: str, job: Dict[str, Any], payload: Dict[str, Any] | None = None, **extra: Any) -> None:
    parts = [event, _job_context(job, payload)]
    for key, value in extra.items():
        if value is None:
            continue
        parts.append(f"{key}={_preview(value, 160)}")
    log(*parts)


# ── Job dispatcher ────────────────────────────────────

def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()
    _log_stage("stage-start", job, payload, payload_keys=sorted(payload.keys())[:20])

    def _finish(result: Dict[str, Any]) -> Dict[str, Any]:
        _log_stage("stage-done", job, payload, result=_result_summary(result))
        return result

    def _ollama_stage(prompt_builder, fallback_fn=None, sanitize=True, allow_fallback=True):
        builder_name = getattr(prompt_builder, "__name__", "prompt_builder")
        _log_stage("stage-prepare-prompt", job, payload, builder=builder_name)
        prompt = prompt_builder(job, payload)
        _log_stage("stage-prompt-ready", job, payload, builder=builder_name, prompt_len=len(prompt))
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            logger.warning(f"ollama failed for {job_type}: {ex} [{_job_context(job, payload)}]")
            if not allow_fallback:
                raise RetryableStageError(f"{job_type} failed: {ex}") from ex
            result = fallback_fn(payload, job) if fallback_fn else fallback_result(job)
            _log_stage("stage-fallback-result", job, payload, builder=builder_name, fallback_type=type(result).__name__)
        if sanitize:
            result = sanitize_result_payload(job_type, payload, result)
            _log_stage("stage-sanitized", job, payload, result=_result_summary(result))
        return result

    # ── Profiling / gap ───────────────────────────────
    if job_type == "assignment_course_profile_build":
        return _finish(_ollama_stage(build_course_profile_prompt, fallback_fn=fallback_course_profile))
    if job_type == "assignment_gap_analysis":
        return _finish(_ollama_stage(build_gap_analysis_prompt, fallback_fn=fallback_gap_analysis))

    # ── Batch plan / replan ───────────────────────────
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        return _finish(_ollama_stage(build_batch_plan_prompt, allow_fallback=False))

    # ── Reference pack ────────────────────────────────
    if job_type == "assignment_reference_pack_build":
        return _finish(_ollama_stage(build_reference_pack_prompt, allow_fallback=False))

    # ── Brief generate ────────────────────────────────
    if job_type == "assignment_brief_generate":
        return _finish(_ollama_stage(build_brief_prompt, allow_fallback=False))

    # ── Validate draft ────────────────────────────────
    if job_type == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        return _finish(validation)

    # ── Brief review ──────────────────────────────────
    if job_type == "assignment_brief_review":
        return _finish(run_brief_review(payload, job))

    # ── Brief repair ──────────────────────────────────
    if job_type == "assignment_brief_repair":
        return _finish(_ollama_stage(build_brief_repair_prompt, allow_fallback=False))

    # ── Per-stage reviews (deterministic) ─────────────
    if job_type == "assignment_structural_review":
        return _finish(run_structural_review(payload, job))
    if job_type == "assignment_pedagogy_review":
        return _finish(fallback_pedagogy_review(payload, job))
    if job_type == "assignment_style_review":
        return _finish(run_style_review(payload, job))
    if job_type == "assignment_similarity_review":
        return _finish(run_similarity_review(payload, job))
    if job_type == "assignment_batch_context_review":
        return _finish(run_batch_context_review(payload, job))
    if job_type == "assignment_test_strength_review":
        return _finish(run_test_strength_review(payload, job))
    if job_type == "assignment_runtime_review":
        return _finish(run_runtime_review(payload, job))

    # ── Batch-level stages ────────────────────────────
    if job_type == "assignment_student_journey_review":
        return _finish(run_student_journey_review(payload, job))
    if job_type == "assignment_batch_publish_prepare":
        return _finish(run_batch_publish_prepare(payload, job))
    if job_type == "assignment_batch_planner_feedback":
        return _finish(run_batch_planner_feedback(payload, job))
    if job_type == "assignment_batch_review":
        prompt = build_batch_review_prompt(job, payload)
        _log_stage("stage-prompt-ready", job, payload, builder="build_batch_review_prompt", prompt_len=len(prompt))
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            logger.warning(f"batch review ollama failed: {ex} [{_job_context(job, payload)}]")
            result = run_batch_review(payload, job)
            _log_stage("stage-fallback-result", job, payload, builder="build_batch_review_prompt", fallback="run_batch_review")
        return _finish(result)

    # ── Draft repair ──────────────────────────────────
    if job_type == "assignment_repair":
        prompt = build_prompt(job, payload)
        _log_stage("stage-prompt-ready", job, payload, builder="build_prompt", prompt_len=len(prompt))
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            logger.warning(f"repair ollama failed: {ex} [{_job_context(job, payload)}]")
            return _finish(fallback_repair_result(payload, job))
        repaired_draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
        if isinstance(repaired_draft, dict):
            result["draftValidation"] = run_self_check(repaired_draft)
            validation = result.get("draftValidation") if isinstance(result.get("draftValidation"), dict) else {}
            _log_stage("stage-repair-validation", job, payload, validation=_result_summary(validation))
        return _finish(result)

    # ── Default: generic generation ───────────────────
    prompt = build_prompt(job, payload)
    _log_stage("stage-prompt-ready", job, payload, builder="build_prompt", prompt_len=len(prompt))
    try:
        result = call_ollama(prompt)
    except Exception as ex:
        logger.warning(f"ollama failed for {job_type}: {ex} [{_job_context(job, payload)}]")
        raise RetryableStageError(f"{job_type} failed: {ex}") from ex
    if job_type.startswith("assignment_generate") and payload.get("enableSelfCheck", True):
        before_keys = sorted(result.keys())[:12] if isinstance(result, dict) else []
        result = try_improve_generation(job, payload, result)
        after_keys = sorted(result.keys())[:12] if isinstance(result, dict) else []
        _log_stage("stage-self-check-finished", job, payload, before_keys=before_keys, after_keys=after_keys)
    return _finish(result)


# ── Main poll loop ────────────────────────────────────

def main():
    if not API_KEY:
        raise RuntimeError("TASKFORGE_INTERNAL_KEY is not configured")
    log(f"started api={API_BASE} worker={WORKER_ID} model={OLLAMA_MODEL} poll={POLL_INTERVAL}s")
    while True:
        loop_started = time.time()
        job = None
        job_id = None
        try:
            job = pull_job()
            if not job:
                time.sleep(POLL_INTERVAL)
                continue
            job_id = job["id"]
            heartbeat(job_id)
            log(f"processing {_job_context(job)}")
            result = process_job(job)
            log(f"completing {_job_context(job)} result={_result_summary(result)}")
            complete(job_id, result)
            elapsed = int((time.time() - loop_started) * 1000)
            log(f"done {_job_context(job)} elapsed_ms={elapsed}")
        except KeyboardInterrupt:
            raise
        except RetryableStageError as ex:
            logger.warning(f"retryable stage error: {ex}")
            if job_id:
                retry_count = int(job.get("retryCount") or job.get("RetryCount") or 0) if isinstance(job, dict) else 0
                retryable = retry_count < max(1, MAX_JOB_RETRIES)
                delay = getattr(ex, "retry_delay_seconds", RETRYABLE_STAGE_DELAY_SECONDS)
                try:
                    fail(job_id, str(ex), retryable=retryable, retry_delay_seconds=delay)
                except Exception as fail_ex:
                    logger.error(f"fail() call also failed: {fail_ex}")
            time.sleep(POLL_INTERVAL)
        except Exception as ex:
            logger.error(f"loop error: {type(ex).__name__}: {ex}", exc_info=True)
            if job_id:
                try:
                    fail(job_id, f"{type(ex).__name__}: {ex}")
                except Exception as fail_ex:
                    logger.error(f"fail() call also failed: {fail_ex}")
            time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
