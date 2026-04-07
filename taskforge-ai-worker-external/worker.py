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
    PLANNER_FALLBACK_AFTER_RETRY_COUNT,
    COURSE_PROFILE_TIMEOUT,
    GAP_ANALYSIS_TIMEOUT,
    BATCH_PLAN_TIMEOUT,
    BRIEF_TIMEOUT,
    REFERENCE_PACK_TIMEOUT,
    GENERATION_TIMEOUT,
    COURSE_PROFILE_NUM_PREDICT,
    GAP_ANALYSIS_NUM_PREDICT,
    BATCH_PLAN_NUM_PREDICT,
    BRIEF_NUM_PREDICT,
    REFERENCE_PACK_NUM_PREDICT,
    GENERATION_NUM_PREDICT,
    COURSE_PROFILE_MAX_RETRIES,
    GAP_ANALYSIS_MAX_RETRIES,
    BATCH_PLAN_MAX_RETRIES,
    REFERENCE_PACK_MAX_RETRIES,
    BRIEF_MAX_RETRIES,
)
from log import log, logger
from api_client import pull_job, heartbeat, complete, fail
from payload import parse_payload, sanitize_result_payload, _derive_course_style_title
from validators import run_self_check
from ollama import call_ollama, OllamaCallConfig
from prompt_builder import (
    build_prompt,
    build_course_profile_prompt,
    build_gap_analysis_prompt,
    build_batch_plan_prompt,
    build_brief_prompt,
    build_brief_repair_prompt,
    build_reference_pack_prompt,
    build_batch_review_prompt,
    build_stage_schema_repair_prompt,
    build_draft_generate_prompt,
    build_draft_body_generate_prompt,
    build_draft_title_generate_prompt,
    build_draft_title_repair_prompt,
    build_draft_course_style_analysis_prompt,
    build_draft_generation_spec_prompt,
    build_draft_content_plan_prompt,
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


def _job_retry_count(job: Dict[str, Any]) -> int:
    try:
        return int(job.get("retryCount") or job.get("RetryCount") or 0)
    except Exception:
        return 0


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


def _stage_required_keys(job_type: str) -> tuple[list[str], list[str]]:
    if job_type == "assignment_course_profile_build":
        return (["canonicalRequest", "courseDigest", "courseProfile"], ["summary", "decisionSummary"])
    if job_type == "assignment_gap_analysis":
        return (["gapAnalysis", "coverage"], ["summary", "decisionSummary", "canonicalRequest", "courseDigest"])
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        return (["plan"], ["canonicalRequest", "coverage", "summary", "decisionSummary"])
    if job_type == "assignment_reference_pack_build":
        return (["stylePack", "policyPack", "exemplarPack"], ["generationHints", "signals"])
    if job_type in {"assignment_brief_generate", "assignment_brief_repair"}:
        return (["titleHint", "generationPrompt", "targetSkill"], ["summary", "difficultyTarget"])
    if job_type == "assignment_generate_from_text":
        return (["draft"], ["summary", "decisionSummary", "draftValidation"])
    if job_type == "assignment_repair":
        return (["draft"], ["repairSummary", "draftValidation"])
    return ([], [])


def _schema_missing_reason(job_type: str, payload: Dict[str, Any], result: Dict[str, Any]) -> str | None:
    required, _ = _stage_required_keys(job_type)
    for key in required:
        if key == "plan":
            plan = result.get("plan") if isinstance(result.get("plan"), dict) else {}
            tasks = plan.get("tasks") if isinstance(plan.get("tasks"), list) else None
            if not tasks:
                return "missing plan.tasks"
            continue
        if key == "gapAnalysis" and not isinstance(result.get("gapAnalysis"), dict):
            return "missing gapAnalysis"
        if key == "coverage" and not isinstance(result.get("coverage"), dict):
            return "missing coverage"
        if key == "courseDigest" and not isinstance(result.get("courseDigest"), dict):
            return "missing courseDigest"
        if key == "courseProfile" and not isinstance(result.get("courseProfile"), dict):
            return "missing courseProfile"
        if key == "draft":
            draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
            if not isinstance(draft, dict):
                return "missing draft"
            if not str(draft.get("title") or "").strip():
                return "missing draft.title"
            if not str(draft.get("assignmentType") or payload.get("assignmentType") or "").strip():
                return "missing draft.assignmentType"
            if not str(draft.get("description") or "").strip():
                return "missing draft.description"
            assignment_type = str(draft.get("assignmentType") or payload.get("assignmentType") or "").strip().lower()
            if assignment_type == "code-test":
                if not isinstance(draft.get("publicTests"), list) or not draft.get("publicTests"):
                    return "missing draft.publicTests"
                if not isinstance(draft.get("hiddenTests"), list) or not draft.get("hiddenTests"):
                    return "missing draft.hiddenTests"
                if not str(draft.get("referenceSolutionPython") or "").strip():
                    return "missing draft.referenceSolutionPython"
            continue
        value = result.get(key)
        if value is None or (isinstance(value, str) and not value.strip()):
            return f"missing {key}"
    return None


def _repair_invalid_stage_result(job: Dict[str, Any], payload: Dict[str, Any], job_type: str, bad_result: Dict[str, Any]) -> Dict[str, Any]:
    if job_type not in {"assignment_course_profile_build", "assignment_gap_analysis", "assignment_batch_plan", "assignment_batch_replan", "assignment_generate_from_text", "assignment_repair"}:
        return bad_result
    stage = "batch_plan" if job_type in {"assignment_batch_plan", "assignment_batch_replan"} else ("gap_analysis" if job_type == "assignment_gap_analysis" else ("repair" if job_type == "assignment_repair" else ("draft_generate" if job_type == "assignment_generate_from_text" else "course_profile_build")))
    prompt = build_stage_schema_repair_prompt(stage, payload, bad_result)
    required, preferred = _stage_required_keys(job_type)
    repair_cfg = _stage_llm_config(job_type, payload, _job_retry_count(job))
    repair_cfg.stage = f"{stage}_schema_repair"
    repair_cfg.timeout = min(repair_cfg.timeout, 45)
    repair_cfg.num_predict = min(repair_cfg.num_predict or 320, 320)
    repair_cfg.temperature = min(float(repair_cfg.temperature or 0.1), 0.05)
    repair_cfg.required_keys = required
    repair_cfg.preferred_keys = preferred
    _log_stage("stage-schema-repair-prompt", job, payload, prompt_len=len(prompt), required=required, preferred=preferred)
    repaired = call_ollama(prompt, repair_cfg)
    _log_stage("stage-schema-repair-raw", job, payload, result=_result_summary(repaired))
    return sanitize_result_payload(job_type, payload, repaired)


def _log_stage(event: str, job: Dict[str, Any], payload: Dict[str, Any] | None = None, **extra: Any) -> None:
    parts = [event, _job_context(job, payload)]
    for key, value in extra.items():
        if value is None:
            continue
        parts.append(f"{key}={_preview(value, 160)}")
    log(*parts)




def _fallback_course_style_analysis(payload: Dict[str, Any]) -> Dict[str, Any]:
    refs = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    titles = []
    seen = set()
    for ref in refs[:12]:
        if not isinstance(ref, dict):
            continue
        title = str(ref.get("title") or ref.get("Title") or "").strip()
        if title and title.casefold() not in seen:
            seen.add(title.casefold())
            titles.append(title)
    return {
        "courseStyle": {
            "descriptionSections": ["Суть", "Входные данные", "Выходные данные", "Ограничения", "Примечания"],
            "descriptionTone": "Короткое учебное условие в стиле существующих заданий курса.",
            "testStyle": "Небольшие конкретные примеры с простым проверяемым выводом.",
        },
        "titleStyle": {
            "pattern": "Короткое русскоязычное название в стиле текущего курса.",
            "examples": titles[:6],
        },
        "antiPatterns": [
            "Не копировать существующее название дословно",
            "Не смешивать несколько учебных целей",
            "Не уходить в продвинутую матричную тему, если slot простой",
        ],
        "positivePatterns": [
            "Соблюдать стиль названий курса",
            "Давать короткое и конкретное условие",
            "Сохранять явный формат ввода и вывода",
        ],
        "summary": "Fallback course style analysis built from existing reference assignments.",
    }


def _coerce_course_style_analysis(payload: Dict[str, Any], result: Any) -> Dict[str, Any]:
    if not isinstance(result, dict):
        return _fallback_course_style_analysis(payload)
    course_style = result.get("courseStyle") if isinstance(result.get("courseStyle"), dict) else {}
    title_style = result.get("titleStyle") if isinstance(result.get("titleStyle"), dict) else {}
    anti = result.get("antiPatterns") if isinstance(result.get("antiPatterns"), list) else []
    positive = result.get("positivePatterns") if isinstance(result.get("positivePatterns"), list) else []
    if not course_style:
        return _fallback_course_style_analysis(payload)
    if not title_style:
        title_style = _fallback_course_style_analysis(payload).get("titleStyle") or {}
    if not anti:
        anti = _fallback_course_style_analysis(payload).get("antiPatterns") or []
    if not positive:
        positive = _fallback_course_style_analysis(payload).get("positivePatterns") or []
    return {
        "courseStyle": course_style,
        "titleStyle": title_style,
        "antiPatterns": anti[:8],
        "positivePatterns": positive[:8],
        "summary": result.get("summary") or "Course style analysis completed.",
    }


def _fallback_generation_spec(payload: Dict[str, Any], style_analysis: Dict[str, Any]) -> Dict[str, Any]:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    target_skill = str(task.get("targetSkill") or task.get("TargetSkill") or brief.get("targetSkill") or "").strip()
    micro_goal = str(task.get("microGoal") or task.get("MicroGoal") or brief.get("summary") or "").strip()
    return {
        "generationSpec": {
            "exactTask": micro_goal or target_skill or "Сделать отдельную задачу в стиле курса",
            "ioContract": "Явно описать входные и выходные данные простыми секциями.",
            "constraintsFocus": "Указать только нужные ограничения без перегруза деталями.",
            "titleDirection": brief.get("titleHint") or target_skill or "Короткое название в стиле курса",
            "distinctFromPeers": "Сфокусироваться на своей микроцели и не повторять соседние slot-ы.",
            "keepStyle": (style_analysis.get("positivePatterns") if isinstance(style_analysis, dict) else [])[:5],
            "avoid": (style_analysis.get("antiPatterns") if isinstance(style_analysis, dict) else [])[:5],
        },
        "summary": "Fallback generation spec built from brief and task context.",
    }


def _coerce_generation_spec(payload: Dict[str, Any], style_analysis: Dict[str, Any], result: Any) -> Dict[str, Any]:
    if not isinstance(result, dict):
        return _fallback_generation_spec(payload, style_analysis)
    spec = result.get("generationSpec") if isinstance(result.get("generationSpec"), dict) else {}
    if not spec:
        return _fallback_generation_spec(payload, style_analysis)
    required = ["exactTask", "ioContract", "constraintsFocus", "titleDirection", "distinctFromPeers"]
    if any(not str(spec.get(k) or "").strip() for k in required):
        return _fallback_generation_spec(payload, style_analysis)
    spec.setdefault("keepStyle", (style_analysis.get("positivePatterns") if isinstance(style_analysis, dict) else [])[:5])
    spec.setdefault("avoid", (style_analysis.get("antiPatterns") if isinstance(style_analysis, dict) else [])[:5])
    return {"generationSpec": spec, "summary": result.get("summary") or "Generation spec completed."}




def _fallback_content_plan(payload: Dict[str, Any], style_analysis: Dict[str, Any], spec: Dict[str, Any]) -> Dict[str, Any]:
    generation_spec = spec.get("generationSpec") if isinstance(spec, dict) else {}
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    return {
        "contentPlan": {
            "pedagogicalGoal": str(task.get("microGoal") or task.get("MicroGoal") or brief.get("summary") or generation_spec.get("exactTask") or "Сделать отдельную задачу в стиле курса").strip(),
            "noveltyHook": str(generation_spec.get("distinctFromPeers") or "Сделать задачу отличимой от соседних items.").strip(),
            "inputModel": str(generation_spec.get("ioContract") or "Явно описать входные данные.").strip(),
            "outputModel": "Явно описать ожидаемый вывод без лишнего шума.",
            "constraintsPlan": str(generation_spec.get("constraintsFocus") or "Сфокусироваться на минимально нужных ограничениях.").strip(),
            "sectionPlan": (style_analysis.get("courseStyle") or {}).get("descriptionSections") if isinstance(style_analysis.get("courseStyle"), dict) else ["Суть", "Входные данные", "Выходные данные", "Ограничения", "Примечания"],
            "publicTestPlan": ["Базовый пример", "Пограничный компактный пример"],
            "hiddenTestPlan": ["Минимальный кейс", "Типичный кейс", "Пограничный кейс"],
            "titleShape": str(generation_spec.get("titleDirection") or brief.get("titleHint") or "Короткое course-native название").strip(),
            "bannedOverlaps": generation_spec.get("avoid") or [],
            "coursePhrasingRules": generation_spec.get("keepStyle") or [],
        },
        "summary": "Fallback content plan built from generation spec.",
    }


def _coerce_content_plan(payload: Dict[str, Any], style_analysis: Dict[str, Any], spec: Dict[str, Any], result: Any) -> Dict[str, Any]:
    if not isinstance(result, dict):
        return _fallback_content_plan(payload, style_analysis, spec)
    plan = result.get("contentPlan") if isinstance(result.get("contentPlan"), dict) else {}
    required = ["pedagogicalGoal", "noveltyHook", "inputModel", "outputModel", "constraintsPlan", "sectionPlan", "publicTestPlan", "hiddenTestPlan", "titleShape"]
    if any(not plan.get(k) for k in required):
        return _fallback_content_plan(payload, style_analysis, spec)
    return {"contentPlan": plan, "summary": result.get("summary") or "Draft content plan completed."}


def _title_looks_bad(title: str) -> bool:
    low = (title or "").strip().lower()
    if not low or low == "__pending_title__":
        return True
    banned = ["revised", "draft", "pending", "final", "version", "task"]
    if any(word in low for word in banned):
        return True
    if all(ord(ch) < 128 for ch in low) and len(low.split()) <= 8:
        return True
    return False

def _merge_generated_title(result: Dict[str, Any], title_result: Dict[str, Any], payload: Dict[str, Any]) -> Dict[str, Any]:
    draft = result.get("draft") if isinstance(result.get("draft"), dict) else {}
    title = ""
    if isinstance(title_result, dict):
        title = str(title_result.get("title") or "").strip()
    direction = ""
    spec = payload.get("generationSpec") if isinstance(payload.get("generationSpec"), dict) else {}
    if isinstance(spec, dict):
        direction = str(spec.get("titleDirection") or "").strip()
    if _title_looks_bad(title):
        if direction:
            draft_with_direction = dict(draft)
            draft_with_direction["description"] = f"{direction}. {draft.get('description') or ''}".strip()
            title = _derive_course_style_title(payload, draft_with_direction)
        else:
            title = _derive_course_style_title(payload, draft)
    draft["title"] = title
    result["draft"] = draft
    meta = draft.get("meta") if isinstance(draft.get("meta"), dict) else {}
    meta["titleGeneration"] = {"source": (title_result.get("decisionSummary") if isinstance(title_result, dict) else None) or {"source": "fallback-title"}}
    draft["meta"] = meta
    return result


def _generate_draft_via_substages(job: Dict[str, Any], payload: Dict[str, Any], retry_count: int) -> Dict[str, Any]:
    job_type = "assignment_generate_from_text"
    llm_cfg = _stage_llm_config(job_type, payload, retry_count)

    style_prompt = build_draft_course_style_analysis_prompt(job, payload)
    style_cfg = OllamaCallConfig(stage="draft_course_style_analysis", timeout=min(50, llm_cfg.timeout), num_predict=320, temperature=0.08, required_keys=["courseStyle", "titleStyle"], preferred_keys=["summary", "antiPatterns", "positivePatterns"])
    _log_stage("substage-prepare", job, payload, substage="draft_course_style_analysis", prompt_len=len(style_prompt), timeout=style_cfg.timeout, num_predict=style_cfg.num_predict)
    try:
        style_raw = call_ollama(style_prompt, style_cfg)
    except Exception as ex:
        logger.warning(f"draft course style analysis failed: {ex} [{_job_context(job, payload)}]")
        style_raw = {}
    style_result = _coerce_course_style_analysis(payload, style_raw)
    _log_stage("substage-done", job, payload, substage="draft_course_style_analysis", result=_result_summary(style_result))

    spec_prompt = build_draft_generation_spec_prompt(job, payload, style_result)
    spec_cfg = OllamaCallConfig(stage="draft_generation_spec", timeout=min(50, llm_cfg.timeout), num_predict=320, temperature=0.08, required_keys=["generationSpec"], preferred_keys=["summary"])
    _log_stage("substage-prepare", job, payload, substage="draft_generation_spec", prompt_len=len(spec_prompt), timeout=spec_cfg.timeout, num_predict=spec_cfg.num_predict)
    try:
        spec_raw = call_ollama(spec_prompt, spec_cfg)
    except Exception as ex:
        logger.warning(f"draft generation spec failed: {ex} [{_job_context(job, payload)}]")
        spec_raw = {}
    spec_result = _coerce_generation_spec(payload, style_result, spec_raw)
    _log_stage("substage-done", job, payload, substage="draft_generation_spec", result=_result_summary(spec_result))

    enriched_payload = dict(payload)
    enriched_payload["styleAnalysis"] = style_result
    enriched_payload["generationSpec"] = spec_result.get("generationSpec") if isinstance(spec_result, dict) else {}

    content_prompt = build_draft_content_plan_prompt(job, enriched_payload)
    content_cfg = OllamaCallConfig(stage="draft_content_plan", timeout=min(50, llm_cfg.timeout), num_predict=360, temperature=0.08, required_keys=["contentPlan"], preferred_keys=["summary"])
    _log_stage("substage-prepare", job, enriched_payload, substage="draft_content_plan", prompt_len=len(content_prompt), timeout=content_cfg.timeout, num_predict=content_cfg.num_predict)
    try:
        content_raw = call_ollama(content_prompt, content_cfg)
    except Exception as ex:
        logger.warning(f"draft content plan failed: {ex} [{_job_context(job, enriched_payload)}]")
        content_raw = {}
    content_result = _coerce_content_plan(payload, style_result, spec_result, content_raw)
    enriched_payload["contentPlan"] = content_result.get("contentPlan") if isinstance(content_result, dict) else {}
    _log_stage("substage-done", job, enriched_payload, substage="draft_content_plan", result=_result_summary(content_result))

    body_prompt = build_draft_body_generate_prompt(job, enriched_payload)
    body_cfg = OllamaCallConfig(stage="draft_body_generate", timeout=min(llm_cfg.timeout, max(60, llm_cfg.timeout)), num_predict=min(llm_cfg.num_predict or 1200, 1400), temperature=0.10, required_keys=["draft"], preferred_keys=["summary", "decisionSummary"])
    _log_stage("substage-prepare", job, enriched_payload, substage="draft_body_generate", prompt_len=len(body_prompt), timeout=body_cfg.timeout, num_predict=body_cfg.num_predict)
    try:
        body_result = call_ollama(body_prompt, body_cfg)
    except Exception as ex:
        logger.warning(f"draft body ollama failed: {ex} [{_job_context(job, enriched_payload)}]")
        fallback_job = dict(job)
        import json as _json
        fallback_job["inputJson"] = _json.dumps(enriched_payload, ensure_ascii=False)
        return sanitize_result_payload(job_type, enriched_payload, fallback_result(fallback_job))
    body_result = sanitize_result_payload(job_type, enriched_payload, body_result)
    _log_stage("substage-done", job, enriched_payload, substage="draft_body_generate", result=_result_summary(body_result))

    title_prompt = build_draft_title_generate_prompt(job, enriched_payload, body_result.get("draft") if isinstance(body_result.get("draft"), dict) else {})
    title_cfg = OllamaCallConfig(stage="draft_title_generate", timeout=min(40, llm_cfg.timeout), num_predict=120, temperature=0.08, required_keys=["title"], preferred_keys=["summary"])
    _log_stage("substage-prepare", job, enriched_payload, substage="draft_title_generate", prompt_len=len(title_prompt), timeout=title_cfg.timeout, num_predict=title_cfg.num_predict)
    try:
        title_result = call_ollama(title_prompt, title_cfg)
    except Exception as ex:
        logger.warning(f"draft title ollama failed: {ex} [{_job_context(job, enriched_payload)}]")
        title_result = {"title": _derive_course_style_title(enriched_payload, body_result.get("draft") if isinstance(body_result.get("draft"), dict) else {})}
    if _title_looks_bad(str((title_result or {}).get("title") or "")):
        try:
            repair_prompt = build_draft_title_repair_prompt(job, enriched_payload, body_result.get("draft") if isinstance(body_result.get("draft"), dict) else {}, str((title_result or {}).get("title") or ""))
            repair_cfg = OllamaCallConfig(stage="draft_title_repair", timeout=min(35, llm_cfg.timeout), num_predict=80, temperature=0.06, required_keys=["title"], preferred_keys=["summary"])
            _log_stage("substage-prepare", job, enriched_payload, substage="draft_title_repair", prompt_len=len(repair_prompt), timeout=repair_cfg.timeout, num_predict=repair_cfg.num_predict)
            repaired_title = call_ollama(repair_prompt, repair_cfg)
            if isinstance(repaired_title, dict) and str(repaired_title.get("title") or "").strip():
                title_result = repaired_title
            _log_stage("substage-done", job, enriched_payload, substage="draft_title_repair", result=_result_summary(repaired_title if isinstance(repaired_title, dict) else {"title": repaired_title}))
        except Exception as ex:
            logger.warning(f"draft title repair failed: {ex} [{_job_context(job, enriched_payload)}]")
    _log_stage("substage-done", job, enriched_payload, substage="draft_title_generate", result=_result_summary(title_result if isinstance(title_result, dict) else {"title": title_result}))

    result = _merge_generated_title(body_result, title_result if isinstance(title_result, dict) else {}, enriched_payload)
    return sanitize_result_payload(job_type, enriched_payload, result)

def _set_compact_mode(payload: Dict[str, Any], job_type: str, retry_count: int) -> None:
    if job_type == "assignment_course_profile_build":
        if retry_count >= 2:
            payload["__compactMode"] = "ultra"
        elif retry_count >= 1:
            payload["__compactMode"] = "compact"
    elif job_type == "assignment_gap_analysis":
        if retry_count >= 2:
            payload["__compactMode"] = "ultra"
        elif retry_count >= 1:
            payload["__compactMode"] = "compact"
    elif job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        if retry_count >= 2:
            payload["__compactMode"] = "ultra"
        elif retry_count >= 1:
            payload["__compactMode"] = "compact"


def _stage_retry_limit(job_type: str) -> int:
    if job_type == "assignment_course_profile_build":
        return COURSE_PROFILE_MAX_RETRIES
    if job_type == "assignment_gap_analysis":
        return GAP_ANALYSIS_MAX_RETRIES
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        return BATCH_PLAN_MAX_RETRIES
    if job_type == "assignment_reference_pack_build":
        return REFERENCE_PACK_MAX_RETRIES
    if job_type in {"assignment_brief_generate", "assignment_brief_repair"}:
        return BRIEF_MAX_RETRIES
    if job_type == "assignment_generate_from_text":
        return MAX_JOB_RETRIES
    return MAX_JOB_RETRIES


def _stage_llm_config(job_type: str, payload: Dict[str, Any], retry_count: int) -> OllamaCallConfig:
    compact_mode = str(payload.get("__compactMode") or "").strip().lower()
    if job_type == "assignment_course_profile_build":
        return OllamaCallConfig(stage="course_profile_build", timeout=COURSE_PROFILE_TIMEOUT, num_predict=360 if compact_mode else COURSE_PROFILE_NUM_PREDICT, temperature=0.1, required_keys=["canonicalRequest", "courseDigest", "courseProfile"], preferred_keys=["summary", "decisionSummary"])
    if job_type == "assignment_gap_analysis":
        return OllamaCallConfig(stage="gap_analysis", timeout=GAP_ANALYSIS_TIMEOUT, num_predict=280 if compact_mode else GAP_ANALYSIS_NUM_PREDICT, temperature=0.1, required_keys=["gapAnalysis", "coverage"], preferred_keys=["summary", "decisionSummary", "canonicalRequest", "courseDigest"])
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        return OllamaCallConfig(stage="batch_plan", timeout=BATCH_PLAN_TIMEOUT, num_predict=260 if compact_mode else BATCH_PLAN_NUM_PREDICT, temperature=0.08, required_keys=["plan"], preferred_keys=["canonicalRequest", "coverage", "summary", "decisionSummary"])
    if job_type in {"assignment_brief_generate", "assignment_brief_repair"}:
        return OllamaCallConfig(stage="brief", timeout=BRIEF_TIMEOUT, num_predict=420 if compact_mode else BRIEF_NUM_PREDICT, temperature=0.12, required_keys=["titleHint", "generationPrompt", "targetSkill"], preferred_keys=["summary", "difficultyTarget"])
    if job_type == "assignment_reference_pack_build":
        return OllamaCallConfig(stage="reference_pack", timeout=REFERENCE_PACK_TIMEOUT, num_predict=420 if compact_mode else REFERENCE_PACK_NUM_PREDICT, temperature=0.12, required_keys=["stylePack", "policyPack", "exemplarPack"], preferred_keys=["generationHints", "signals"])
    if job_type == "assignment_generate_from_text":
        return OllamaCallConfig(stage="draft_generate", timeout=GENERATION_TIMEOUT, num_predict=1200 if compact_mode else GENERATION_NUM_PREDICT, temperature=0.1, required_keys=["draft"], preferred_keys=["summary", "decisionSummary", "draftValidation"])
    if job_type == "assignment_repair":
        return OllamaCallConfig(stage="repair", timeout=GENERATION_TIMEOUT, num_predict=1000 if compact_mode else GENERATION_NUM_PREDICT, temperature=0.1, required_keys=["draft"], preferred_keys=["repairSummary", "draftValidation"])
    return OllamaCallConfig(stage=job_type or "generic", timeout=GENERATION_TIMEOUT, num_predict=GENERATION_NUM_PREDICT, temperature=0.15)


# ── Job dispatcher ────────────────────────────────────

def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()
    retry_count = _job_retry_count(job)
    _set_compact_mode(payload, job_type, retry_count)
    _log_stage("stage-start", job, payload, payload_keys=sorted(payload.keys())[:20], compact_mode=payload.get("__compactMode"), retry_count=retry_count)

    def _finish(result: Dict[str, Any]) -> Dict[str, Any]:
        missing_reason = _schema_missing_reason(job_type, payload, result)
        if missing_reason:
            _log_stage("stage-schema-invalid", job, payload, reason=missing_reason, result=_result_summary(result))
            try:
                result = _repair_invalid_stage_result(job, payload, job_type, result)
            except Exception as ex:
                _log_stage("stage-schema-repair-failed", job, payload, error=ex)
            missing_reason = _schema_missing_reason(job_type, payload, result)
            if missing_reason:
                _log_stage("stage-schema-fallback", job, payload, reason=missing_reason)
                fallback_job = dict(job)
                import json as _json
                fallback_job["inputJson"] = _json.dumps(payload, ensure_ascii=False)
                if job_type == "assignment_course_profile_build":
                    result = sanitize_result_payload(job_type, payload, fallback_course_profile(payload, job))
                elif job_type == "assignment_gap_analysis":
                    result = sanitize_result_payload(job_type, payload, fallback_gap_analysis(payload, job))
                else:
                    result = sanitize_result_payload(job_type, payload, fallback_result(fallback_job))
        _log_stage("stage-done", job, payload, result=_result_summary(result))
        return result

    def _ollama_stage(prompt_builder, fallback_fn=None, sanitize=True, allow_fallback=True, stage_name: str | None = None):
        builder_name = getattr(prompt_builder, "__name__", "prompt_builder")
        _log_stage("stage-prepare-prompt", job, payload, builder=builder_name)
        prompt = prompt_builder(job, payload)
        llm_cfg = _stage_llm_config(job_type, payload, retry_count)
        if stage_name:
            llm_cfg.stage = stage_name
        _log_stage("stage-prompt-ready", job, payload, builder=builder_name, prompt_len=len(prompt), timeout=llm_cfg.timeout, num_predict=llm_cfg.num_predict)
        try:
            result = call_ollama(prompt, llm_cfg)
        except Exception as ex:
            logger.warning(f"ollama failed for {job_type}: {ex} [{_job_context(job, payload)}]")
            stage_retry_limit = _stage_retry_limit(job_type)
            should_retry = retry_count < max(0, stage_retry_limit - 1)
            planner_fallback_allowed = job_type in {"assignment_batch_plan", "assignment_batch_replan"} and retry_count >= PLANNER_FALLBACK_AFTER_RETRY_COUNT
            if should_retry and not planner_fallback_allowed:
                raise RetryableStageError(f"{job_type} failed: {ex}") from ex
            if not allow_fallback and not planner_fallback_allowed:
                raise RetryableStageError(f"{job_type} failed: {ex}") from ex
            fallback_job = dict(job)
            if payload is not None:
                import json as _json
                fallback_job["inputJson"] = _json.dumps(payload, ensure_ascii=False)
            result = fallback_fn(payload, job) if fallback_fn else fallback_result(fallback_job)
            _log_stage("stage-fallback-result", job, payload, builder=builder_name, fallback_type=type(result).__name__, planner_fallback=planner_fallback_allowed)
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

    # ── Draft generate ────────────────────────────────
    if job_type == "assignment_generate_from_text":
        result = _generate_draft_via_substages(job, payload, retry_count)
        if payload.get("enableSelfCheck", True):
            before_keys = sorted(result.keys())[:12] if isinstance(result, dict) else []
            result = try_improve_generation(job, payload, result)
            after_keys = sorted(result.keys())[:12] if isinstance(result, dict) else []
            _log_stage("stage-self-check-finished", job, payload, before_keys=before_keys, after_keys=after_keys)
        return _finish(result)

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
            result = call_ollama(prompt, _stage_llm_config(job_type, payload, retry_count))
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
            result = call_ollama(prompt, _stage_llm_config(job_type, payload, retry_count))
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
        result = call_ollama(prompt, _stage_llm_config(job_type, payload, retry_count))
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
                retryable = retry_count < max(1, _stage_retry_limit((job.get("type") or "").lower().strip()) - 1)
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
