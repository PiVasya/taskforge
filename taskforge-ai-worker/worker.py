"""TaskForge AI Worker — thin orchestrator.

All business logic lives in dedicated modules:
  config, log, text_utils, api_client, runners, payload,
  validators, mutation, ollama, prompt_builder, reviews,
  batch_pipeline, fallbacks, repair.

This file contains only the job-routing dispatcher and the main poll loop.
"""

import time
from typing import Any, Dict

from config import API_BASE, API_KEY, WORKER_ID, OLLAMA_MODEL, POLL_INTERVAL
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
    build_reference_pack_prompt,
    build_batch_review_prompt,
    build_student_journey_prompt,
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


# ── Job dispatcher ────────────────────────────────────

def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()

    def _ollama_stage(prompt_builder, fallback_fn=None, sanitize=True):
        prompt = prompt_builder(job, payload)
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            logger.warning(f"ollama failed for {job_type}: {ex}")
            result = fallback_fn(payload, job) if fallback_fn else fallback_result(job)
        if sanitize:
            result = sanitize_result_payload(job_type, payload, result)
        return result

    # ── Profiling / gap ───────────────────────────────
    if job_type == "assignment_course_profile_build":
        return _ollama_stage(build_course_profile_prompt, fallback_fn=fallback_course_profile)
    if job_type == "assignment_gap_analysis":
        return _ollama_stage(build_gap_analysis_prompt, fallback_fn=fallback_gap_analysis)

    # ── Batch plan / replan ───────────────────────────
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        return _ollama_stage(build_batch_plan_prompt)

    # ── Reference pack ────────────────────────────────
    if job_type == "assignment_reference_pack_build":
        return _ollama_stage(build_reference_pack_prompt)

    # ── Brief generate ────────────────────────────────
    if job_type == "assignment_brief_generate":
        return _ollama_stage(build_brief_prompt)

    # ── Validate draft ────────────────────────────────
    if job_type == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        return validation

    # ── Brief review ──────────────────────────────────
    if job_type == "assignment_brief_review":
        return run_brief_review(payload, job)

    # ── Brief repair ──────────────────────────────────
    if job_type == "assignment_brief_repair":
        return fallback_result(job)

    # ── Per-stage reviews (deterministic) ─────────────
    if job_type == "assignment_structural_review":
        return run_structural_review(payload, job)
    if job_type == "assignment_pedagogy_review":
        return fallback_pedagogy_review(payload, job)
    if job_type == "assignment_style_review":
        return run_style_review(payload, job)
    if job_type == "assignment_similarity_review":
        return run_similarity_review(payload, job)
    if job_type == "assignment_batch_context_review":
        return run_batch_context_review(payload, job)
    if job_type == "assignment_test_strength_review":
        return run_test_strength_review(payload, job)
    if job_type == "assignment_runtime_review":
        return run_runtime_review(payload, job)

    # ── Batch-level stages ────────────────────────────
    if job_type == "assignment_student_journey_review":
        return run_student_journey_review(payload, job)
    if job_type == "assignment_batch_publish_prepare":
        return run_batch_publish_prepare(payload, job)
    if job_type == "assignment_batch_planner_feedback":
        return run_batch_planner_feedback(payload, job)
    if job_type == "assignment_batch_review":
        prompt = build_batch_review_prompt(job, payload)
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            logger.warning(f"batch review ollama failed: {ex}")
            result = run_batch_review(payload, job)
        return result

    # ── Draft repair ──────────────────────────────────
    if job_type == "assignment_repair":
        prompt = build_prompt(job, payload)
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            logger.warning(f"repair ollama failed: {ex}")
            return fallback_repair_result(payload, job)
        repaired_draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
        if isinstance(repaired_draft, dict):
            result["draftValidation"] = run_self_check(repaired_draft)
        return result

    # ── Default: generic generation ───────────────────
    prompt = build_prompt(job, payload)
    try:
        result = call_ollama(prompt)
    except Exception as ex:
        logger.warning(f"ollama failed for {job_type}: {ex}")
        return fallback_result(job)
    if job_type.startswith("assignment_generate") and payload.get("enableSelfCheck", True):
        result = try_improve_generation(job, payload, result)
    log("process_job <<< result ready", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
    return result


# ── Main poll loop ────────────────────────────────────

def main():
    if not API_KEY:
        raise RuntimeError("TASKFORGE_INTERNAL_KEY is not configured")
    log(f"started api={API_BASE} worker={WORKER_ID} model={OLLAMA_MODEL} poll={POLL_INTERVAL}s")
    while True:
        loop_started = time.time()
        try:
            job = pull_job()
            if not job:
                time.sleep(POLL_INTERVAL)
                continue
            job_id = job["id"]
            job_type = job.get("type", "?")
            heartbeat(job_id)
            log(f"processing {job_type} job={job_id}")
            result = process_job(job)
            complete(job_id, result)
            elapsed = int((time.time() - loop_started) * 1000)
            log(f"done {job_type} job={job_id} in {elapsed}ms")
        except KeyboardInterrupt:
            raise
        except Exception as ex:
            logger.error(f"loop error: {type(ex).__name__}: {ex}", exc_info=True)
            time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
