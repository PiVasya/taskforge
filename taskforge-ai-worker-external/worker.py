"""TaskForge AI Worker — thin orchestrator.

All business logic lives in dedicated modules:
  config, log, text_utils, api_client, runners, payload,
  validators, mutation, ollama, prompt_builder, reviews,
  batch_pipeline, fallbacks, repair.

This file contains only the job-routing dispatcher and the main poll loop.
"""

import json
import re
import time
from typing import Any, Dict

from config import (
    API_BASE,
    API_KEY,
    WORKER_ID,
    ACTIVE_MODEL,
    ACTIVE_PROVIDER,
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
    CHAT_STRICT_MODE,
    DRAFT_SUBSTAGES,
    SCHEMA_VALIDATION_MODE,
    STRUCTURED_OUTPUTS,
    STAGE_PROVIDER_ORDER_PLANNING,
    STAGE_PROVIDER_ORDER_CHAT,
    STAGE_PROVIDER_ORDER_DRAFT,
    STAGE_PROVIDER_ORDER_REPAIR,
    STAGE_PROVIDER_ORDER_REVIEW,
    STAGE_ROUTING_OVERRIDES,
    WORKER_TELEMETRY_ENABLED,
    DUPLICATE_CLUSTER_PREVIEW_GROUPS,
)
from log import log, logger, log_event, preview_text, INCLUDE_PROMPTS, INCLUDE_RESPONSES, PROMPT_PREVIEW_CHARS, RESPONSE_PREVIEW_CHARS
from api_client import pull_job, heartbeat, complete, fail
from payload import parse_payload, sanitize_result_payload, _derive_course_style_title
from validators import run_self_check
from llm_client import call_llm, OllamaCallConfig
from schemas import openrouter_response_format, validate_stage_result
from prompt_builder import (
    build_prompt,
    build_course_profile_prompt,
    build_gap_analysis_prompt,
    build_chat_turn_prompt,
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


def _payload_instruction_strictness(payload: Dict[str, Any]) -> int:
    for raw in [
        payload.get("instructionStrictness"),
        ((payload.get("memory") or {}) if isinstance(payload.get("memory"), dict) else {}).get("instructionStrictness"),
        ((payload.get("batchMemory") or {}) if isinstance(payload.get("batchMemory"), dict) else {}).get("instructionStrictness"),
    ]:
        try:
            if raw is None or raw == "":
                continue
            return max(0, min(100, int(raw)))
        except Exception:
            continue
    return 55


def _strictness_temperature(strictness: int, low: float, high: float) -> float:
    strictness = max(0, min(100, int(strictness)))
    span = high - low
    return round(high - (span * (strictness / 100.0)), 4)


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
    if job_type == "assistant_chat_turn":
        return (["assistantMessage", "actions"], ["sessionTitle", "action"])
    return ([], [])


def _schema_stage_name(job_type: str) -> str | None:
    mapping = {
        "assignment_course_profile_build": "course_profile_build",
        "assignment_gap_analysis": "gap_analysis",
        "assignment_batch_plan": "batch_plan",
        "assignment_batch_replan": "batch_plan",
        "assignment_reference_pack_build": "reference_pack",
        "assignment_brief_generate": "brief",
        "assignment_brief_repair": "brief",
        "assignment_generate_from_text": "draft_generate",
        "assignment_repair": "assignment_repair",
        "assistant_chat_turn": "assistant_chat_turn",
    }
    return mapping.get(job_type)


def _stage_schema_errors(job_type: str, result: Dict[str, Any]) -> list[str]:
    if SCHEMA_VALIDATION_MODE == "off":
        return []
    ok, errors = validate_stage_result(_schema_stage_name(job_type), result)
    return [] if ok else errors


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
                if draft.get("hiddenTests") is not None and not isinstance(draft.get("hiddenTests"), list):
                    return "invalid draft.hiddenTests"
                if not str(draft.get("referenceSolutionPython") or "").strip():
                    return "missing draft.referenceSolutionPython"
            continue
        value = result.get(key)
        if value is None or (isinstance(value, str) and not value.strip()):
            return f"missing {key}"
    return None


def _chat_last_user_text(payload: Dict[str, Any]) -> str:
    conversation = payload.get("conversation") if isinstance(payload.get("conversation"), list) else []
    for item in reversed(conversation):
        if not isinstance(item, dict):
            continue
        if str(item.get("role") or "").strip().lower() != "user":
            continue
        text = str(item.get("content") or item.get("text") or "").strip()
        if text:
            return text
    return ""


def _chat_recent_attachments(payload: Dict[str, Any]) -> list[Dict[str, Any]]:
    items = payload.get("recentAttachments") if isinstance(payload.get("recentAttachments"), list) else []
    return [x for x in items if isinstance(x, dict)]


def _chat_extract_requested_count(text: str) -> int | None:
    if not text:
        return None
    for pattern in (
        r"(?<!\d)(\d{1,2})\s*(?:задач[а-я]*|шт\.?|штук|items?)",
        r"\bна\s+(\d{1,2})\b",
        r"\b(\d{1,2})\s*(?:pieces|tasks?)\b",
    ):
        match = re.search(pattern, text, flags=re.IGNORECASE)
        if match:
            try:
                value = int(match.group(1))
                return max(1, min(50, value))
            except Exception:
                return None
    return None


def _chat_extract_assignment_id(text: str) -> str | None:
    if not text:
        return None
    match = re.search(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", text)
    return match.group(0) if match else None


def _chat_wants_multiple(text: str, model_count: int) -> bool:
    low = (text or "").lower()
    if any(token in low for token in ["саму задачу", "одну задачу", "одно задание", "один пример", "одну штуку"]):
        return False
    return model_count > 1 or any(token in low for token in ["batch", "пакет", "нескольк", "много", "ещё", "еще", "задач"])


def _chat_pick_assignment_type(payload: Dict[str, Any], result: Dict[str, Any]) -> str:
    defaults = payload.get("defaults") if isinstance(payload.get("defaults"), dict) else {}
    return str(result.get("assignmentType") or defaults.get("assignmentType") or "code-test").strip() or "code-test"


def _chat_safe_count(value: Any, default: int = 1) -> int:
    try:
        parsed = int(value)
    except Exception:
        parsed = default
    return max(1, min(12, parsed))


def _chat_pick_difficulty(payload: Dict[str, Any], result: Dict[str, Any]) -> int:
    defaults = payload.get("defaults") if isinstance(payload.get("defaults"), dict) else {}
    try:
        value = int(result.get("difficulty") or defaults.get("difficulty") or 2)
    except Exception:
        value = 2
    return max(1, min(3, value))


def _chat_pick_mode(payload: Dict[str, Any], result: Dict[str, Any]) -> str:
    defaults = payload.get("defaults") if isinstance(payload.get("defaults"), dict) else {}
    return str(result.get("mode") or defaults.get("mode") or "topic-pack").strip() or "topic-pack"


def _chat_pick_course_id(payload: Dict[str, Any], result: Dict[str, Any]) -> Any:
    return result.get("courseId") or payload.get("courseId")


def _chat_build_session_title(payload: Dict[str, Any]) -> str | None:
    selected = payload.get("selectedCourse") if isinstance(payload.get("selectedCourse"), dict) else {}
    title = str(selected.get("title") or payload.get("sessionTitle") or "").strip()
    if not title:
        return None
    if title.lower().startswith("ai чат") or title.lower().startswith("ai chat"):
        return title[:64]
    return f"AI чат · {title}"[:64]


def _collect_known_ids(payload: Dict[str, Any], key: str) -> set[str]:
    values: set[str] = set()
    if key == "courseId":
        for item in [payload.get("courseId"), ((payload.get("selectedCourse") or {}) if isinstance(payload.get("selectedCourse"), dict) else {}).get("id")]:
            if str(item or "").strip():
                values.add(str(item).strip())
        for collection_key in ("availableCourses", "recentCourses"):
            items = payload.get(collection_key) if isinstance(payload.get(collection_key), list) else []
            for obj in items:
                if isinstance(obj, dict) and str(obj.get("id") or "").strip():
                    values.add(str(obj.get("id")).strip())
        return values
    source_map = {"draftId": "recentDrafts", "assignmentId": "recentAssignments", "batchId": "recentBatches", "userId": "recentUsers", "sourceAttemptId": "recentAttempts"}
    items = payload.get(source_map.get(key, "")) if isinstance(payload.get(source_map.get(key, "")), list) else []
    for obj in items:
        if isinstance(obj, dict):
            raw = obj.get(key) or obj.get("id")
            if str(raw or "").strip():
                values.add(str(raw).strip())
    return values


def _action_is_destructive(name: str) -> bool:
    return name in {"approve_draft", "reject_draft", "publish_draft"}


def _validate_chat_action_arguments(payload: Dict[str, Any], action: Dict[str, Any]) -> tuple[bool, str | None]:
    args = action.get("arguments") if isinstance(action.get("arguments"), dict) else {}
    action["arguments"] = args
    for key, value in list(args.items()):
        if not key.endswith("Id") and not key.endswith("Ids"):
            continue
        base_key = key[:-1] if key.endswith("Ids") else key
        known = _collect_known_ids(payload, base_key)
        if not known:
            continue
        if key.endswith("Ids"):
            values = value if isinstance(value, list) else [value]
            normalized = [str(item).strip() for item in values if str(item).strip()]
            if any(item not in known for item in normalized):
                return False, f"unknown ids in {key}"
            args[key] = normalized
        else:
            normalized = str(value or "").strip()
            if normalized and normalized not in known:
                return False, f"unknown id in {key}"
            args[key] = normalized
    if _action_is_destructive(str(action.get("name") or "")) and not bool(args.get("confirmed")):
        return False, "destructive action requires confirmed=true"
    return True, None


def _apply_chat_strict_mode(payload: Dict[str, Any], result: Dict[str, Any]) -> tuple[Dict[str, Any], list[str]]:
    if not CHAT_STRICT_MODE or not isinstance(result, dict):
        return result, []
    allowed = {str(item.get("name") or "").strip() for item in (payload.get("availableActions") if isinstance(payload.get("availableActions"), list) else []) if isinstance(item, dict)}
    actions = result.get("actions") if isinstance(result.get("actions"), list) else []
    strict_actions: list[Dict[str, Any]] = []
    issues: list[str] = []
    for action in actions[:3]:
        if not isinstance(action, dict):
            issues.append("action is not object")
            continue
        name = str(action.get("name") or "").strip()
        if not name or (allowed and name not in allowed):
            issues.append(f"unknown action: {name or '-'}")
            continue
        if not isinstance(action.get("arguments"), dict):
            action["arguments"] = {}
        last_user = _chat_last_user_text(payload)
        latest_intent_kind = _chat_latest_intent_kind(payload, last_user, str(result.get("assistantMessage") or ""))
        explicit_generate_ok = _chat_is_finalize_request(last_user) or _chat_is_direct_generate_request(last_user) or _chat_has_approved_blueprint(payload)
        if name in {"queue_generate_from_text", "queue_generate_batch", "queue_generate_from_file"} and latest_intent_kind == "generate" and not explicit_generate_ok:
            issues.append("generation requires chat blueprint approval first")
            continue
        if name == "finalize_chat_blueprint" and not (_chat_is_finalize_request(last_user) or _chat_is_direct_generate_request(last_user) or _chat_has_blueprint(payload)):
            issues.append("finalize_chat_blueprint requires explicit approval")
            continue
        ok, reason = _validate_chat_action_arguments(payload, action)
        if not ok:
            issues.append(reason or f"invalid action args: {name}")
            continue
        strict_actions.append({"name": name, "reason": str(action.get("reason") or "").strip() or "Выбрано по текущему контексту чата.", "arguments": action.get("arguments") if isinstance(action.get("arguments"), dict) else {}})
    result["actions"] = strict_actions
    if issues and not strict_actions and not str(result.get("assistantMessage") or "").strip():
        result["assistantMessage"] = "Мне не хватает надёжных данных для запуска действия без риска ошибки. Уточни запрос или выбери сущность явно."
    elif issues and any("confirmed" in issue for issue in issues) and not strict_actions:
        result["assistantMessage"] = str(result.get("assistantMessage") or "").strip() or "Для этого действия нужно явное подтверждение публикации или одобрения."
    if issues and not result.get("sessionTitle"):
        result["sessionTitle"] = _chat_build_session_title(payload)
    return result, issues




def _chat_memory(payload: Dict[str, Any]) -> Dict[str, Any]:
    return payload.get("memory") if isinstance(payload.get("memory"), dict) else {}


def _chat_blueprint(payload: Dict[str, Any]) -> Dict[str, Any]:
    direct = payload.get("currentDraftBlueprint") if isinstance(payload.get("currentDraftBlueprint"), dict) else None
    if isinstance(direct, dict):
        return direct
    memory = _chat_memory(payload)
    return memory.get("currentDraftBlueprint") if isinstance(memory.get("currentDraftBlueprint"), dict) else {}


def _chat_blueprint_proposals(payload: Dict[str, Any]) -> list[Dict[str, Any]]:
    blueprint = _chat_blueprint(payload)
    proposals = blueprint.get("proposals") if isinstance(blueprint.get("proposals"), list) else []
    return [item for item in proposals if isinstance(item, dict)]


def _chat_has_blueprint(payload: Dict[str, Any]) -> bool:
    return len(_chat_blueprint_proposals(payload)) > 0


def _chat_has_approved_blueprint(payload: Dict[str, Any]) -> bool:
    blueprint = _chat_blueprint(payload)
    if not isinstance(blueprint, dict):
        return False
    return bool(blueprint.get("approvedForDraft") or blueprint.get("ApprovedForDraft")) and _chat_has_blueprint(payload)


def _chat_is_direct_generate_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    markers = [
        "всё генерируй", "все генерируй", "генерируй задачу", "генерируй уже", "запускай создание",
        "запускай генерацию", "не черновик", "без черновика", "делай уже фул", "делай фул", "делай уже полную",
        "сразу создавай задачу", "сразу запускай задачу", "создавай задачу", "запускай создание задачи",
        "я её опубликую", "я ее опубликую", "готовое задание", "готовую задачу", "делай задачу уже"
    ]
    return any(marker in low for marker in markers)


def _chat_is_edit_draft_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    edit_markers = [
        "отредач", "отредакт", "поправь", "исправь", "измени", "доработай", "перепиши"
    ]
    draft_markers = [
        "черновик", "draft", "готовое", "что получилось", "сгенерирован", "опубликован"
    ]
    return any(marker in low for marker in edit_markers) and any(marker in low for marker in draft_markers)


def _chat_is_edit_blueprint_request(payload: Dict[str, Any], text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    edit_markers = ["отредач", "отредакт", "поправь", "исправь", "измени", "доработай", "перепиши", "замени"]
    target_markers = ["вариант", "услов", "задач", "тест", "пример", "шаг"]
    if not any(marker in low for marker in edit_markers):
        return False
    if any(marker in low for marker in ["черновик", "draft", "опубликован", "что получилось"]):
        return False
    return _chat_has_blueprint(payload) and any(marker in low for marker in target_markers)


def _chat_is_finalize_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    return any(marker in low for marker in [
        "одобря", "ок, делай", "ок делай", "норм, делай", "закидывай в черновик", "в черновик", "сделай черновик",
        "преврати в черновик", "финализируй", "сохраняй как задачу", "делай draft", "создавай draft",
        "всё генерируй", "все генерируй", "генерируй задачу", "запускай создание", "запускай генерацию",
        "не черновик", "без черновика", "делай уже фул", "делай фул", "я её опубликую", "я ее опубликую"
    ])


def _chat_is_show_blueprint_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    return any(marker in low for marker in ["покажи варианты", "покажи услов", "какие варианты", "покажи наброс", "покажи черновые услов", "ещё раз покажи"]) 


def _chat_is_drop_blueprint_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    return any(marker in low for marker in ["начни заново", "сбрось варианты", "удали варианты", "выбрось варианты", "заново варианты", "очисти условия"]) 


def _chat_instruction_contract(payload: Dict[str, Any]) -> Dict[str, Any]:
    try:
        from prompt_builder import _extract_instruction_contract  # type: ignore
        contract = _extract_instruction_contract(payload)
        return contract if isinstance(contract, dict) else {}
    except Exception:
        return {}


def _chat_extract_code_like_snippets(text: str) -> list[str]:
    text = str(text or "")
    snippets: list[str] = []
    seen: set[str] = set()

    def push(value: str) -> None:
        item = str(value or "").strip().strip('`')
        if len(item) < 2:
            return
        key = item.casefold()
        if key in seen:
            return
        seen.add(key)
        snippets.append(item[:160])

    import re
    for match in re.findall(r"`([^`]{2,200})`", text):
        push(match)
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        candidate = re.sub(r"^(шаг\s*\d+[:.)-]?|напиши(?:те)?[:\s-]*|введите[:\s-]*|сделай(?:те)?[:\s-]*)", "", line, flags=re.IGNORECASE).strip()
        if 2 <= len(candidate) <= 200 and any(token in candidate for token in ["#", ";", "<", ">", "{", "}", "(", ")", "::", "<<", ">>"]):
            push(candidate)
    return snippets[:8]


def _chat_build_fallback_blueprint_condition(last_user: str, assistant: str, contract: Dict[str, Any]) -> str:
    explicit_lines = [line.strip() for line in str(last_user or "").splitlines() if line.strip()]
    exact = [str(x).strip() for x in (contract.get("exactSnippets") if isinstance(contract.get("exactSnippets"), list) else []) if str(x).strip()]
    forbidden = [str(x).strip() for x in (contract.get("forbiddenSnippets") if isinstance(contract.get("forbiddenSnippets"), list) else []) if str(x).strip()]
    code_lines = _chat_extract_code_like_snippets(last_user)
    focus_lines: list[str] = []
    for line in explicit_lines:
        low = line.lower()
        if any(token in low for token in ["курс рассчитан", "пиши короче", "для первокласс", "1 в 1", "пошагово", "полное решение", "без return", "return 0", "пример:"]):
            focus_lines.append(line)
    sections: list[str] = []
    if focus_lines:
        sections.append("Что нужно сделать:\n" + "\n".join(focus_lines[:6]))
    elif assistant:
        sections.append("Что нужно сделать:\n" + str(assistant).strip()[:400])
    if code_lines or exact:
        merged: list[str] = []
        seen: set[str] = set()
        for item in code_lines + exact:
            key = item.casefold()
            if key in seen:
                continue
            seen.add(key)
            merged.append(item)
        sections.append("Обязательные строки или фрагменты:\n" + "\n".join(f"- {item}" for item in merged[:8]))
    if forbidden:
        sections.append("Что нельзя добавлять:\n" + "\n".join(f"- {item}" for item in forbidden[:6]))
    if contract.get("preserveOrder"):
        sections.append("Важно: сохраняй порядок шагов таким же, как его задал пользователь.")
    return "\n\n".join(section for section in sections if section).strip()


def _chat_build_blueprint_proposals(payload: Dict[str, Any], result: Dict[str, Any], last_user: str, prompt: str, count: int, assignment_type: str, difficulty: int) -> list[Dict[str, Any]]:
    raw = result.get("draftBlueprint") if isinstance(result.get("draftBlueprint"), dict) else {}
    proposals = raw.get("proposals") if isinstance(raw.get("proposals"), list) else []
    existing = _chat_blueprint_proposals(payload)
    contract = _chat_instruction_contract(payload)
    exact = [str(x).strip() for x in (contract.get("exactSnippets") if isinstance(contract.get("exactSnippets"), list) else []) if str(x).strip()]
    forbidden = [str(x).strip() for x in (contract.get("forbiddenSnippets") if isinstance(contract.get("forbiddenSnippets"), list) else []) if str(x).strip()]
    clean: list[Dict[str, Any]] = []
    for index, item in enumerate(proposals[: max(1, min(5, count))], start=1):
        if not isinstance(item, dict):
            continue
        existing_item = existing[index - 1] if index - 1 < len(existing) and isinstance(existing[index - 1], dict) else {}
        title = str(item.get("title") or item.get("name") or existing_item.get("title") or f"Вариант {index}").strip()
        cond = str(item.get("conditionPreview") or item.get("fullCondition") or item.get("summary") or item.get("condition") or existing_item.get("conditionPreview") or "").strip()
        full_condition = str(item.get("fullCondition") or existing_item.get("fullCondition") or cond).strip()
        goal = str(item.get("goal") or item.get("microGoal") or existing_item.get("goal") or "").strip()
        if not title and not cond:
            continue
        must_keep = [str(x).strip() for x in (item.get("mustKeep") if isinstance(item.get("mustKeep"), list) else existing_item.get("mustKeep") if isinstance(existing_item.get("mustKeep"), list) else []) if str(x).strip()][:8]
        avoid = [str(x).strip() for x in (item.get("avoid") if isinstance(item.get("avoid"), list) else existing_item.get("avoid") if isinstance(existing_item.get("avoid"), list) else []) if str(x).strip()][:8]
        for snippet in exact[:8]:
            if snippet.casefold() not in {x.casefold() for x in must_keep}:
                must_keep.append(snippet)
        for snippet in forbidden[:8]:
            if snippet.casefold() not in {x.casefold() for x in avoid}:
                avoid.append(snippet)
        if not cond:
            cond = _chat_build_fallback_blueprint_condition(last_user, str(result.get("assistantMessage") or ""), contract)
        if not full_condition:
            full_condition = _chat_build_fallback_blueprint_condition(last_user, str(result.get("assistantMessage") or ""), contract)
        proposal = {
            "id": item.get("id") or existing_item.get("id") or None,
            "title": title or f"Вариант {index}",
            "assignmentType": str(item.get("assignmentType") or existing_item.get("assignmentType") or assignment_type or "code-test").strip() or "code-test",
            "difficulty": max(1, min(5, int(item.get("difficulty") or existing_item.get("difficulty") or difficulty or 2))),
            "goal": goal,
            "conditionPreview": cond[:2000],
            "fullCondition": full_condition[:8000],
            "mustKeep": must_keep[:10],
            "avoid": avoid[:10],
            "placementAfterAssignmentId": item.get("placementAfterAssignmentId") or existing_item.get("placementAfterAssignmentId") or item.get("afterAssignmentId") or existing_item.get("afterAssignmentId"),
            "placementAfterTitle": item.get("placementAfterTitle") or existing_item.get("placementAfterTitle") or item.get("afterAssignmentTitle") or existing_item.get("afterAssignmentTitle"),
            "placementReason": item.get("placementReason") or existing_item.get("placementReason"),
            "status": item.get("status") or existing_item.get("status") or "draft",
            "publicTests": [
                {
                    "input": str(t.get("input") or "").strip()[:200],
                    "expectedOutput": str(t.get("expectedOutput") or "").strip()[:200],
                }
                for t in (((item.get("publicTests") if isinstance(item.get("publicTests"), list) else existing_item.get("publicTests") if isinstance(existing_item.get("publicTests"), list) else [])[:8])) if isinstance(t, dict)
            ],
            "hiddenTests": [
                {
                    "input": str(t.get("input") or "").strip()[:200],
                    "expectedOutput": str(t.get("expectedOutput") or "").strip()[:200],
                }
                for t in (((item.get("hiddenTests") if isinstance(item.get("hiddenTests"), list) else existing_item.get("hiddenTests") if isinstance(existing_item.get("hiddenTests"), list) else [])[:8])) if isinstance(t, dict)
            ],
        }
        clean.append(proposal)
    if clean:
        return clean
    base_text = str(result.get("assistantMessage") or "").strip() or str(prompt or last_user or "").strip()
    base_condition = _chat_build_fallback_blueprint_condition(last_user, base_text, contract) or str(result.get("conditionPreview") or result.get("summary") or base_text or prompt or last_user or "").strip()
    default_count = max(1, min(3, count or 1))
    return [{
        "id": (existing[i - 1].get("id") if i - 1 < len(existing) and isinstance(existing[i - 1], dict) else None),
        "title": str(result.get("title") or (existing[i - 1].get("title") if i - 1 < len(existing) and isinstance(existing[i - 1], dict) else f"Вариант {i}")).strip() or f"Вариант {i}",
        "assignmentType": assignment_type,
        "difficulty": difficulty,
        "goal": str(result.get("goal") or "").strip(),
        "conditionPreview": base_condition[:2000],
        "fullCondition": (base_condition or prompt or last_user)[:8000],
        "mustKeep": exact[:10],
        "avoid": forbidden[:10],
        "publicTests": [],
        "hiddenTests": [],
    } for i in range(1, default_count + 1)]


def _chat_is_listing_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    mentions_assignments = any(token in low for token in ["задан", "assignment", "урок", "курс"])
    asks_to_show = any(token in low for token in ["выведи", "выводи", "покажи", "показывай", "список", "перечисли", "какие", "изучи задачи курса"])
    mentions_plan = any(token in low for token in ["план", "мостик", "подводящ"])
    return mentions_assignments and asks_to_show and not mentions_plan


def _chat_is_audit_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    asks_audit = any(token in low for token in ["косяк", "косяки", "пробел", "пробелы", "найди", "найти", "проверь", "аудит", "слишком рано", "до объясн", "прежде чем", "ещё такие", "еще такие", "опубликован"])
    mentions_pedagogy = any(token in low for token in ["переменн", "cout", "cin", "ввод", "вывод", "include", "namespace", "main", "синтакс", "объясн", "подвод", "лесенк", "новая функция", "новые функции"])
    if asks_audit and mentions_pedagogy:
        return True
    return _chat_is_precision_check_request(low) and mentions_pedagogy


def _chat_is_gap_remediation_request(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    asks_for_coverage = any(token in low for token in [
        "все пробел", "все дыр", "все косяк", "все слабые места", "закрой пробел", "закрыть пробел",
        "на все пробел", "по всем пробел", "весь курс", "по всему курсу", "реально проанализируй",
        "предложи решения", "предложи мостики", "предложи как закрыть", "найди пробелы и",
        "сгенерируй задачи на все", "собери задачи на все", "исправь пробелы", "ремеди"
    ])
    mentions_course = any(token in low for token in ["курс", "курса", "курсе", "обучал", "лесенк", "педагог", "мостик", "подводящ"])
    return asks_for_coverage and mentions_course


def _chat_has_strong_blueprint_revision_signal(payload: Dict[str, Any], text: str) -> bool:
    low = (text or "").strip().lower()
    if not low or not _chat_has_blueprint(payload):
        return False
    explicit_approval = _chat_is_finalize_request(low) and not any(marker in low for marker in ["не так", "передел", "сначала", "но", "только", "замени", "оставь"])
    if explicit_approval:
        return False
    revision_markers = [
        "1 в 1", "один в один", "пошагово", "как первое задание", "как в первом задании", "как образец",
        "точно как", "сделай как", "передел", "не так", "оставь", "замени", "используй", "остальное устраивает",
        "сохрани", "убери", "добавь", "только не", "только чтобы", "повтори структуру"
    ]
    return any(marker in low for marker in revision_markers)


def _chat_agent_state(payload: Dict[str, Any]) -> Dict[str, Any]:
    memory = _chat_memory(payload)
    return memory.get("agentState") if isinstance(memory.get("agentState"), dict) else {}


def _chat_agent_plan_steps(payload: Dict[str, Any]) -> list[dict[str, Any]]:
    agent_state = _chat_agent_state(payload)
    items = agent_state.get("planSteps") if isinstance(agent_state.get("planSteps"), list) else []
    clean = []
    for item in items[:8]:
        if not isinstance(item, dict):
            continue
        key = str(item.get("key") or "").strip()
        title = str(item.get("title") or "").strip()
        status = str(item.get("status") or "pending").strip().lower() or "pending"
        action = str(item.get("recommendedAction") or "").strip()
        if not key and not title:
            continue
        clean.append({
            "key": key,
            "title": title,
            "status": status,
            "summary": str(item.get("summary") or "").strip(),
            "recommendedAction": action,
            "successSignal": str(item.get("successSignal") or "").strip(),
            "blockedBy": str(item.get("blockedBy") or "").strip(),
        })
    return clean


def _chat_agent_candidate_actions(payload: Dict[str, Any]) -> list[dict[str, Any]]:
    clean = []
    seen = set()
    for step in _chat_agent_plan_steps(payload):
        name = str(step.get("recommendedAction") or "").strip()
        if not name or name in seen:
            continue
        status = "preferred" if step.get("status") == "current" else ("candidate" if step.get("status") == "pending" else "done")
        clean.append({
            "name": name,
            "why": str(step.get("summary") or step.get("successSignal") or "").strip(),
            "status": status,
        })
        seen.add(name)
    agent_state = _chat_agent_state(payload)
    actions = agent_state.get("decisionCandidates") if isinstance(agent_state.get("decisionCandidates"), list) else []
    for item in actions[:5]:
        if not isinstance(item, dict):
            continue
        name = str(item.get("name") or "").strip()
        if not name or name in seen:
            continue
        clean.append({
            "name": name,
            "why": str(item.get("why") or "").strip(),
            "status": str(item.get("status") or "candidate").strip().lower() or "candidate",
        })
        seen.add(name)
    return clean


def _chat_agent_open_questions(payload: Dict[str, Any]) -> list[str]:
    agent_state = _chat_agent_state(payload)
    items = agent_state.get("openQuestions") if isinstance(agent_state.get("openQuestions"), list) else []
    return [str(x).strip() for x in items if str(x).strip()][:6]


def _chat_agent_confidence(payload: Dict[str, Any]) -> int:
    agent_state = _chat_agent_state(payload)
    try:
        return max(0, min(100, int(agent_state.get("confidencePercent") or 0)))
    except Exception:
        return 0


def _chat_agent_blocker_summary(payload: Dict[str, Any]) -> str:
    agent_state = _chat_agent_state(payload)
    return str(agent_state.get("blockerSummary") or "").strip()


def _chat_agent_needs_clarification(payload: Dict[str, Any]) -> bool:
    agent_state = _chat_agent_state(payload)
    return bool(agent_state.get("needsClarification"))


def _chat_agent_autonomy_mode(payload: Dict[str, Any]) -> str:
    agent_state = _chat_agent_state(payload)
    return str(agent_state.get("autonomyMode") or "").strip().lower()


def _chat_pick_agent_candidate_action(payload: Dict[str, Any]) -> str:
    allowed = {
        "analyze_course_progression", "inspect_course_assignments", "prepare_bridge_plan",
        "save_chat_blueprint", "revise_chat_blueprint", "finalize_chat_blueprint",
        "queue_generate_from_text", "show_chat_blueprint", "show_bridge_plan"
    }
    for item in _chat_agent_candidate_actions(payload):
        name = item["name"]
        status = str(item.get("status") or "candidate").strip().lower()
        if status == "done":
            continue
        if name in allowed:
            return name
    return ""


def _chat_latest_intent_kind(payload: Dict[str, Any], last_user: str, prompt: str) -> str:
    low = (last_user or prompt or "").strip().lower()
    if _chat_is_listing_request(low):
        return "inspect"
    if _chat_is_gap_remediation_request(low):
        return "remediation"
    if _chat_is_audit_request(low):
        return "audit"
    if _chat_is_drop_blueprint_request(low):
        return "drop-blueprint"
    if _chat_is_show_blueprint_request(low):
        return "show-blueprint"
    if _chat_is_edit_blueprint_request(payload, low) or _chat_has_strong_blueprint_revision_signal(payload, low):
        return "revise-blueprint"
    if _chat_is_edit_draft_request(low):
        return "edit-draft"
    if _chat_is_direct_generate_request(low):
        return "generate"
    if _chat_is_finalize_request(low):
        return "finalize-blueprint"
    if any(marker in low for marker in ["поправь план", "измени план", "исправь план", "поставь её второй", "поставь ее второй", "добавь вторым"]):
        return "revise-plan"
    if any(marker in low for marker in ["покажи план", "какой план", "что в плане"]):
        return "show-plan"
    if any(marker in low for marker in ["собери план", "сделай план", "предложи план", "план вставок", "мостик", "подводящ"]):
        return "plan"
    if any(marker in low for marker in ["не план", "саму задачу", "готовую задачу", "готовый текст", "создай черновик", "создай draft", "сразу генерац", "сгенерируй", "создай зада", "сделай зада", "всё, делай", "все, делай", "делай всё", "делай все", "сделай всё сразу", "сделай все сразу"]):
        return "generate"
    memory = _chat_memory(payload)
    agent_state = memory.get("agentState") if isinstance(memory.get("agentState"), dict) else {}
    for raw in (memory.get("latestIntentKind"), agent_state.get("latestIntentKind")):
        value = str(raw or "").strip()
        if value:
            return value
    return "chat"


def _chat_is_precision_check_request(text: str) -> bool:
    low = (text or "").lower()
    return any(token in low for token in [
        "точно",
        "точнее",
        "где именно",
        "по итогу",
        "проверь точнее",
        "посмотри точнее",
        "реальные условия",
        "сама задача",
        "само условие",
        "она врёт",
        "она врет",
    ])


def _chat_latest_teaching_script(payload: Dict[str, Any], last_user: str) -> str:
    memory = _chat_memory(payload)
    for raw in (memory.get("latestTeachingScript"), memory.get("latestExplicitInstruction")):
        value = str(raw or "").strip()
        if value:
            return value
    # Do NOT fall back to last_user — it caused suppress_bridge_plan_loop to fire on every message
    return ""


def _chat_suppress_bridge_plan_loop(payload: Dict[str, Any], latest_intent_kind: str, teaching_script: str, last_user: str) -> bool:
    memory = _chat_memory(payload)
    if bool(memory.get("suppressBridgePlanLoop")):
        return True
    if latest_intent_kind == "generate" or bool(teaching_script.strip()):
        return True
    low = (last_user or "").lower()
    return any(marker in low for marker in ["не показывай план", "не возвращайся к план", "не делай новый план", "не show_bridge_plan", "не revise_bridge_plan", "нужна сама задача", "нужен именно текст задачи", "сделай всё сразу", "сделай все сразу", "всё, делай", "все, делай"])

def _normalize_chat_turn_result(payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(result, dict):
        return {"assistantMessage": "Я не смогла корректно разобрать ответ модели. Повтори запрос короче или уточни действие.", "actions": []}

    last_user = _chat_last_user_text(payload)
    prompt = str(result.get("prompt") or result.get("summary") or result.get("assistantMessage") or "").strip()
    latest_intent_kind = _chat_latest_intent_kind(payload, last_user, prompt)
    if latest_intent_kind in {"generate", "revise-blueprint"} and isinstance(result.get("draftBlueprint"), dict):
        proposals = _chat_build_blueprint_proposals(payload, result, last_user, prompt, _chat_safe_count(result.get("count"), max(1, len(_chat_blueprint_proposals(payload)) or 1)), _chat_pick_assignment_type(payload, result), _chat_pick_difficulty(payload, result))
        action_name = "revise_chat_blueprint" if latest_intent_kind == "revise-blueprint" and _chat_has_blueprint(payload) else "save_chat_blueprint"
        assistant_fallback = "Я обновила примерные условия в чате. Посмотри, всё ли теперь совпадает, и скажи, когда уже закидывать в черновик." if action_name == "revise_chat_blueprint" else "Я набросала примерные условия. Посмотри, что поправить, и потом скажи, когда закидывать в черновик."
        result["actions"] = [{
            "name": action_name,
            "reason": "Обновляю уже сохранённые примерные условия по новым замечаниям пользователя." if action_name == "revise_chat_blueprint" else "Сначала сохраняю примерные условия из чата, чтобы пользователь мог их поправить и утвердить перед финализацией в draft.",
            "arguments": {
                "courseId": _chat_pick_course_id(payload, result),
                "summary": str((result.get("draftBlueprint") or {}).get("summary") or result.get("assistantMessage") or "").strip()[:300],
                "proposals": proposals,
            },
        }]
        return {
            "assistantMessage": str(result.get("assistantMessage") or "").strip() or assistant_fallback,
            "actions": result.get("actions") or [],
            "sessionTitle": result.get("sessionTitle") or _chat_build_session_title(payload),
        }

    if isinstance(result.get("actions"), list) and str(result.get("assistantMessage") or "").strip():
        if result.get("actions"):
            assistant_msg = str(result.get("assistantMessage") or "").strip()
            last_user = _chat_last_user_text(payload)
            seen_action_names = set()
            deduped_actions = []
            for action in result["actions"]:
                if not isinstance(action, dict):
                    continue
                aname = str(action.get("name") or "").strip()
                if aname and aname in seen_action_names:
                    continue
                if aname:
                    seen_action_names.add(aname)
                args = action.get("arguments") if isinstance(action.get("arguments"), dict) else {}
                action_prompt = str(args.get("prompt") or "").strip()
                if action_prompt and assistant_msg and action_prompt == assistant_msg:
                    args["prompt"] = last_user or action_prompt
                deduped_actions.append(action)
            result["actions"] = deduped_actions
        return result

    llm_msg = str(result.get("assistantMessage") or "").strip()
    if llm_msg and len(llm_msg) > 40:
        return {
            "assistantMessage": llm_msg,
            "actions": [],
            "sessionTitle": result.get("sessionTitle") or _chat_build_session_title(payload),
        }

    prompt = str(result.get("prompt") or result.get("summary") or result.get("message") or "").strip()
    if not prompt:
        prompt = _chat_last_user_text(payload) or llm_msg
    if not prompt:
        return {
            "assistantMessage": "Я не смогла собрать внятный ответ по этому сообщению. Сформулируй запрос чуть конкретнее: что именно сделать и для какого курса.",
            "actions": [],
            "sessionTitle": _chat_build_session_title(payload),
        }

    last_user = _chat_last_user_text(payload)
    course_id = _chat_pick_course_id(payload, result)
    raw_count = result.get("count")
    model_count = (_chat_extract_requested_count(str(raw_count or "")) or _chat_safe_count(raw_count, 1)) if str(raw_count or "").strip() else 1
    explicit_count = _chat_extract_requested_count(last_user)
    wants_multiple = _chat_wants_multiple(last_user, model_count)
    final_count = explicit_count or max(1, model_count)
    assignment_type = _chat_pick_assignment_type(payload, result)
    difficulty = _chat_pick_difficulty(payload, result)
    mode = _chat_pick_mode(payload, result)
    attachments = _chat_recent_attachments(payload)
    mentioned_file = any(token in (last_user or "").lower() for token in ["файл", "влож", "прикреп", "pdf", "docx", "xlsx", "pptx", "zip"])
    last_attachment = attachments[-1] if attachments else None
    memory = _chat_memory(payload)
    agent_state = memory.get("agentState") if isinstance(memory.get("agentState"), dict) else {}
    latest_intent_kind = _chat_latest_intent_kind(payload, last_user, prompt)
    focus_text = last_user or prompt
    explicit_after_assignment_id = _chat_extract_assignment_id(last_user)
    remembered_after_assignment_id = explicit_after_assignment_id or agent_state.get("placementAfterAssignmentId") or ((agent_state.get("placementCandidates") or [{}])[0].get("afterAssignmentId") if isinstance(agent_state.get("placementCandidates"), list) and agent_state.get("placementCandidates") else None)
    short_followup = (last_user or "").strip().lower() in {"продолжай", "давай дальше", "дальше", "начинай", "ок", "го", "погнали", "делай дальше"}

    if not course_id and latest_intent_kind in {"inspect", "audit", "remediation", "plan", "generate"}:
        return {
            "assistantMessage": "Для этого шага нужен выбранный курс. Выбери курс справа, и я продолжу в этом же контексте.",
            "actions": [],
            "sessionTitle": _chat_build_session_title(payload),
        }

    if latest_intent_kind == "remediation":
        return {
            "assistantMessage": "Запускаю полный проход по курсу: сначала найду реальные пробелы, потом проверю соседние задания и соберу решения.",
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "advance_agent_stage",
                "reason": "Пользователь просит не просто аудит, а полноценный remediation-цикл по курсу: discover -> verify -> propose -> draft.",
                "arguments": {
                    "courseId": course_id,
                    "focus": focus_text,
                },
            }],
        }

    if latest_intent_kind == "inspect":
        return {
            "assistantMessage": "Открою существующие задания курса и выведу их сюда списком.",
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "inspect_course_assignments",
                "reason": "Пользователь просит посмотреть и перечислить уже существующие задания курса, а не строить новый план.",
                "arguments": {
                    "courseId": course_id,
                    "query": focus_text,
                    **({"aroundAssignmentId": remembered_after_assignment_id} if remembered_after_assignment_id else {}),
                    "window": 4 if remembered_after_assignment_id else 0,
                    "limitAssignments": 30,
                },
            }],
        }

    if latest_intent_kind == "audit":
        has_audit = isinstance(memory.get("lastCourseAudit"), dict)
        has_inspection = isinstance(memory.get("lastCourseInspection"), dict) and isinstance((memory.get("lastCourseInspection") or {}).get("assignments"), list) and len((memory.get("lastCourseInspection") or {}).get("assignments") or []) > 0
        if _chat_is_precision_check_request(last_user):
            if has_audit and not has_inspection:
                return {
                    "assistantMessage": "Открою реальные задания и сверю выводы по условиям, чтобы не опираться только на старый аудит.",
                    "sessionTitle": _chat_build_session_title(payload),
                    "actions": [{
                        "name": "inspect_course_assignments",
                        "reason": "Пользователь просит проверить точнее, поэтому нужно снять ложные срабатывания аудита по реальным условиям.",
                        "arguments": {
                            "courseId": course_id,
                            "query": focus_text,
                            **({"aroundAssignmentId": remembered_after_assignment_id} if remembered_after_assignment_id else {}),
                            "window": 4 if remembered_after_assignment_id else 2,
                            "limitAssignments": 18,
                        },
                    }],
                }
            if not has_audit and not has_inspection:
                return {
                    "assistantMessage": "Сначала перепроверю курс, а потом сразу открою реальные задания, чтобы ответить по условиям, а не по догадкам.",
                    "sessionTitle": _chat_build_session_title(payload),
                    "actions": [
                        {
                            "name": "analyze_course_progression",
                            "reason": "Нужен базовый аудит по курсу перед точечной проверкой спорных мест.",
                            "arguments": {
                                "courseId": course_id,
                                "focus": focus_text,
                            },
                        },
                        {
                            "name": "inspect_course_assignments",
                            "reason": "Сразу после аудита нужно открыть реальные задания и подтвердить или опровергнуть спорные выводы.",
                            "arguments": {
                                "courseId": course_id,
                                "query": focus_text,
                                **({"aroundAssignmentId": remembered_after_assignment_id} if remembered_after_assignment_id else {}),
                                "window": 4 if remembered_after_assignment_id else 2,
                                "limitAssignments": 18,
                            },
                        },
                    ],
                }
        return {
            "assistantMessage": "Сначала проверю курс под этот фокус и найду проблемные места.",
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "analyze_course_progression",
                "reason": "Пользователь просит найти педагогические пробелы и резкие вводы новых сущностей в курсе.",
                "arguments": {
                    "courseId": course_id,
                    "focus": focus_text,
                },
            }],
        }

    if latest_intent_kind == "plan":
        has_audit = isinstance(memory.get("lastCourseAudit"), dict)
        if has_audit:
            return {
                "assistantMessage": "Соберу план вставок по найденному контексту.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{
                    "name": "prepare_bridge_plan",
                    "reason": "Пользователь прямо просит план мостиков или подводящих заданий.",
                    "arguments": {
                        "courseId": course_id,
                        "focus": focus_text,
                        **({"afterAssignmentId": remembered_after_assignment_id} if remembered_after_assignment_id else {}),
                        **({"count": explicit_count} if explicit_count else {}),
                    },
                }],
            }
        return {
            "assistantMessage": "Сначала быстро проверю курс, чтобы план был опорным, а не выдуманным.",
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "analyze_course_progression",
                "reason": "Для осмысленного плана сначала нужен аудит курса под текущий запрос.",
                "arguments": {
                    "courseId": course_id,
                    "focus": focus_text,
                },
            }],
        }

    blueprint = _chat_blueprint(payload)
    blueprint_proposals = _chat_blueprint_proposals(payload)

    if latest_intent_kind == "generate" and blueprint_proposals and (_chat_is_finalize_request(last_user) or _chat_is_direct_generate_request(last_user) or _chat_has_approved_blueprint(payload)):
        selected = blueprint_proposals[0]
        return {
            "assistantMessage": "Ок, запускаю создание задачи по уже согласованному условию из чата.",
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "queue_generate_from_text",
                "reason": "Уже есть согласованный chat blueprint, а пользователь явно просит сразу запустить создание задачи.",
                "arguments": {
                    "courseId": course_id,
                    "useCurrentBlueprint": True,
                    "proposalId": selected.get("id"),
                    "assignmentType": selected.get("assignmentType") or assignment_type,
                    "difficulty": selected.get("difficulty") or difficulty,
                    "titleHint": selected.get("title") or result.get("titleHint") or result.get("title") or None,
                    "enableSelfCheck": True,
                },
            }],
        }

    if latest_intent_kind == "show-blueprint":
        if blueprint_proposals:
            return {
                "assistantMessage": "Показываю текущие примерные условия из этой сессии.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{
                    "name": "show_chat_blueprint",
                    "reason": "Пользователь просит показать уже собранные примерные условия, не превращая их пока в draft.",
                    "arguments": {"courseId": course_id},
                }],
            }
        return {"assistantMessage": "Пока нет сохранённых примерных условий. Сначала опиши, какую задачу или набор задач ты хочешь собрать.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if latest_intent_kind == "drop-blueprint":
        if blueprint_proposals:
            return {
                "assistantMessage": "Сброшу старые варианты и начнём с чистого листа.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{"name": "drop_chat_blueprint", "reason": "Пользователь просит выбросить старые примерные условия и собрать новые.", "arguments": {"courseId": course_id}}],
            }
        return {"assistantMessage": "Сейчас в памяти и так нет старых вариантов. Можно сразу описывать новый замысел.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if latest_intent_kind == "revise-blueprint":
        if blueprint_proposals:
            return {
                "assistantMessage": "Поняла. Обновлю текущие варианты в чате по твоим замечаниям.",
                "actions": [],
                "sessionTitle": _chat_build_session_title(payload),
            }
        return {"assistantMessage": "Сейчас нечего править: сначала нужно собрать примерные условия прямо в чате.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if latest_intent_kind == "finalize-blueprint":
        if blueprint_proposals:
            return {
                "assistantMessage": "Ок, превращаю согласованные условия в полноценные draft-черновики.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{
                    "name": "finalize_chat_blueprint",
                    "reason": "Пользователь явно одобрил текущие примерные условия и просит закинуть их в черновики.",
                    "arguments": {"courseId": course_id, "enableSelfCheck": True},
                }],
            }
        return {"assistantMessage": "Мне пока нечего финализировать: сначала нужно собрать и обсудить примерные условия прямо в чате.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if latest_intent_kind == "edit-draft":
        recent_drafts = payload.get("recentDrafts") if isinstance(payload.get("recentDrafts"), list) else []
        latest_draft_id = None
        for item in recent_drafts:
            if isinstance(item, dict) and str(item.get("id") or "").strip():
                latest_draft_id = str(item.get("id")).strip()
                break
        if latest_draft_id:
            return {
                "assistantMessage": "Ок, запущу правку уже готового AI-черновика по твоим замечаниям.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{
                    "name": "revise_draft_from_chat",
                    "reason": "Пользователь просит поправить уже готовый AI-черновик по новому сообщению.",
                    "arguments": {"courseId": course_id, "draftId": latest_draft_id, "prompt": focus_text},
                }],
            }
        return {"assistantMessage": "Я не вижу в этой сессии готового AI-черновика для правки. Сначала сгенерируй его или открой нужный draft.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if short_followup:
        if blueprint_proposals:
            return {
                "assistantMessage": "Продолжаю по текущим примерным условиям. Напиши, что менять, или скажи, когда уже закидывать в черновик.",
                "actions": [],
                "sessionTitle": _chat_build_session_title(payload),
            }
        confidence = _chat_agent_confidence(payload)
        open_questions = _chat_agent_open_questions(payload)
        blocker_summary = _chat_agent_blocker_summary(payload)
        needs_clarification = _chat_agent_needs_clarification(payload)
        autonomy_mode = _chat_agent_autonomy_mode(payload)
        candidate_action = _chat_pick_agent_candidate_action(payload)
        next_suggested = str(agent_state.get("nextSuggestedAction") or "").strip().lower()
        if (needs_clarification or autonomy_mode == "ask-first") and (blocker_summary or open_questions):
            blocker = blocker_summary or open_questions[0]
            return {"assistantMessage": f"Сейчас лучше не прыгать дальше вслепую: {blocker}", "actions": [], "sessionTitle": _chat_build_session_title(payload)}
        if confidence < 35 and open_questions:
            return {"assistantMessage": f"Сейчас ещё не хватает опоры: {open_questions[0]}", "actions": [], "sessionTitle": _chat_build_session_title(payload)}
        if candidate_action in {"analyze_course_progression", "inspect_course_assignments", "prepare_bridge_plan", "show_bridge_plan", "save_chat_blueprint", "revise_chat_blueprint", "finalize_chat_blueprint", "queue_generate_from_text"}:
            return {
                "assistantMessage": "Продолжаю от текущего состояния сессии.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{
                    "name": "advance_agent_stage",
                    "reason": "Короткий follow-up пользователя. Агент опирается на planSteps и decisionCandidates, а не только на старый nextSuggestedAction.",
                    "arguments": {"courseId": course_id, "focus": focus_text},
                }],
            }
        if next_suggested in {"analyze_course_progression", "inspect_course_assignments", "prepare_bridge_plan", "show_bridge_plan", "queue_generate_bridge_batch", "queue_generate_batch", "queue_generate_from_text"}:
            return {
                "assistantMessage": "Продолжаю от текущего состояния сессии.",
                "sessionTitle": _chat_build_session_title(payload),
                "actions": [{
                    "name": "advance_agent_stage",
                    "reason": "Короткий follow-up пользователя. Можно безопасно продолжить от сохранённого agentState.",
                    "arguments": {"courseId": course_id, "focus": focus_text},
                }],
            }
        return {"assistantMessage": "Уточни, что именно продолжать: показать существующие задания, искать пробелы или собирать новые условия в чате?", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if wants_multiple and explicit_count is None:
        return {"assistantMessage": f"Поняла направление. Сколько задач нужно набросать в чате: 2, 3, 5 или другое число? Сейчас вижу сложность {difficulty}/5.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}

    if last_attachment and mentioned_file and latest_intent_kind == "generate" and _chat_is_finalize_request(last_user):
        return {
            "assistantMessage": f"Ок, запускаю финальную генерацию по файлу '{last_attachment.get('originalName') or last_attachment.get('fileKey') or 'файл'}'.",
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "queue_generate_from_file",
                "reason": "Пользователь явно подтвердил финальную генерацию из прикреплённого файла.",
                "arguments": {"courseId": course_id, "prompt": prompt, "fileKey": last_attachment.get("fileKey"), "assignmentType": assignment_type, "count": final_count, "difficulty": difficulty, "titleHint": result.get("titleHint") or result.get("title") or None, "enableSelfCheck": True},
            }],
        }

    if latest_intent_kind == "generate":
        proposals = _chat_build_blueprint_proposals(payload, result, last_user, prompt, final_count, assignment_type, difficulty)
        assistant = str(result.get("assistantMessage") or "").strip() or ("Я набросала примерные условия для обсуждения. Посмотри, что менять, и потом скажи, когда уже закидывать в черновик." if final_count <= 1 else f"Я набросала {len(proposals)} примерных условий. Посмотри, что менять, и потом скажи, когда уже закидывать их в черновики.")
        return {
            "assistantMessage": assistant,
            "sessionTitle": _chat_build_session_title(payload),
            "actions": [{
                "name": "save_chat_blueprint",
                "reason": "Для нового задания сначала нужно сохранить примерные условия в памяти чата, обсудить правки и только потом финализировать в draft.",
                "arguments": {"courseId": course_id, "summary": assistant[:300], "proposals": proposals},
            }],
        }

    return {
        "assistantMessage": "Уточни, что ты хочешь сделать с этим курсом: показать существующие задания, найти пробелы или сгенерировать новые?",
        "actions": [],
        "sessionTitle": _chat_build_session_title(payload),
    }


def _repair_invalid_stage_result(job: Dict[str, Any], payload: Dict[str, Any], job_type: str, bad_result: Dict[str, Any], schema_errors: list[str] | None = None) -> Dict[str, Any]:
    stage = _schema_stage_name(job_type)
    if not stage:
        return bad_result
    prompt = build_stage_schema_repair_prompt(stage, payload, bad_result, schema_errors=schema_errors)
    required, preferred = _stage_required_keys(job_type)
    repair_cfg = _stage_llm_config(job_type, payload, _job_retry_count(job))
    repair_cfg.stage = f"{stage}_schema_repair"
    if job_type in {"assignment_repair", "assignment_generate_from_text"}:
        repair_cfg.timeout = min(repair_cfg.timeout + 30, 75)
        repair_cfg.num_predict = min(repair_cfg.num_predict or 1200, 1200)
    else:
        repair_cfg.timeout = min(repair_cfg.timeout, 45)
        repair_cfg.num_predict = min(repair_cfg.num_predict or 320, 320)
    repair_cfg.required_keys = required
    repair_cfg.preferred_keys = preferred
    repair_cfg.json_schema = None
    _log_stage("stage-schema-repair-prompt", job, payload, prompt_len=len(prompt), required=required, preferred=preferred, schema_errors=(schema_errors or [])[:6])
    repaired = call_llm(prompt, repair_cfg)
    _log_stage("stage-schema-repair-raw", job, payload, result=_result_summary(repaired))
    return sanitize_result_payload(job_type, payload, repaired)



def _payload_overview(payload: Dict[str, Any] | None) -> Dict[str, Any]:
    if not isinstance(payload, dict):
        return {}
    info: Dict[str, Any] = {
        'payload_keys': list(payload.keys())[:20],
    }
    if isinstance(payload.get('conversation'), list):
        info['conversation_items'] = len(payload.get('conversation') or [])
    if isinstance(payload.get('recentAttachments'), list):
        info['recent_attachments'] = len(payload.get('recentAttachments') or [])
    if isinstance(payload.get('referenceAssignments'), list):
        info['reference_assignments'] = len(payload.get('referenceAssignments') or [])
    if isinstance(payload.get('batchItems'), list):
        info['batch_items'] = len(payload.get('batchItems') or [])
    prompt = payload.get('prompt')
    if INCLUDE_PROMPTS and isinstance(prompt, str) and prompt.strip():
        info['prompt_preview'] = preview_text(prompt, PROMPT_PREVIEW_CHARS)
    return info


def _consume_llm_meta(result: Any) -> dict[str, Any] | None:
    if isinstance(result, dict):
        meta = result.pop("__llmMeta", None)
        if isinstance(meta, dict):
            return meta
    return None


def _build_worker_telemetry(job: Dict[str, Any], payload: Dict[str, Any], result: Dict[str, Any], llm_meta: dict[str, Any] | None) -> dict[str, Any] | None:
    if not WORKER_TELEMETRY_ENABLED:
        return None
    telemetry: dict[str, Any] = {
        "jobType": (job.get("type") or "").lower().strip(),
        "jobId": job.get("id"),
        "stageCode": job.get("stageCode") or payload.get("stageCode"),
        "compactMode": payload.get("__compactMode"),
        "retryCount": _job_retry_count(job),
        "resultKeys": sorted(result.keys())[:16] if isinstance(result, dict) else [],
    }
    anchor_context = payload.get("anchorContext") if isinstance(payload.get("anchorContext"), dict) else {}
    selection = payload.get("selectionTelemetry") if isinstance(payload.get("selectionTelemetry"), dict) else {}
    if selection:
        telemetry["selectionTelemetry"] = {
            "anchorId": selection.get("anchorId"),
            "selectedIds": list(selection.get("selectedIds") or [])[:8],
            "seedTokens": list(selection.get("seedTokens") or [])[:8],
            "topCandidates": list(selection.get("topCandidates") or [])[:5],
        }
    duplicate_preview = anchor_context.get("duplicateClustersPreview") if isinstance(anchor_context.get("duplicateClustersPreview"), list) else []
    duplicate_hints = anchor_context.get("duplicateSignatureHints") if isinstance(anchor_context.get("duplicateSignatureHints"), list) else []
    telemetry["duplicateSignals"] = {
        "clusterCount": len(duplicate_preview),
        "signatureHints": len(duplicate_hints),
        "clusters": duplicate_preview[:max(1, DUPLICATE_CLUSTER_PREVIEW_GROUPS)],
    }
    if isinstance(llm_meta, dict) and llm_meta:
        telemetry["llm"] = llm_meta
    return telemetry


def _result_overview(result: Any) -> Dict[str, Any]:
    if not isinstance(result, dict):
        return {'result_type': type(result).__name__}
    info: Dict[str, Any] = {
        'result_keys': sorted(result.keys())[:20],
        'result_status': result.get('status'),
        'result_score': result.get('score'),
    }
    summary = result.get('summary') or result.get('assistantMessage') or result.get('message') or result.get('title')
    if summary is not None:
        info['result_summary'] = preview_text(summary, RESPONSE_PREVIEW_CHARS)
    if INCLUDE_RESPONSES:
        info['result_preview'] = preview_text(result, RESPONSE_PREVIEW_CHARS)
    return info


def _log_stage(event: str, job: Dict[str, Any], payload: Dict[str, Any] | None = None, **extra: Any) -> None:
    fields: Dict[str, Any] = {
        'job_context': _job_context(job, payload),
    }
    fields.update(_payload_overview(payload))
    normalized_extra: Dict[str, Any] = {}
    for key, value in extra.items():
        if value is None:
            continue
        if key == 'result' and isinstance(value, dict):
            normalized_extra.update(_result_overview(value))
        else:
            normalized_extra[key] = value
    fields.update(normalized_extra)
    log_event(event, **fields)




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
    style_cfg = _with_stage_schema(OllamaCallConfig(stage="draft_course_style_analysis", timeout=min(50, llm_cfg.timeout), num_predict=320, temperature=0.08, required_keys=["courseStyle", "titleStyle"], preferred_keys=["summary", "antiPatterns", "positivePatterns"]), "assignment_generate_from_text", retry_count, str(payload.get("__compactMode") or "").strip().lower())
    _log_stage("substage-prepare", job, payload, substage="draft_course_style_analysis", prompt_len=len(style_prompt), timeout=style_cfg.timeout, num_predict=style_cfg.num_predict)
    try:
        style_raw = call_llm(style_prompt, style_cfg)
    except Exception as ex:
        logger.warning(f"draft course style analysis failed: {ex} [{_job_context(job, payload)}]")
        style_raw = {}
    style_result = _coerce_course_style_analysis(payload, style_raw)
    _log_stage("substage-done", job, payload, substage="draft_course_style_analysis", result=_result_summary(style_result))

    spec_prompt = build_draft_generation_spec_prompt(job, payload, style_result)
    spec_cfg = _with_stage_schema(OllamaCallConfig(stage="draft_generation_spec", timeout=min(50, llm_cfg.timeout), num_predict=320, temperature=0.08, required_keys=["generationSpec"], preferred_keys=["summary"]), "assignment_generate_from_text", retry_count, str(payload.get("__compactMode") or "").strip().lower())
    _log_stage("substage-prepare", job, payload, substage="draft_generation_spec", prompt_len=len(spec_prompt), timeout=spec_cfg.timeout, num_predict=spec_cfg.num_predict)
    try:
        spec_raw = call_llm(spec_prompt, spec_cfg)
    except Exception as ex:
        logger.warning(f"draft generation spec failed: {ex} [{_job_context(job, payload)}]")
        spec_raw = {}
    spec_result = _coerce_generation_spec(payload, style_result, spec_raw)
    _log_stage("substage-done", job, payload, substage="draft_generation_spec", result=_result_summary(spec_result))

    enriched_payload = dict(payload)
    enriched_payload["styleAnalysis"] = style_result
    enriched_payload["generationSpec"] = spec_result.get("generationSpec") if isinstance(spec_result, dict) else {}

    content_prompt = build_draft_content_plan_prompt(job, enriched_payload)
    content_cfg = _with_stage_schema(OllamaCallConfig(stage="draft_content_plan", timeout=min(50, llm_cfg.timeout), num_predict=360, temperature=0.08, required_keys=["contentPlan"], preferred_keys=["summary"]), "assignment_generate_from_text", retry_count, str(payload.get("__compactMode") or "").strip().lower())
    _log_stage("substage-prepare", job, enriched_payload, substage="draft_content_plan", prompt_len=len(content_prompt), timeout=content_cfg.timeout, num_predict=content_cfg.num_predict)
    try:
        content_raw = call_llm(content_prompt, content_cfg)
    except Exception as ex:
        logger.warning(f"draft content plan failed: {ex} [{_job_context(job, enriched_payload)}]")
        content_raw = {}
    content_result = _coerce_content_plan(payload, style_result, spec_result, content_raw)
    enriched_payload["contentPlan"] = content_result.get("contentPlan") if isinstance(content_result, dict) else {}
    _log_stage("substage-done", job, enriched_payload, substage="draft_content_plan", result=_result_summary(content_result))

    body_prompt = build_draft_body_generate_prompt(job, enriched_payload)
    body_cfg = _with_stage_schema(OllamaCallConfig(stage="draft_body_generate", timeout=min(llm_cfg.timeout, max(60, llm_cfg.timeout)), num_predict=min(llm_cfg.num_predict or 1200, 1400), temperature=0.10, required_keys=["draft"], preferred_keys=["summary", "decisionSummary"]), "assignment_generate_from_text", retry_count, str(payload.get("__compactMode") or "").strip().lower())
    _log_stage("substage-prepare", job, enriched_payload, substage="draft_body_generate", prompt_len=len(body_prompt), timeout=body_cfg.timeout, num_predict=body_cfg.num_predict)
    try:
        body_result = call_llm(body_prompt, body_cfg)
    except Exception as ex:
        logger.warning(f"draft body ollama failed: {ex} [{_job_context(job, enriched_payload)}]")
        fallback_job = dict(job)
        import json as _json
        fallback_job["inputJson"] = _json.dumps(enriched_payload, ensure_ascii=False)
        return sanitize_result_payload(job_type, enriched_payload, fallback_result(fallback_job))
    body_result = sanitize_result_payload(job_type, enriched_payload, body_result)
    _log_stage("substage-done", job, enriched_payload, substage="draft_body_generate", result=body_result)

    title_prompt = build_draft_title_generate_prompt(job, enriched_payload, body_result.get("draft") if isinstance(body_result.get("draft"), dict) else {})
    title_cfg = _with_stage_schema(OllamaCallConfig(stage="draft_title_generate", timeout=min(40, llm_cfg.timeout), num_predict=120, temperature=0.08, required_keys=["title"], preferred_keys=["summary"]), "assignment_generate_from_text", retry_count, str(payload.get("__compactMode") or "").strip().lower())
    _log_stage("substage-prepare", job, enriched_payload, substage="draft_title_generate", prompt_len=len(title_prompt), timeout=title_cfg.timeout, num_predict=title_cfg.num_predict)
    try:
        title_result = call_llm(title_prompt, title_cfg)
    except Exception as ex:
        logger.warning(f"draft title ollama failed: {ex} [{_job_context(job, enriched_payload)}]")
        title_result = {"title": _derive_course_style_title(enriched_payload, body_result.get("draft") if isinstance(body_result.get("draft"), dict) else {})}
    if _title_looks_bad(str((title_result or {}).get("title") or "")):
        try:
            repair_prompt = build_draft_title_repair_prompt(job, enriched_payload, body_result.get("draft") if isinstance(body_result.get("draft"), dict) else {}, str((title_result or {}).get("title") or ""))
            repair_cfg = _with_stage_schema(OllamaCallConfig(stage="draft_title_repair", timeout=min(35, llm_cfg.timeout), num_predict=80, temperature=0.06, required_keys=["title"], preferred_keys=["summary"]), "assignment_generate_from_text", retry_count, str(payload.get("__compactMode") or "").strip().lower())
            _log_stage("substage-prepare", job, enriched_payload, substage="draft_title_repair", prompt_len=len(repair_prompt), timeout=repair_cfg.timeout, num_predict=repair_cfg.num_predict)
            repaired_title = call_llm(repair_prompt, repair_cfg)
            if isinstance(repaired_title, dict) and str(repaired_title.get("title") or "").strip():
                title_result = repaired_title
            _log_stage("substage-done", job, enriched_payload, substage="draft_title_repair", result=(repaired_title if isinstance(repaired_title, dict) else {"title": repaired_title}))
        except Exception as ex:
            logger.warning(f"draft title repair failed: {ex} [{_job_context(job, enriched_payload)}]")
    _log_stage("substage-done", job, enriched_payload, substage="draft_title_generate", result=(title_result if isinstance(title_result, dict) else {"title": title_result}))

    result = _merge_generated_title(body_result, title_result if isinstance(title_result, dict) else {}, enriched_payload)
    return sanitize_result_payload(job_type, enriched_payload, result)

def _set_compact_mode(payload: Dict[str, Any], job_type: str, retry_count: int) -> None:
    if not isinstance(payload, dict):
        return

    def _payload_size_bytes() -> int:
        try:
            return len(json.dumps(payload, ensure_ascii=False, default=str))
        except Exception:
            return 0

    payload_size = _payload_size_bytes()
    existing = str(payload.get("__compactMode") or "").strip().lower()
    mode = existing

    if job_type == "assignment_course_profile_build":
        if retry_count >= 2:
            mode = "ultra"
        elif retry_count >= 1:
            mode = mode or "compact"
    elif job_type == "assignment_gap_analysis":
        if retry_count >= 2:
            mode = "ultra"
        elif retry_count >= 1:
            mode = mode or "compact"
    elif job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        if retry_count >= 2:
            mode = "ultra"
        elif retry_count >= 1:
            mode = mode or "compact"
    elif job_type == "assistant_chat_turn":
        if retry_count >= 2 or payload_size >= 220_000:
            mode = "ultra"
        elif retry_count >= 1 or payload_size >= 110_000:
            mode = mode or "compact"

    if mode:
        payload["__compactMode"] = mode


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


def _use_draft_substages(payload: Dict[str, Any], retry_count: int) -> bool:
    explicit = payload.get("__useDraftSubstages")
    if isinstance(explicit, bool):
        return explicit
    if explicit is not None:
        return str(explicit).strip().lower() in {"1", "true", "yes", "on"}
    if DRAFT_SUBSTAGES:
        return True
    compact_mode = str(payload.get("__compactMode") or "").strip().lower()
    ref_count = len(payload.get("referenceAssignments") or []) if isinstance(payload.get("referenceAssignments"), list) else 0
    return retry_count > 0 or compact_mode == "ultra" or ref_count >= 8


def _stage_group(stage_name: str | None, job_type: str | None = None) -> str:
    stage = str(stage_name or job_type or "").strip().lower()
    if stage in {"course_profile_build", "gap_analysis", "batch_plan", "brief", "reference_pack"} or stage.startswith("assignment_batch"):
        return "planning"
    if stage == "assistant_chat_turn":
        return "chat"
    if stage.startswith("repair") or stage in {"assignment_repair"}:
        return "repair"
    if stage.startswith("draft_") or stage == "draft_generate" or stage == "assignment_generate_from_text":
        return "draft"
    return "review"


def _stage_routing_profile(stage_name: str | None, job_type: str | None, retry_count: int, compact_mode: str) -> Dict[str, Any] | None:
    group = _stage_group(stage_name, job_type)
    override = None
    if isinstance(STAGE_ROUTING_OVERRIDES, dict):
        override = STAGE_ROUTING_OVERRIDES.get(str(stage_name or "")) or STAGE_ROUTING_OVERRIDES.get(str(job_type or "")) or STAGE_ROUTING_OVERRIDES.get(group)
    if isinstance(override, dict):
        profile = dict(override)
    else:
        if group == "planning":
            order = STAGE_PROVIDER_ORDER_PLANNING
        elif group == "chat":
            order = STAGE_PROVIDER_ORDER_CHAT
        elif group == "draft":
            order = STAGE_PROVIDER_ORDER_DRAFT
        elif group == "repair":
            order = STAGE_PROVIDER_ORDER_REPAIR
        else:
            order = STAGE_PROVIDER_ORDER_REVIEW
        profile = {"order": order} if order else {}
    if not profile:
        return None
    if retry_count > 0:
        profile.setdefault("allow_fallbacks", True)
    if compact_mode == "ultra":
        profile.setdefault("allow_fallbacks", True)
    return profile


def _stage_plugins(stage_name: str | None, retry_count: int, compact_mode: str, wants_schema: bool) -> list[dict[str, Any]]:
    plugin_ids: list[str] = []
    if wants_schema:
        plugin_ids.append("response-healing")
    if retry_count > 0 or compact_mode in {"compact", "ultra"} or _stage_group(stage_name) in {"draft", "repair"}:
        plugin_ids.append("context-compression")
    unique: list[str] = []
    seen: set[str] = set()
    for item in plugin_ids:
        if item in seen:
            continue
        seen.add(item)
        unique.append(item)
    return [{"id": item} for item in unique]


def _with_stage_schema(cfg: OllamaCallConfig, job_type: str | None = None, retry_count: int = 0, compact_mode: str = "") -> OllamaCallConfig:
    if STRUCTURED_OUTPUTS and not cfg.json_schema:
        cfg.json_schema = openrouter_response_format(cfg.stage)
    routing = _stage_routing_profile(cfg.stage, job_type, retry_count, compact_mode)
    if routing:
        cfg.routing = routing
    plugins = _stage_plugins(cfg.stage, retry_count, compact_mode, bool(cfg.json_schema or cfg.json_mode))
    if plugins:
        cfg.plugins = plugins
    return cfg


def _stage_llm_config(job_type: str, payload: Dict[str, Any], retry_count: int) -> OllamaCallConfig:
    compact_mode = str(payload.get("__compactMode") or "").strip().lower()
    if job_type == "assignment_course_profile_build":
        return _with_stage_schema(OllamaCallConfig(stage="course_profile_build", timeout=COURSE_PROFILE_TIMEOUT, num_predict=360 if compact_mode else COURSE_PROFILE_NUM_PREDICT, temperature=0.1, required_keys=["canonicalRequest", "courseDigest", "courseProfile"], preferred_keys=["summary", "decisionSummary"]), job_type, retry_count, compact_mode)
    if job_type == "assignment_gap_analysis":
        return _with_stage_schema(OllamaCallConfig(stage="gap_analysis", timeout=GAP_ANALYSIS_TIMEOUT, num_predict=280 if compact_mode else GAP_ANALYSIS_NUM_PREDICT, temperature=0.1, required_keys=["gapAnalysis", "coverage"], preferred_keys=["summary", "decisionSummary", "canonicalRequest", "courseDigest"]), job_type, retry_count, compact_mode)
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        return _with_stage_schema(OllamaCallConfig(stage="batch_plan", timeout=BATCH_PLAN_TIMEOUT, num_predict=260 if compact_mode else BATCH_PLAN_NUM_PREDICT, temperature=0.08, required_keys=["plan"], preferred_keys=["canonicalRequest", "coverage", "summary", "decisionSummary"]), job_type, retry_count, compact_mode)
    if job_type in {"assignment_brief_generate", "assignment_brief_repair"}:
        return _with_stage_schema(OllamaCallConfig(stage="brief", timeout=BRIEF_TIMEOUT, num_predict=420 if compact_mode else BRIEF_NUM_PREDICT, temperature=0.12, required_keys=["titleHint", "generationPrompt", "targetSkill"], preferred_keys=["summary", "difficultyTarget"]), job_type, retry_count, compact_mode)
    if job_type == "assignment_reference_pack_build":
        return _with_stage_schema(OllamaCallConfig(stage="reference_pack", timeout=REFERENCE_PACK_TIMEOUT, num_predict=420 if compact_mode else REFERENCE_PACK_NUM_PREDICT, temperature=0.12, required_keys=["stylePack", "policyPack", "exemplarPack"], preferred_keys=["generationHints", "signals"]), job_type, retry_count, compact_mode)
    if job_type == "assignment_generate_from_text":
        strictness = _payload_instruction_strictness(payload)
        temperature = _strictness_temperature(strictness, 0.03, 0.18)
        return _with_stage_schema(OllamaCallConfig(stage="draft_generate", timeout=GENERATION_TIMEOUT, num_predict=1200 if compact_mode else GENERATION_NUM_PREDICT, temperature=temperature, required_keys=["draft"], preferred_keys=["summary", "decisionSummary", "draftValidation"]), job_type, retry_count, compact_mode)
    if job_type == "assignment_repair":
        repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
        primary_route = str(repair_plan.get("primaryRoute") or "general").strip().lower() or "general"
        stage_name = f"repair_{primary_route}" if primary_route != "general" else "repair"
        strictness = _payload_instruction_strictness(payload)
        temperature = _strictness_temperature(strictness, 0.02, 0.12)
        return _with_stage_schema(OllamaCallConfig(stage=stage_name, timeout=GENERATION_TIMEOUT, num_predict=1000 if compact_mode else GENERATION_NUM_PREDICT, temperature=temperature, required_keys=["draft"], preferred_keys=["repairSummary", "draftValidation"]), job_type, retry_count, compact_mode)
    if job_type == "assistant_chat_turn":
        strictness = _payload_instruction_strictness(payload)
        temperature = _strictness_temperature(strictness, 0.01, 0.2)
        cfg = OllamaCallConfig(stage="assistant_chat_turn", timeout=GENERATION_TIMEOUT, num_predict=900 if compact_mode else min(GENERATION_NUM_PREDICT, 1200), temperature=temperature, required_keys=["assistantMessage", "actions"], preferred_keys=["sessionTitle", "action"], json_schema=None, assistant_prefill=False)
        cfg.json_mode = True
        cfg = _with_stage_schema(cfg, job_type, retry_count, compact_mode)
        cfg.json_schema = None
        return cfg
    return _with_stage_schema(OllamaCallConfig(stage=job_type or "generic", timeout=GENERATION_TIMEOUT, num_predict=GENERATION_NUM_PREDICT, temperature=0.15), job_type, retry_count, compact_mode)



# ── Job dispatcher ────────────────────────────────────

def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()
    retry_count = _job_retry_count(job)
    _set_compact_mode(payload, job_type, retry_count)
    _log_stage("stage-start", job, payload, payload_keys=sorted(payload.keys())[:20], compact_mode=payload.get("__compactMode"), retry_count=retry_count)

    def _finish(result: Dict[str, Any]) -> Dict[str, Any]:
        llm_meta = _consume_llm_meta(result)
        if llm_meta:
            _log_stage("stage-llm-meta", job, payload, llm_meta=llm_meta)
        worker_telemetry = _build_worker_telemetry(job, payload, result, llm_meta)
        if worker_telemetry:
            result["__workerTelemetry"] = worker_telemetry
        if job_type == "assistant_chat_turn":
            result = _normalize_chat_turn_result(payload, result)
            result, chat_issues = _apply_chat_strict_mode(payload, result)
            if chat_issues:
                _log_stage("chat-strict-mode-adjusted", job, payload, issues=chat_issues[:8], result=result)
        missing_reason = _schema_missing_reason(job_type, payload, result)
        schema_errors = _stage_schema_errors(job_type, result)
        schema_reason = "; ".join(schema_errors[:6]) if schema_errors else None
        invalid_reason = missing_reason or schema_reason
        if invalid_reason:
            _log_stage("stage-schema-invalid", job, payload, reason=invalid_reason, missing_reason=missing_reason, schema_errors=schema_errors[:8], result=result)
            try:
                result = _repair_invalid_stage_result(job, payload, job_type, result, schema_errors=schema_errors)
                if job_type == "assistant_chat_turn":
                    result = _normalize_chat_turn_result(payload, result)
                    result, _ = _apply_chat_strict_mode(payload, result)
            except Exception as ex:
                _log_stage("stage-schema-repair-failed", job, payload, error=ex)
            missing_reason = _schema_missing_reason(job_type, payload, result)
            schema_errors = _stage_schema_errors(job_type, result)
            schema_reason = "; ".join(schema_errors[:6]) if schema_errors else None
            invalid_reason = missing_reason or (None if SCHEMA_VALIDATION_MODE == "soft" else schema_reason)
            if invalid_reason:
                _log_stage("stage-schema-fallback", job, payload, reason=invalid_reason, schema_errors=schema_errors[:8])
                fallback_job = dict(job)
                import json as _json
                fallback_job["inputJson"] = _json.dumps(payload, ensure_ascii=False)
                if job_type == "assignment_course_profile_build":
                    result = sanitize_result_payload(job_type, payload, fallback_course_profile(payload, job))
                elif job_type == "assignment_gap_analysis":
                    result = sanitize_result_payload(job_type, payload, fallback_gap_analysis(payload, job))
                elif job_type == "assistant_chat_turn":
                    result = {"assistantMessage": "Мне не хватило надёжных данных, чтобы безопасно выбрать действие. Уточни запрос короче или выбери сущность явно.", "actions": [], "sessionTitle": _chat_build_session_title(payload)}
                else:
                    result = sanitize_result_payload(job_type, payload, fallback_result(fallback_job))
        _log_stage("stage-done", job, payload, result=result)
        return result

    def _ollama_stage(prompt_builder, fallback_fn=None, sanitize=True, allow_fallback=True, stage_name: str | None = None):
        builder_name = getattr(prompt_builder, "__name__", "prompt_builder")
        _log_stage("stage-prepare-prompt", job, payload, builder=builder_name)
        prompt = prompt_builder(job, payload)
        llm_cfg = _stage_llm_config(job_type, payload, retry_count)
        if stage_name:
            llm_cfg.stage = stage_name
            llm_cfg = _with_stage_schema(llm_cfg, job_type, retry_count, str(payload.get("__compactMode") or "").strip().lower())
        _log_stage("stage-prompt-ready", job, payload, builder=builder_name, prompt_len=len(prompt), prompt_preview=(preview_text(prompt, PROMPT_PREVIEW_CHARS) if INCLUDE_PROMPTS else None), timeout=llm_cfg.timeout, num_predict=llm_cfg.num_predict)
        try:
            result = call_llm(prompt, llm_cfg)
        except Exception as ex:
            logger.warning(f"llm provider failed for {job_type}: {ex} [{_job_context(job, payload)}]")
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
            _log_stage("stage-sanitized", job, payload, result=result)
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
        use_substages = _use_draft_substages(payload, retry_count)
        _log_stage("stage-draft-mode", job, payload, use_substages=use_substages)
        if use_substages:
            result = _generate_draft_via_substages(job, payload, retry_count)
        else:
            result = _ollama_stage(build_draft_generate_prompt, sanitize=True, allow_fallback=True, stage_name="draft_generate")
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
            result = call_llm(prompt, _stage_llm_config(job_type, payload, retry_count))
        except Exception as ex:
            logger.warning(f"batch review provider failed: {ex} [{_job_context(job, payload)}]")
            result = run_batch_review(payload, job)
            _log_stage("stage-fallback-result", job, payload, builder="build_batch_review_prompt", fallback="run_batch_review")
        return _finish(result)

    # ── AI chat orchestrator ───────────────────────────
    if job_type == "assistant_chat_turn":
        prompt = build_chat_turn_prompt(job, payload)
        _log_stage("stage-prompt-ready", job, payload, builder="build_chat_turn_prompt", prompt_len=len(prompt))
        try:
            result = call_llm(prompt, _stage_llm_config(job_type, payload, retry_count))
        except Exception as ex:
            logger.warning(f"chat orchestrator provider failed: {ex} [{_job_context(job, payload)}]")
            raise RetryableStageError(f"{job_type} failed: {ex}") from ex
        return _finish(result)

    # ── Draft repair ──────────────────────────────────
    if job_type == "assignment_repair":
        prompt = build_prompt(job, payload)
        _log_stage("stage-prompt-ready", job, payload, builder="build_prompt", prompt_len=len(prompt))
        try:
            result = call_llm(prompt, _stage_llm_config(job_type, payload, retry_count))
        except Exception as ex:
            logger.warning(f"repair provider failed: {ex} [{_job_context(job, payload)}]")
            return _finish(fallback_repair_result(payload, job))
        repaired_draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
        if isinstance(repaired_draft, dict):
            result["draftValidation"] = run_self_check(repaired_draft)
            validation = result.get("draftValidation") if isinstance(result.get("draftValidation"), dict) else {}
            _log_stage("stage-repair-validation", job, payload, validation=validation)
            # Guard: if LLM repair worsened the draft significantly, fall back to deterministic repair
            pre_score = float((payload.get("scorecard") or {}).get("overallScore") or 0)
            post_score = float(validation.get("score") or 0)
            if pre_score > 0 and post_score < pre_score * 0.7:
                _log_stage("stage-repair-regression", job, payload, pre_score=pre_score, post_score=post_score)
                return _finish(fallback_repair_result(payload, job))
        return _finish(result)

    # ── Default: generic generation ───────────────────
    prompt = build_prompt(job, payload)
    _log_stage("stage-prompt-ready", job, payload, builder="build_prompt", prompt_len=len(prompt))
    try:
        result = call_llm(prompt, _stage_llm_config(job_type, payload, retry_count))
    except Exception as ex:
        logger.warning(f"llm provider failed for {job_type}: {ex} [{_job_context(job, payload)}]")
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
    log_event('worker-started', api=API_BASE, worker=WORKER_ID, provider=ACTIVE_PROVIDER, model=ACTIVE_MODEL, poll_seconds=POLL_INTERVAL)
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
            log_event('job-processing', job_context=_job_context(job), retry_count=_job_retry_count(job))
            result = process_job(job)
            log_event('job-completing', job_context=_job_context(job), **_result_overview(result))
            complete(job_id, result)
            elapsed = int((time.time() - loop_started) * 1000)
            log_event('job-done', job_context=_job_context(job), elapsed_ms=elapsed)
        except KeyboardInterrupt:
            raise
        except RetryableStageError as ex:
            log_event('job-retryable-error', level='warning', error=str(ex), job_context=_job_context(job or {}, (parse_payload(job.get('inputJson')) if isinstance(job, dict) and job.get('inputJson') else None) if job else None))
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
            log_event('worker-loop-error', level='error', error_type=type(ex).__name__, error=str(ex), job_context=_job_context(job or {}, (parse_payload(job.get('inputJson')) if isinstance(job, dict) and job.get('inputJson') else None) if job else None))
            if job_id:
                try:
                    fail(job_id, f"{type(ex).__name__}: {ex}")
                except Exception as fail_ex:
                    logger.error(f"fail() call also failed: {fail_ex}")
            time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
