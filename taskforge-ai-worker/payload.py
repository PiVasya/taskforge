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



def _ensure_canonical_request(payload: Dict[str, Any], result: Dict[str, Any] | None = None) -> Dict[str, Any]:
    result = result or {}
    existing = result.get("canonicalRequest") if isinstance(result.get("canonicalRequest"), dict) else {}
    prompt = normalize_text(payload.get("prompt"))
    prompt_low = prompt.lower()
    domain = normalize_text(existing.get("domain")) or ("matrix" if ("matrix" in prompt_low or "матриц" in prompt_low) else "general")
    difficulty = safe_int(existing.get("difficulty"), safe_int(payload.get("difficulty"), 3))
    count = max(1, safe_int(existing.get("count"), safe_int(payload.get("count"), 1)))
    must_include = unique_string_list(existing.get("mustInclude"), 8)
    if not must_include:
        must_include = ["matrices", "complex"] if domain == "matrix" else unique_string_list([payload.get("assignmentType") or "task"], 4)
    avoid = unique_string_list(existing.get("avoid"), 8)
    if not avoid:
        avoid = ["generic titles", "duplicate tasks"]
    return {
        "domain": domain,
        "count": count,
        "difficulty": difficulty,
        "mustInclude": must_include,
        "avoid": avoid,
    }


def _synthesize_gap_analysis(payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    canonical = _ensure_canonical_request(payload, result)
    digest = result.get("courseDigest") if isinstance(result.get("courseDigest"), dict) else build_course_digest(payload)
    refs = compact_reference_assignments(payload, limit=GAP_ANALYSIS_REFERENCE_ASSIGNMENTS, description_len=GAP_ANALYSIS_REFERENCE_DESCRIPTION_LEN, include_cases=False)
    covered = unique_string_list([*(digest.get("dominantSkills") or []), canonical.get("domain")], 6)
    if canonical.get("domain") == "matrix":
        missing = ["advanced matrix multiplication", "matrix transformations", "matrix optimization"]
    else:
        missing = [canonical.get("domain") or "topic"]
    return {
        "gapAnalysis": {
            "coveredTopics": covered[:6],
            "missingTopics": missing[:6],
            "weakCoverageTopics": unique_string_list(digest.get("negativePatterns"), 4),
            "duplicateClusters": ["generic introductory tasks"] if canonical.get("domain") == "matrix" else [],
            "recommendedFocus": unique_string_list([*canonical.get("mustInclude", []), *missing[:2]], 6),
            "curriculumRisks": ["avoid generic tasks", "avoid duplicate task shapes"],
        },
        "coverage": {
            "matchedReferenceCount": len(refs),
            "coverageBand": "medium" if refs else "low",
        },
        "summary": f"Gap analysis repaired for {canonical.get('domain')} domain with {len(refs)} references.",
        "decisionSummary": {"confidence": "medium", "source": "schema-repair-gap"},
    }


def _normalize_plan_tasks(tasks_value: Any) -> List[Dict[str, Any]]:
    source = tasks_value if isinstance(tasks_value, list) else []
    tasks: List[Dict[str, Any]] = []
    for idx, item in enumerate(source, start=1):
        if not isinstance(item, dict):
            continue
        tasks.append({
            "index": safe_int(item.get("index"), idx),
            "titleHint": normalize_text(item.get("titleHint") or item.get("title") or item.get("targetSkill") or item.get("skill") or f"Task {idx}"),
            "targetSkill": normalize_text(item.get("targetSkill") or item.get("skill") or item.get("titleHint") or item.get("title") or f"Task {idx}"),
            "primarySkill": normalize_text(item.get("primarySkill") or item.get("targetSkill") or item.get("skill") or "problem solving"),
            "microGoal": normalize_text(item.get("microGoal") or item.get("goal") or item.get("summary") or item.get("title") or f"Task {idx}"),
            "uniqueAngle": normalize_text(item.get("uniqueAngle") or item.get("angle") or item.get("whyItExists") or item.get("microGoal") or "non-duplicate slot"),
            "difficultyTarget": safe_int(item.get("difficultyTarget"), safe_int(item.get("difficulty"), 2)),
            "mustInclude": unique_string_list(item.get("mustInclude"), 6),
            "antiDuplicateHints": unique_string_list(item.get("antiDuplicateHints"), 6),
            "whyItExists": normalize_text(item.get("whyItExists") or item.get("microGoal") or item.get("summary") or "planned slot"),
            "decisionLog": item.get("decisionLog")[:3] if isinstance(item.get("decisionLog"), list) else [{"stage": "batch_plan", "message": "Normalized planner task."}],
        })
    return tasks


def _synthesize_batch_plan(payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    from fallbacks import build_fallback_plan_tasks

    canonical = _ensure_canonical_request(payload, result)
    plan_obj = result.get("plan") if isinstance(result.get("plan"), dict) else {}
    tasks: List[Dict[str, Any]] = []
    for candidate in (plan_obj.get("tasks"), result.get("tasks"), result.get("items"), result.get("slots")):
        tasks = _normalize_plan_tasks(candidate)
        if tasks:
            break
    if not tasks:
        tasks = _normalize_plan_tasks(build_fallback_plan_tasks({**payload, "count": canonical.get("count"), "difficulty": canonical.get("difficulty")}))
    count = max(1, safe_int(canonical.get("count"), len(tasks) or 1))
    tasks = tasks[:count]
    while len(tasks) < count:
        extra = _normalize_plan_tasks(build_fallback_plan_tasks({**payload, "count": count, "difficulty": canonical.get("difficulty")}))
        for item in extra:
            if len(tasks) >= count:
                break
            candidate = dict(item)
            candidate["index"] = len(tasks) + 1
            tasks.append(candidate)
    for idx, task in enumerate(tasks, start=1):
        task["index"] = idx
        if not task.get("mustInclude"):
            task["mustInclude"] = canonical.get("mustInclude", [])[:4]
        if not task.get("antiDuplicateHints"):
            task["antiDuplicateHints"] = canonical.get("avoid", [])[:4]
    coverage = result.get("coverage") if isinstance(result.get("coverage"), dict) else {"coverageBand": "medium", "noveltyGoal": f"Produce {count} distinct {canonical.get('domain')} tasks"}
    decision_summary = result.get("decisionSummary") if isinstance(result.get("decisionSummary"), dict) else {"confidence": "medium", "source": "schema-repair-plan"}
    return {
        "canonicalRequest": canonical,
        "coverage": coverage,
        "summary": normalize_text(result.get("summary")) or f"Batch plan repaired for {canonical.get('domain')} domain.",
        "decisionSummary": decision_summary,
        "plan": {"tasks": tasks},
    }



def _extract_allowed_languages(payload: Dict[str, Any]) -> List[str]:
    langs = payload.get("allowedLanguages") if isinstance(payload.get("allowedLanguages"), list) else []
    langs = unique_string_list(langs, 6)
    if langs:
        return langs
    schema = payload.get("targetSchema") if isinstance(payload.get("targetSchema"), dict) else {}
    schema_langs = unique_string_list(schema.get("allowedLanguages"), 6)
    if schema_langs:
        return schema_langs
    refs = compact_reference_assignments(payload, limit=8, description_len=60, include_cases=False)
    collected: List[str] = []
    for ref in refs:
        for lang in str(ref.get("allowedLanguagesCsv") or "").split(','):
            lang = normalize_text(lang).lower()
            if lang:
                collected.append(lang)
    langs = unique_string_list(collected, 6)
    return langs or ["python", "cpp", "csharp"]


def _generation_seed_text(payload: Dict[str, Any], result: Dict[str, Any] | None = None) -> str:
    result = result or {}
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    parts = [
        normalize_text(payload.get("prompt")),
        normalize_text(payload.get("titleHint")),
        normalize_text(payload.get("sourceText")),
        normalize_text(payload.get("notes")),
        normalize_text(brief.get("titleHint")),
        normalize_text(brief.get("summary")),
        normalize_text(brief.get("generationPrompt")),
        normalize_text(brief.get("sourceText")),
        normalize_text(brief.get("targetSkill")),
        normalize_text(task.get("targetSkill") or task.get("TargetSkill")),
        normalize_text(task.get("microGoal") or task.get("MicroGoal")),
        normalize_text(result.get("summary")),
    ]
    return " ".join(x for x in parts if x).lower()


def _build_matrix_multiplication_draft(payload: Dict[str, Any]) -> Dict[str, Any]:
    title = normalize_text(payload.get("titleHint")) or "matrix multiplication with dynamic memory"
    allowed = _extract_allowed_languages(payload)
    description = (
        "<p>Реализуйте класс или структуру для работы с матрицами и выполните умножение двух матриц.</p>"
        "<p><strong>Входные данные:</strong> в первой строке заданы три целых числа n, m, k. Далее следуют n строк по m целых чисел матрицы A и затем m строк по k целых чисел матрицы B.</p>"
        "<p><strong>Выходные данные:</strong> выведите произведение A×B в виде n строк по k целых чисел, разделённых пробелами.</p>"
        "<p><strong>Ограничения:</strong> 1 ≤ n, m, k ≤ 40, элементы матриц по модулю не превосходят 10^3.</p>"
        "<p><strong>Примечания:</strong> нужно корректно обработать размеры, не допускать смешения форматов ввода/вывода и предусмотреть эффективную реализацию умножения.</p>"
    )
    code = """import sys

def solve(data: str) -> str:
    nums = list(map(int, data.strip().split()))
    if not nums:
        return ""
    n, m, k = nums[:3]
    pos = 3
    a = []
    for _ in range(n):
        row = nums[pos:pos + m]
        pos += m
        a.append(row)
    b = []
    for _ in range(m):
        row = nums[pos:pos + k]
        pos += k
        b.append(row)
    res = [[0] * k for _ in range(n)]
    for i in range(n):
        for t in range(m):
            av = a[i][t]
            if av == 0:
                continue
            bt = b[t]
            for j in range(k):
                res[i][j] += av * bt[j]
    return "\\n".join(" ".join(map(str, row)) for row in res)

if __name__ == '__main__':
    print(solve(sys.stdin.read()))
"""
    public_tests = [
        {"input": "2 2 2\n1 2\n3 4\n5 6\n7 8\n", "expectedOutput": "19 22\n43 50"},
        {"input": "1 3 1\n2 0 -1\n4\n5\n6\n", "expectedOutput": "2"},
    ]
    hidden_tests = [
        {"input": "1 1 1\n7\n8\n", "expectedOutput": "56"},
        {"input": "2 3 2\n1 0 2\n-1 3 1\n3 1\n2 1\n1 0\n", "expectedOutput": "5 1\n4 2"},
        {"input": "2 2 3\n1 2\n0 1\n1 0 2\n3 4 5\n", "expectedOutput": "7 8 12\n3 4 5"},
        {"input": "3 2 2\n1 1\n2 0\n0 3\n4 1\n2 2\n", "expectedOutput": "6 3\n8 2\n6 6"},
        {"input": "1 2 2\n0 0\n5 6\n7 8\n", "expectedOutput": "0 0"},
    ]
    return {
        "assignmentType": "code-test",
        "title": title,
        "description": description,
        "allowedLanguages": allowed,
        "publicTests": public_tests,
        "hiddenTests": hidden_tests,
        "referenceSolutionPython": code,
        "requiredCalls": ["solve"],
        "forbiddenCalls": ["Process.Start", "__import__"],
        "meta": {"generationSource": "schema-repair", "domain": "matrix", "strategy": "deterministic-matrix-multiplication"},
    }


def _build_matrix_rank_draft(payload: Dict[str, Any]) -> Dict[str, Any]:
    title = normalize_text(payload.get("titleHint")) or "gaussian elimination and matrix rank"
    allowed = _extract_allowed_languages(payload)
    description = (
        "<p>Реализуйте алгоритм Гаусса для приведения матрицы к ступенчатому виду и вычислите её ранг.</p>"
        "<p><strong>Входные данные:</strong> в первой строке заданы n и m. Далее следуют n строк по m целых чисел.</p>"
        "<p><strong>Выходные данные:</strong> выведите одно целое число — ранг матрицы.</p>"
        "<p><strong>Ограничения:</strong> 1 ≤ n, m ≤ 35, элементы по модулю не превосходят 10^4.</p>"
        "<p><strong>Примечания:</strong> нужно корректно обрабатывать линейно зависимые строки, нулевые строки и вырожденные случаи.</p>"
    )
    code = """import sys
from fractions import Fraction

def solve(data: str) -> str:
    nums = list(map(int, data.strip().split()))
    if not nums:
        return ""
    n, m = nums[:2]
    pos = 2
    a = []
    for _ in range(n):
        row = [Fraction(x) for x in nums[pos:pos + m]]
        pos += m
        a.append(row)
    rank = 0
    row = 0
    for col in range(m):
        pivot = None
        for i in range(row, n):
            if a[i][col] != 0:
                pivot = i
                break
        if pivot is None:
            continue
        a[row], a[pivot] = a[pivot], a[row]
        pivot_val = a[row][col]
        for j in range(col, m):
            a[row][j] /= pivot_val
        for i in range(n):
            if i != row and a[i][col] != 0:
                factor = a[i][col]
                for j in range(col, m):
                    a[i][j] -= factor * a[row][j]
        row += 1
        rank += 1
        if row == n:
            break
    return str(rank)

if __name__ == '__main__':
    print(solve(sys.stdin.read()))
"""
    public_tests = [
        {"input": "2 2\n1 2\n2 4\n", "expectedOutput": "1"},
        {"input": "3 3\n1 0 0\n0 1 0\n0 0 1\n", "expectedOutput": "3"},
    ]
    hidden_tests = [
        {"input": "2 3\n1 2 3\n2 4 6\n", "expectedOutput": "1"},
        {"input": "3 2\n1 2\n3 4\n5 6\n", "expectedOutput": "2"},
        {"input": "3 3\n0 0 0\n0 0 0\n0 0 0\n", "expectedOutput": "0"},
        {"input": "3 3\n1 2 3\n0 1 4\n5 6 0\n", "expectedOutput": "3"},
        {"input": "4 4\n1 0 0 0\n0 1 0 0\n0 0 0 0\n0 0 0 0\n", "expectedOutput": "2"},
    ]
    return {
        "assignmentType": "code-test",
        "title": title,
        "description": description,
        "allowedLanguages": allowed,
        "publicTests": public_tests,
        "hiddenTests": hidden_tests,
        "referenceSolutionPython": code,
        "requiredCalls": ["solve"],
        "forbiddenCalls": ["Process.Start", "__import__"],
        "meta": {"generationSource": "schema-repair", "domain": "matrix", "strategy": "deterministic-gaussian-rank"},
    }


def _build_matrix_power_draft(payload: Dict[str, Any]) -> Dict[str, Any]:
    title = normalize_text(payload.get("titleHint")) or "matrix exponentiation under modulus"
    allowed = _extract_allowed_languages(payload)
    description = (
        "<p>Дана квадратная матрица A размера n×n и целое неотрицательное число p. Требуется вычислить матрицу A<sup>p</sup> по модулю M.</p>"
        "<p><strong>Входные данные:</strong> в первой строке заданы n, p и M. Далее следуют n строк по n целых чисел матрицы A.</p>"
        "<p><strong>Выходные данные:</strong> выведите матрицу A<sup>p</sup> по модулю M.</p>"
        "<p><strong>Ограничения:</strong> 1 ≤ n ≤ 20, 0 ≤ p ≤ 10^9, 1 ≤ M ≤ 10^9.</p>"
    )
    code = """import sys

def mul(a, b, mod):
    n = len(a)
    res = [[0] * n for _ in range(n)]
    for i in range(n):
        for k in range(n):
            av = a[i][k]
            if av == 0:
                continue
            for j in range(n):
                res[i][j] = (res[i][j] + av * b[k][j]) % mod
    return res

def mpow(a, p, mod):
    n = len(a)
    res = [[int(i == j) for j in range(n)] for i in range(n)]
    while p > 0:
        if p & 1:
            res = mul(res, a, mod)
        a = mul(a, a, mod)
        p >>= 1
    return res

def solve(data: str) -> str:
    nums = list(map(int, data.strip().split()))
    if not nums:
        return ""
    n, p, mod = nums[:3]
    pos = 3
    a = []
    for _ in range(n):
        row = [x % mod for x in nums[pos:pos+n]]
        pos += n
        a.append(row)
    res = mpow(a, p, mod)
    return "\\n".join(" ".join(map(str, row)) for row in res)

if __name__ == '__main__':
    print(solve(sys.stdin.read()))
"""
    public_tests = [
        {"input": "2 2 1000\n1 1\n1 0\n", "expectedOutput": "2 1\n1 1"},
        {"input": "1 5 100\n3\n", "expectedOutput": "43"},
    ]
    hidden_tests = [
        {"input": "2 0 1000\n5 7\n1 2\n", "expectedOutput": "1 0\n0 1"},
        {"input": "2 3 1000\n1 1\n1 0\n", "expectedOutput": "3 2\n2 1"},
        {"input": "2 1 10\n2 3\n4 5\n", "expectedOutput": "2 3\n4 5"},
        {"input": "2 2 5\n2 0\n0 2\n", "expectedOutput": "4 0\n0 4"},
        {"input": "1 10 7\n2\n", "expectedOutput": "2"},
    ]
    return {
        "assignmentType": "code-test",
        "title": title,
        "description": description,
        "allowedLanguages": allowed,
        "publicTests": public_tests,
        "hiddenTests": hidden_tests,
        "referenceSolutionPython": code,
        "requiredCalls": ["solve"],
        "forbiddenCalls": ["Process.Start", "__import__"],
        "meta": {"generationSource": "schema-repair", "domain": "matrix", "strategy": "deterministic-matrix-power"},
    }


def _synthesize_generation_result(payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(result, dict):
        result = {}
    draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
    if draft is None and all(result.get(key) is not None for key in ("assignmentType", "title", "description")):
        draft = {k: v for k, v in result.items() if k not in {"summary", "decisionSummary", "draftValidation", "status", "score", "checks", "findings"}}
        result = {"draft": draft, **{k: v for k, v in result.items() if k in {"summary", "decisionSummary", "draftValidation"}}}
    seed = _generation_seed_text(payload, result)
    existing_meta = draft.get("meta") if isinstance(draft, dict) and isinstance(draft.get("meta"), dict) else {}
    existing_title = normalize_text(draft.get("title")) if isinstance(draft, dict) else ""
    should_replace_fallback = bool(draft) and (existing_title.lower().startswith("ai fallback") or normalize_text(existing_meta.get("generationSource") or existing_meta.get("source")).lower() == "fallback") and ("matrix" in seed or "матриц" in seed or "gauss" in seed or "rank" in seed or "ранг" in seed)
    if not isinstance(draft, dict) or should_replace_fallback:
        if "gauss" in seed or "rank" in seed or "ступенчат" in seed or "ранг" in seed:
            draft = _build_matrix_rank_draft(payload)
        elif "power" in seed or "степен" in seed or "mod" in seed:
            draft = _build_matrix_power_draft(payload)
        elif "matrix" in seed or "матриц" in seed:
            draft = _build_matrix_multiplication_draft(payload)
        else:
            return result
        result = dict(result)
        result["draft"] = draft
        result["summary"] = normalize_text(result.get("summary")) or truncate_text(normalize_text(draft.get("description")), 180)
        result["decisionSummary"] = result.get("decisionSummary") if isinstance(result.get("decisionSummary"), dict) else {"confidence": "medium", "source": "schema-repair-draft"}
    if isinstance(result.get("draft"), dict):
        result["draft"]["allowedLanguages"] = _extract_allowed_languages(payload) if not isinstance(result["draft"].get("allowedLanguages"), list) else unique_string_list(result["draft"].get("allowedLanguages"), 6)
        meta = result["draft"].get("meta") if isinstance(result["draft"].get("meta"), dict) else {}
        meta.setdefault("generationSource", "llm")
        result["draft"]["meta"] = meta
    return result

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

    if job_type.startswith("assignment_generate"):
        sanitized = _synthesize_generation_result(payload, sanitized)

    if job_type == "assignment_course_profile_build":
        sanitized["canonicalRequest"] = _ensure_canonical_request(payload, sanitized)
        if not isinstance(sanitized.get("courseDigest"), dict):
            sanitized["courseDigest"] = build_course_digest(payload)
        if not isinstance(sanitized.get("courseProfile"), dict):
            digest = sanitized.get("courseDigest") if isinstance(sanitized.get("courseDigest"), dict) else {}
            sanitized["courseProfile"] = {
                "dominantSkills": unique_string_list(digest.get("dominantSkills"), 8),
                "difficultyDistribution": digest.get("difficultyDistribution") or {},
                "styleProfile": digest.get("styleProfile") or {},
                "policyProfile": digest.get("policyProfile") or {},
                "negativePatterns": unique_string_list(digest.get("negativePatterns"), 8),
            }
        sanitized["summary"] = normalize_text(sanitized.get("summary")) or f"Course profile prepared for {sanitized['canonicalRequest'].get('domain')} domain."
        sanitized["decisionSummary"] = sanitized.get("decisionSummary") if isinstance(sanitized.get("decisionSummary"), dict) else {"confidence": "medium", "source": "schema-repair-course"}

    if job_type == "assignment_gap_analysis" and not isinstance(sanitized.get("gapAnalysis"), dict):
        sanitized.update(_synthesize_gap_analysis(payload, sanitized))

    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        sanitized.update(_synthesize_batch_plan(payload, sanitized))

    return sanitized
