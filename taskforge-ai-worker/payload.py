"""Payload parsing, compacting, sanitisation and beginner-track detection."""

import json
import re
from typing import Any, Dict, List

from config import (
    MAX_REFERENCE_ASSIGNMENTS,
    MAX_REFERENCE_DESCRIPTION_LEN,
    GAP_ANALYSIS_REFERENCE_ASSIGNMENTS,
    GAP_ANALYSIS_REFERENCE_DESCRIPTION_LEN,
    BATCH_PLAN_REFERENCE_ASSIGNMENTS,
    BATCH_PLAN_REFERENCE_ASSIGNMENTS_RETRY,
    BATCH_PLAN_REFERENCE_DESCRIPTION_LEN,
)
from log import log, log_debug, logger
from text_utils import (
    normalize_text,
    truncate_text,
    summarize_description,
    unique_string_list,
    strip_conflicting_lists,
    has_html_markup,
    safe_int,
)


# ── Parse / pretty-print ────────────────────────────

def parse_payload(job: Dict[str, Any]) -> Dict[str, Any]:
    try:
        raw = job.get("inputJson") or "{}"
        value = json.loads(raw)
        if isinstance(value, dict):
            log_debug(f"payload keys={list(value.keys())[:15]} len={len(raw)}")
            return value
        logger.warning(f"payload is {type(value).__name__}, expected dict")
        return {}
    except Exception as ex:
        logger.error(f"payload parse failed: {ex}")
        return {}


def pretty_payload(job: Dict[str, Any]) -> str:
    payload = parse_payload(job)
    return json.dumps(payload, ensure_ascii=False, indent=2)


def files_text(job: Dict[str, Any]) -> str:
    lines: List[str] = []
    for f in job.get("files") or []:
        name = f.get("originalName") or f.get("fileKey") or "unknown-file"
        url = f.get("publicUrl") or ""
        mime = f.get("mimeType") or ""
        extra = f" ({mime})" if mime else ""
        lines.append(f"- {name}{extra}: {url}" if url else f"- {name}{extra}")
    return "\n".join(lines) if lines else "- no files attached"


# ── Beginner-track detection ─────────────────────────

def detect_beginner_char_array_track(payload: Dict[str, Any]) -> bool:
    prompt = normalize_text(payload.get("prompt")).lower()
    if not prompt or "char" not in prompt or "c++" not in prompt:
        return False
    beginner_markers = ["прост", "вводн", "базов", "нович", "с нуля", "beginner", "intro"]
    advanced_markers = ["strcat", "strcpy", "strlen", "cstring", "fgets", "scanf", "printf"]
    return any(m in prompt for m in beginner_markers) and not any(m in prompt for m in advanced_markers)


# ── Compact helpers ──────────────────────────────────

def compact_reference_assignments(
    payload: Dict[str, Any],
    *,
    limit: int | None = None,
    description_len: int | None = None,
    include_cases: bool = True,
) -> List[Dict[str, Any]]:
    refs = payload.get("referenceAssignments")
    if not isinstance(refs, list):
        return []
    max_items = max(1, limit or MAX_REFERENCE_ASSIGNMENTS)
    max_desc_len = max(80, description_len or MAX_REFERENCE_DESCRIPTION_LEN)
    compact: List[Dict[str, Any]] = []
    for idx, item in enumerate(refs[:max_items], start=1):
        if not isinstance(item, dict):
            continue
        compact_item = {
            "index": idx,
            "id": item.get("id"),
            "courseId": item.get("courseId"),
            "type": item.get("type"),
            "title": truncate_text(item.get("title"), 120 if max_desc_len <= 160 else 160),
            "descriptionSummary": summarize_description(
                item.get("description") or item.get("Description"),
                max_desc_len,
            ),
            "difficulty": item.get("difficulty"),
            "rating": item.get("rating"),
            "tags": truncate_text(item.get("tags"), 120),
            "allowedLanguagesCsv": truncate_text(item.get("allowedLanguagesCsv"), 80),
            "hiddenTestsCount": item.get("hiddenTestsCount"),
            "blocksCount": item.get("blocksCount"),
            "questionsCount": item.get("questionsCount"),
            "forbiddenCalls": item.get("forbiddenCalls")[:6] if isinstance(item.get("forbiddenCalls"), list) else [],
            "requiredCalls": item.get("requiredCalls")[:6] if isinstance(item.get("requiredCalls"), list) else [],
        }
        if include_cases:
            compact_item["publicCases"] = item.get("publicCases")[:2] if isinstance(item.get("publicCases"), list) else []
        compact.append(compact_item)
    return compact


def compact_historical_planner_priors(payload: Dict[str, Any]) -> Dict[str, Any]:
    priors = payload.get("historicalPlannerPriors")
    if not isinstance(priors, dict):
        return {}
    return {
        "sourceBatchCount": priors.get("sourceBatchCount") or 0,
        "publicationOutcomes": (priors.get("publicationOutcomes")[:6] if isinstance(priors.get("publicationOutcomes"), list) else []),
        "strongSkills": (priors.get("strongSkills")[:8] if isinstance(priors.get("strongSkills"), list) else []),
        "weakSkills": (priors.get("weakSkills")[:8] if isinstance(priors.get("weakSkills"), list) else []),
        "recurringRepairRoutes": (priors.get("recurringRepairRoutes")[:6] if isinstance(priors.get("recurringRepairRoutes"), list) else []),
        "riskyTransitions": (priors.get("riskyTransitions")[:8] if isinstance(priors.get("riskyTransitions"), list) else []),
        "antiPatterns": (priors.get("antiPatterns")[:10] if isinstance(priors.get("antiPatterns"), list) else []),
    }


def compact_historical_slot_priors(payload: Dict[str, Any]) -> Dict[str, Any]:
    priors = payload.get("historicalSlotPriors")
    if not isinstance(priors, dict):
        return {}
    return {
        "targetSkill": normalize_text(priors.get("targetSkill")),
        "difficultyTarget": priors.get("difficultyTarget"),
        "sourceItemCount": priors.get("sourceItemCount") or 0,
        "recommendedDifficultyBand": priors.get("recommendedDifficultyBand"),
        "strongExamples": (priors.get("strongExamples")[:5] if isinstance(priors.get("strongExamples"), list) else []),
        "weakExamples": (priors.get("weakExamples")[:5] if isinstance(priors.get("weakExamples"), list) else []),
        "routeHints": (priors.get("routeHints")[:5] if isinstance(priors.get("routeHints"), list) else []),
    }


def _trim_list(value: Any, limit: int) -> List[Any]:
    return value[:limit] if isinstance(value, list) else []


def _slim_policy_profile(value: Any) -> Dict[str, Any]:
    policy = value if isinstance(value, dict) else {}
    result: Dict[str, Any] = {}
    for key, limit in (("allowedLanguages", 6), ("requiredCalls", 6), ("forbiddenCalls", 6), ("enforcedMethods", 6), ("forbiddenFunctions", 6)):
        items = unique_string_list(policy.get(key), limit)
        if items:
            result[key] = items
    for key in ("ioStyle", "descriptionStyle", "notes"):
        value = truncate_text(policy.get(key), 120)
        if value:
            result[key] = value
    return result


def _slim_style_profile(value: Any) -> Dict[str, Any]:
    style = value if isinstance(value, dict) else {}
    result: Dict[str, Any] = {}
    for key in ("descriptionFormat", "tone", "complexity", "testStyle", "notes"):
        value = truncate_text(style.get(key), 120)
        if value:
            result[key] = value
    return result


def slim_course_profile_for_stage(value: Any, *, ultra: bool = False) -> Dict[str, Any]:
    compact = compact_course_profile(value)
    return {
        "dominantSkills": compact.get("dominantSkills", [])[: (4 if ultra else 6)],
        "difficultyDistribution": compact.get("difficultyDistribution") or {},
        "styleProfile": _slim_style_profile(compact.get("styleProfile")),
        "policyProfile": _slim_policy_profile(compact.get("policyProfile")),
        "negativePatterns": compact.get("negativePatterns", [])[: (4 if ultra else 6)],
    }


def slim_gap_analysis_for_stage(value: Any, *, ultra: bool = False) -> Dict[str, Any]:
    compact = compact_gap_analysis_object(value)
    limit = 4 if ultra else 6
    return {
        "coveredTopics": compact.get("coveredTopics", [])[:limit],
        "missingTopics": compact.get("missingTopics", [])[:limit],
        "weakCoverageTopics": compact.get("weakCoverageTopics", [])[:limit],
        "recommendedFocus": compact.get("recommendedFocus", [])[:limit],
        "curriculumRisks": compact.get("curriculumRisks", [])[:limit],
    }


def compact_course_profile(value: Any) -> Dict[str, Any]:
    cp = value if isinstance(value, dict) else {}
    return {
        "dominantSkills": unique_string_list(cp.get("dominantSkills"), 8),
        "difficultyDistribution": cp.get("difficultyDistribution") if isinstance(cp.get("difficultyDistribution"), dict) else {},
        "styleProfile": cp.get("styleProfile") if isinstance(cp.get("styleProfile"), dict) else {},
        "policyProfile": cp.get("policyProfile") if isinstance(cp.get("policyProfile"), dict) else {},
        "testProfile": cp.get("testProfile") if isinstance(cp.get("testProfile"), dict) else {},
        "assignmentOntology": cp.get("assignmentOntology") if isinstance(cp.get("assignmentOntology"), dict) else {},
        "exemplarSignals": cp.get("exemplarSignals") if isinstance(cp.get("exemplarSignals"), (dict, list)) else cp.get("exemplarSignals"),
        "negativePatterns": unique_string_list(cp.get("negativePatterns"), 10),
    }


def compact_gap_analysis_object(value: Any) -> Dict[str, Any]:
    ga = value if isinstance(value, dict) else {}
    return {
        "coveredTopics": unique_string_list(ga.get("coveredTopics"), 8),
        "missingTopics": unique_string_list(ga.get("missingTopics"), 8),
        "weakCoverageTopics": unique_string_list(ga.get("weakCoverageTopics"), 6),
        "duplicateClusters": unique_string_list(ga.get("duplicateClusters"), 6),
        "recommendedFocus": unique_string_list(ga.get("recommendedFocus"), 8),
        "curriculumRisks": unique_string_list(ga.get("curriculumRisks"), 8),
    }


def compact_plan_object(value: Any) -> Dict[str, Any]:
    plan = value if isinstance(value, dict) else {}
    tasks: List[Dict[str, Any]] = []
    for item in (plan.get("tasks") if isinstance(plan.get("tasks"), list) else [])[:8]:
        if not isinstance(item, dict):
            continue
        tasks.append({
            "index": item.get("index"),
            "targetSkill": normalize_text(item.get("targetSkill")),
            "microGoal": truncate_text(item.get("microGoal"), 220),
            "difficultyTarget": item.get("difficultyTarget"),
            "whyItExists": truncate_text(item.get("whyItExists"), 220),
            "antiDuplicateHints": unique_string_list(item.get("antiDuplicateHints"), 5),
            "decisionLog": (item.get("decisionLog")[:3] if isinstance(item.get("decisionLog"), list) else []),
        })
    return {"tasks": tasks}


def compact_brief_object(value: Any) -> Dict[str, Any]:
    brief = value if isinstance(value, dict) else {}
    return {
        "titleHint": truncate_text(brief.get("titleHint"), 160),
        "summary": truncate_text(brief.get("summary"), 260),
        "generationPrompt": truncate_text(brief.get("generationPrompt"), 600),
        "sourceText": truncate_text(brief.get("sourceText"), 400),
        "notes": truncate_text(brief.get("notes"), 320),
        "difficultyTarget": brief.get("difficultyTarget"),
        "targetSkill": truncate_text(brief.get("targetSkill"), 160),
        "decisionLog": (brief.get("decisionLog")[:3] if isinstance(brief.get("decisionLog"), list) else []),
    }


def compact_brief_review_object(value: Any) -> Dict[str, Any]:
    review = value if isinstance(value, dict) else {}
    checks: List[Dict[str, Any]] = []
    for item in (review.get("checks") if isinstance(review.get("checks"), list) else [])[:8]:
        if isinstance(item, dict):
            checks.append({
                "name": normalize_text(item.get("name")),
                "status": normalize_text(item.get("status")),
                "details": truncate_text(item.get("details"), 180),
            })
    findings: List[Dict[str, Any]] = []
    for item in (review.get("findings") if isinstance(review.get("findings"), list) else [])[:6]:
        if isinstance(item, dict):
            findings.append({
                "severity": normalize_text(item.get("severity")),
                "code": normalize_text(item.get("code")),
                "message": truncate_text(item.get("message"), 180),
            })
    return {
        "status": normalize_text(review.get("status")),
        "score": review.get("score"),
        "checks": checks,
        "findings": findings,
        "titleHint": truncate_text(review.get("titleHint"), 160),
    }


def build_request_signals(payload: Dict[str, Any]) -> Dict[str, Any]:
    prompt = normalize_text(payload.get("prompt"))
    prompt_low = prompt.lower()
    refs = compact_reference_assignments(payload, limit=8, description_len=100, include_cases=False)
    ref_titles = [normalize_text(r.get("title")) for r in refs if normalize_text(r.get("title"))]
    tokens = [x for x in re.split(r"[^\wа-яА-Я]+", prompt) if len(x) >= 4]
    unique_tokens: List[str] = []
    seen: set[str] = set()
    for token in tokens:
        low = token.lower()
        if low in seen:
            continue
        seen.add(low)
        unique_tokens.append(token)
    return {
        "assignmentType": normalize_text(payload.get("assignmentType") or "code-test") or "code-test",
        "mode": normalize_text(payload.get("mode") or "topic-pack") or "topic-pack",
        "count": max(1, safe_int(payload.get("count"), 1)),
        "difficulty": max(1, min(5, safe_int(payload.get("difficulty"), 2))),
        "domainHints": [
            hint for hint in [
                "matrix" if ("матриц" in prompt_low or "matrix" in prompt_low) else "",
                "oop" if ("ооп" in prompt_low or "oop" in prompt_low or "класс" in prompt_low or "class" in prompt_low) else "",
                "performance" if ("оптим" in prompt_low or "эффектив" in prompt_low or "fast" in prompt_low or "sparse" in prompt_low) else "",
            ] if hint
        ],
        "mustInclude": unique_tokens[:8],
        "referenceTitleHints": ref_titles[:5],
        "sourcePrompt": truncate_text(prompt, 220),
    }


def build_course_digest(payload: Dict[str, Any]) -> Dict[str, Any]:
    refs = compact_reference_assignments(payload, limit=8, description_len=120, include_cases=False)
    langs: List[str] = []
    types: List[str] = []
    title_hints: List[str] = []
    seen_langs: set[str] = set()
    seen_types: set[str] = set()
    seen_titles: set[str] = set()
    for ref in refs:
        title = normalize_text(ref.get("title"))
        if title and title.casefold() not in seen_titles:
            seen_titles.add(title.casefold())
            title_hints.append(title[:90])
        for lang in str(ref.get("allowedLanguagesCsv") or "").split(','):
            lang = normalize_text(lang).lower()
            if lang and lang not in seen_langs:
                seen_langs.add(lang)
                langs.append(lang)
        typ = normalize_text(ref.get("type")).lower()
        if typ and typ not in seen_types:
            seen_types.add(typ)
            types.append(typ)
    return {
        "referenceCount": len(refs),
        "assignmentTypes": types[:4],
        "languages": langs[:6],
        "recentReferenceTitles": title_hints[:5],
        "teachingStyle": [
            "html-description",
            "public-and-hidden-tests",
            "structured-i/o",
        ],
    }


def build_gap_digest(payload: Dict[str, Any]) -> Dict[str, Any]:
    gap_raw = payload.get("gapAnalysis") if isinstance(payload.get("gapAnalysis"), dict) else {}
    ga = gap_raw.get("gapAnalysis") if isinstance(gap_raw.get("gapAnalysis"), dict) else gap_raw
    if not isinstance(ga, dict):
        ga = {}
    return {
        "coveredTopics": _trim_list(ga.get("coveredTopics"), 6),
        "missingTopics": _trim_list(ga.get("missingTopics"), 6),
        "weakCoverageTopics": _trim_list(ga.get("weakCoverageTopics"), 4),
        "recommendedFocus": _trim_list(ga.get("recommendedFocus"), 4),
        "summary": truncate_text(gap_raw.get("summary") or ga.get("summary"), 180),
    }


def compact_payload_for_stage(job_type: str, payload: Dict[str, Any]) -> Dict[str, Any]:
    compact: Dict[str, Any] = {}
    job_type = normalize_text(job_type).lower()
    compact_mode = normalize_text(payload.get("__compactMode")).lower()
    is_course_stage = job_type == "assignment_course_profile_build"
    is_gap_stage = job_type == "assignment_gap_analysis"
    is_planner_stage = job_type in {"assignment_batch_plan", "assignment_batch_replan"}
    is_brief_stage = job_type in {"assignment_brief_generate", "assignment_brief_repair"}
    ultra_compact = compact_mode == "ultra"

    keep_scalar = ["assignmentType", "mode", "count", "difficulty", "prompt", "batchId", "courseId", "requestType", "batchItemId"]
    if is_planner_stage:
        keep_scalar = ["assignmentType", "mode", "count", "difficulty", "prompt", "batchId", "courseId", "requestType"]
    elif is_gap_stage:
        keep_scalar = ["assignmentType", "mode", "count", "prompt", "batchId", "courseId", "requestType"]
    elif is_course_stage:
        keep_scalar = ["assignmentType", "mode", "count", "difficulty", "prompt", "batchId", "courseId"]

    for key in keep_scalar:
        if key in payload:
            value = payload.get(key)
            compact[key] = truncate_text(value, 180 if ultra_compact else 240) if isinstance(value, str) else value

    compact["requestSignals"] = build_request_signals(payload)
    compact["courseDigest"] = build_course_digest(payload)

    if not is_course_stage and isinstance(payload.get("courseProfile"), dict):
        cp_raw = payload["courseProfile"]
        compact["courseProfile"] = {
            "summary": truncate_text(cp_raw.get("summary"), 160 if ultra_compact else 220),
            "canonicalRequest": cp_raw.get("canonicalRequest") if isinstance(cp_raw.get("canonicalRequest"), dict) else None,
            "courseDigest": cp_raw.get("courseDigest") if isinstance(cp_raw.get("courseDigest"), dict) else build_course_digest(payload),
            "courseProfile": slim_course_profile_for_stage(cp_raw.get("courseProfile") if isinstance(cp_raw.get("courseProfile"), dict) else {}, ultra=ultra_compact),
        }

    if not is_course_stage and isinstance(payload.get("gapAnalysis"), dict):
        ga_raw = payload["gapAnalysis"]
        compact["gapAnalysis"] = {
            "summary": truncate_text(ga_raw.get("summary"), 160 if ultra_compact else 220),
            "gapDigest": build_gap_digest(payload),
            "coverage": ga_raw.get("coverage") if isinstance(ga_raw.get("coverage"), dict) else {},
            "gapAnalysis": slim_gap_analysis_for_stage(ga_raw.get("gapAnalysis") if isinstance(ga_raw.get("gapAnalysis"), dict) else {}, ultra=ultra_compact),
        }

    ref_limit = 6
    ref_desc_len = 110
    include_cases = False
    if is_course_stage:
        ref_limit = 5 if ultra_compact else 6
        ref_desc_len = 90
    elif is_gap_stage:
        ref_limit = 4 if ultra_compact else 5
        ref_desc_len = 90
    elif is_planner_stage:
        ref_limit = 3 if ultra_compact else 4
        ref_desc_len = 80
    elif is_brief_stage:
        ref_limit = 4
        ref_desc_len = 100
        include_cases = True

    compact["referenceAssignments"] = compact_reference_assignments(payload, limit=ref_limit, description_len=ref_desc_len, include_cases=include_cases)

    priors = compact_historical_planner_priors(payload)
    if priors and (is_planner_stage or is_brief_stage):
        compact["historicalPlannerPriors"] = {
            "sourceBatchCount": priors.get("sourceBatchCount") or 0,
            "strongSkills": _trim_list(priors.get("strongSkills"), 4 if ultra_compact else 5),
            "weakSkills": _trim_list(priors.get("weakSkills"), 4 if ultra_compact else 5),
            "riskyTransitions": _trim_list(priors.get("riskyTransitions"), 3 if ultra_compact else 4),
            "antiPatterns": _trim_list(priors.get("antiPatterns"), 3 if ultra_compact else 4),
        }

    slot_priors = compact_historical_slot_priors(payload)
    if slot_priors and is_brief_stage:
        compact["historicalSlotPriors"] = slot_priors

    if isinstance(payload.get("plan"), dict):
        compact["plan"] = compact_plan_object(payload["plan"])
    if isinstance(payload.get("task"), dict):
        task = payload["task"]
        compact["task"] = {
            "index": task.get("index") or task.get("Index"),
            "targetSkill": task.get("targetSkill") or task.get("TargetSkill"),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 180),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
            "whyItExists": truncate_text(task.get("whyItExists") or task.get("WhyItExists"), 180),
            "antiDuplicateHints": unique_string_list(task.get("antiDuplicateHints") or task.get("AntiDuplicateHints"), 4),
        }
    if isinstance(payload.get("brief"), dict):
        compact["brief"] = compact_brief_object(payload["brief"])
    if isinstance(payload.get("briefReview"), dict):
        compact["briefReview"] = compact_brief_review_object(payload["briefReview"])

    optional_memory_keys = ["plannerFeedback", "decisionLogDigest"] if (is_course_stage or is_gap_stage or is_planner_stage) else ["plannerFeedback", "decisionLogDigest", "antiPatternMemory", "institutionalMemory"]
    for key in optional_memory_keys:
        value = payload.get(key)
        if isinstance(value, dict):
            compact[key] = value
        elif isinstance(value, list):
            compact[key] = value[: (3 if ultra_compact else 6)]

    return compact


# ── Historical-skill bias extraction ─────────────────

def extract_historical_skill_biases(payload: Dict[str, Any]) -> Dict[str, List[str]]:
    priors = compact_historical_planner_priors(payload)
    strong: List[str] = []
    weak: List[str] = []
    for item in priors.get("strongSkills") or []:
        if isinstance(item, dict):
            value = normalize_text(item.get("targetSkill") or item.get("skill"))
            if value:
                strong.append(value)
    for item in priors.get("weakSkills") or []:
        if isinstance(item, dict):
            value = normalize_text(item.get("targetSkill") or item.get("skill"))
            if value:
                weak.append(value)
    return {"strong": strong[:10], "weak": weak[:10]}


# ── Result sanitisation ──────────────────────────────

def sanitize_result_payload(
    job_type: str, payload: Dict[str, Any], result: Dict[str, Any]
) -> Dict[str, Any]:
    if not isinstance(result, dict):
        return result
    sanitized = json.loads(json.dumps(result, ensure_ascii=False))

    def _sanitize_policy_dict(target: Dict[str, Any], prefer_key: str, block_key: str):
        preferred, blocked, overlap = strip_conflicting_lists(target.get(prefer_key), target.get(block_key))
        if preferred or prefer_key in target:
            target[prefer_key] = preferred
        if blocked or block_key in target:
            target[block_key] = blocked
        if overlap:
            log(
                "sanitized policy overlap",
                {"jobType": job_type, "preferKey": prefer_key, "blockKey": block_key, "overlap": overlap[:8]},
            )

    if isinstance(sanitized.get("courseProfile"), dict):
        cp = sanitized["courseProfile"]
        policy = cp.get("policyProfile") if isinstance(cp.get("policyProfile"), dict) else None
        if isinstance(policy, dict):
            _sanitize_policy_dict(policy, "enforcedMethods", "forbiddenFunctions")
            policy["enforcedMethods"] = unique_string_list(policy.get("enforcedMethods"), 8)
            policy["forbiddenFunctions"] = unique_string_list(policy.get("forbiddenFunctions"), 10)
        if detect_beginner_char_array_track(payload):
            negative = unique_string_list(cp.get("negativePatterns"), 12)
            extra = [
                "не смешивай cout/cin стиль с scanf/printf/fgets в одной beginner-задаче",
                "не прыгай сразу в cstring-функции, если не запрошены явно",
                "не делай взаимоисключающие policy rules",
            ]
            cp["negativePatterns"] = unique_string_list(negative + extra, 12)

    if isinstance(sanitized.get("plan"), dict) and isinstance(sanitized["plan"].get("tasks"), list):
        beginner_track = detect_beginner_char_array_track(payload)
        seen_skills: set = set()
        for idx, task in enumerate(sanitized["plan"]["tasks"], start=1):
            if not isinstance(task, dict):
                continue
            skill = normalize_text(task.get("targetSkill"))
            micro = normalize_text(task.get("microGoal"))
            if beginner_track and any(
                x in (skill + " " + micro).lower()
                for x in ["fgets", "strcat", "strcpy", "strlen", "cstring"]
            ):
                replacements = [
                    ("char array initialization and output", "Объявить char[] и вывести его посимвольно или целиком через cout", 1),
                    ("char array input with cin", "Считать одно слово в char[] через cin и вывести его", 1),
                    ("char array length with loop", "Найти длину char[] вручную циклом до \\0", 2),
                    ("char array symbol replacement", "Заменить указанный символ в char[] и вывести результат", 2),
                    ("char array comparison basics", "Сравнить два коротких char[] посимвольно и вывести yes/no", 2),
                ]
                repl = replacements[(idx - 1) % len(replacements)]
                task["targetSkill"], task["microGoal"], task["difficultyTarget"] = repl
                hints = unique_string_list(task.get("antiDuplicateHints"), 6)
                task["antiDuplicateHints"] = unique_string_list(
                    hints + ["не использовать fgets/strcat/strcpy/strlen без явного запроса"], 6
                )
            low = normalize_text(task.get("targetSkill")).casefold()
            if low in seen_skills and low:
                task["targetSkill"] = f"{task.get('targetSkill')} #{idx}"
            if low:
                seen_skills.add(low)

    if job_type == "assignment_brief_generate":
        beginner_track = detect_beginner_char_array_track(payload)
        if beginner_track:
            gp = normalize_text(sanitized.get("generationPrompt"))
            bad = ["fgets", "strcat", "strcpy", "strlen", "cstring", "scanf", "printf"]
            if any(x in gp.lower() for x in bad):
                task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
                skill = normalize_text(
                    task.get("targetSkill") or task.get("TargetSkill")
                    or sanitized.get("targetSkill") or "char array basics"
                )
                micro = normalize_text(
                    task.get("microGoal") or task.get("MicroGoal")
                    or sanitized.get("summary") or "Одно простое действие с char[]"
                )
                sanitized["generationPrompt"] = (
                    f"Создай одну простую beginner-задачу по C++ на тему char[]. "
                    f"Учебная цель: {micro}. Разрешены только базовые операции: объявление char[], "
                    f"ввод/вывод через cin/cout, цикл по символам, поиск длины вручную, простая замена символов. "
                    f"Не используй scanf/printf/fgets/cstring-функции, если они не запрошены явно. "
                    f"Цель должна быть одна: {skill}."
                )
                sanitized["notes"] = truncate_text(
                    (normalize_text(sanitized.get("notes")) + " Не смешивай C-style IO и cstring-функции в beginner-задаче.").strip(),
                    320,
                )
        sanitized["sourceText"] = truncate_text(sanitized.get("sourceText") or payload.get("prompt"), 400)

    if isinstance(sanitized.get("policyPack"), dict):
        pp = sanitized["policyPack"]
        _sanitize_policy_dict(pp, "requiredCalls", "forbiddenCalls")
        _sanitize_policy_dict(pp, "enforcedMethods", "forbiddenFunctions")

    return sanitized
