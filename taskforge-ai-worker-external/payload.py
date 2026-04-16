"""Payload parsing, compacting, sanitisation and beginner-track detection."""

import json
import re
from typing import Any, Dict, List, Tuple

from config import (
    MAX_REFERENCE_ASSIGNMENTS,
    MAX_REFERENCE_DESCRIPTION_LEN,
    GAP_ANALYSIS_REFERENCE_ASSIGNMENTS,
    GAP_ANALYSIS_REFERENCE_DESCRIPTION_LEN,
    BATCH_PLAN_REFERENCE_ASSIGNMENTS,
    BATCH_PLAN_REFERENCE_ASSIGNMENTS_RETRY,
    BATCH_PLAN_REFERENCE_DESCRIPTION_LEN,
    MIN_PUBLIC_TESTS,
    MIN_HIDDEN_TESTS,
    MAX_HIDDEN_TESTS,
    MIN_TOTAL_TESTS,
    SELECTION_TELEMETRY,
    DUPLICATE_CLUSTERING,
    DUPLICATE_CLUSTER_LIMIT,
    DUPLICATE_CLUSTER_THRESHOLD,
    DUPLICATE_SIGNATURES,
    SIMILARITY_SIGNATURE_HINTS_LIMIT,
)
from log import log, log_debug, logger
from similarity_signatures import similarity_signature_report
from duplicate_clusters import cluster_duplicate_candidates
from text_utils import (
    normalize_text,
    truncate_text,
    summarize_description,
    unique_string_list,
    strip_conflicting_lists,
    has_html_markup,
    safe_int,
    strip_html_to_text,
    extract_first_meaningful_sentence,
)
from runners import run_python_solution


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
            "sort": item.get("sort"),
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



def _desired_anchor_id(payload: Dict[str, Any]) -> str:
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    batch_memory = payload.get("batchMemory") if isinstance(payload.get("batchMemory"), dict) else {}
    placement_plan = batch_memory.get("placementPlan") if isinstance(batch_memory.get("placementPlan"), list) else []
    candidates = [
        task.get("placementAfterAssignmentId") or task.get("PlacementAfterAssignmentId") or task.get("afterAssignmentId"),
        brief.get("placementAfterAssignmentId") or brief.get("afterAssignmentId"),
        payload.get("placementAfterAssignmentId") or payload.get("afterAssignmentId"),
    ]
    for item in placement_plan:
        if isinstance(item, dict) and normalize_text(item.get("afterAssignmentId")):
            candidates.append(item.get("afterAssignmentId"))
            break
    for value in candidates:
        norm = normalize_text(value)
        if norm:
            return norm
    return ""


def _reference_seed_tokens(payload: Dict[str, Any]) -> List[str]:
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    values = [
        payload.get("prompt"),
        payload.get("titleHint"),
        payload.get("targetSkill"),
        payload.get("microGoal"),
        payload.get("sourceText"),
        task.get("titleHint"),
        task.get("targetSkill") or task.get("TargetSkill"),
        task.get("microGoal") or task.get("MicroGoal"),
        brief.get("titleHint"),
        brief.get("targetSkill"),
        brief.get("summary"),
    ]
    return _placement_keywords(*values)


def _select_reference_assignments_for_stage(payload: Dict[str, Any], limit: int, description_len: int, include_cases: bool, return_telemetry: bool = False) -> Any:
    base = compact_reference_assignments(payload, limit=MAX_REFERENCE_ASSIGNMENTS, description_len=description_len, include_cases=include_cases)
    if not base:
        empty = {"anchorId": normalize_text(_desired_anchor_id(payload)), "selectedIds": [], "seedTokens": [], "topCandidates": []}
        return ([], empty) if return_telemetry else []
    anchor_id = _desired_anchor_id(payload)
    refs_sorted = sorted(base, key=lambda ref: (safe_int(ref.get("sort"), safe_int(ref.get("index"), 10**9)), safe_int(ref.get("index"), 10**9)))
    selected: List[Dict[str, Any]] = []
    seen: set[str] = set()
    top_candidates: list[dict[str, Any]] = []

    def _add(ref: Dict[str, Any]) -> None:
        ref_id = normalize_text(ref.get("id")) or normalize_text(ref.get("title"))
        if not ref_id or ref_id in seen:
            return
        seen.add(ref_id)
        selected.append(ref)

    if anchor_id:
        anchor_idx = next((idx for idx, ref in enumerate(refs_sorted) if normalize_text(ref.get("id")) == anchor_id), None)
        if anchor_idx is not None:
            start = max(0, anchor_idx - 3)
            end = min(len(refs_sorted), anchor_idx + 4)
            for ref in refs_sorted[start:end]:
                _add(ref)

    seed_tokens = _reference_seed_tokens(payload)
    if seed_tokens:
        ranked: List[tuple[int, int, Dict[str, Any], int, int]] = []
        target_diff = max(1, safe_int((payload.get("task") or {}).get("difficultyTarget") if isinstance(payload.get("task"), dict) else payload.get("difficulty"), safe_int(payload.get("difficulty"), 2)))
        for order, ref in enumerate(base):
            title = normalize_text(ref.get("title")).lower()
            desc = normalize_text(ref.get("descriptionSummary")).lower()
            tags = normalize_text(ref.get("tags")).lower()
            shared = sum(1 for token in seed_tokens if token and (token in title or token in desc or token in tags))
            diff_penalty = abs(safe_int(ref.get("difficulty"), target_diff) - target_diff)
            score = shared * 5 - diff_penalty
            if normalize_text(ref.get("id")) == anchor_id:
                score += 6
            ranked.append((score, -order, ref, shared, diff_penalty))
        ranked.sort(reverse=True)
        for score, _order, ref, shared, diff_penalty in ranked[:8]:
            top_candidates.append({"id": ref.get("id"), "title": truncate_text(ref.get("title"), 120), "score": score, "sharedTokens": shared, "difficultyPenalty": diff_penalty, "isAnchor": normalize_text(ref.get("id")) == anchor_id})
        for score, _order, ref, _shared, _diff_penalty in ranked:
            if len(selected) >= limit:
                break
            if score <= 0 and len(selected) >= max(4, limit // 2):
                break
            _add(ref)

    for ref in base:
        if len(selected) >= limit:
            break
        _add(ref)
    result = selected[:limit]
    if not return_telemetry:
        return result
    telemetry = {"anchorId": anchor_id, "seedTokens": seed_tokens[:8], "selectedIds": [normalize_text(ref.get("id")) for ref in result if normalize_text(ref.get("id"))], "selectedTitles": [truncate_text(ref.get("title"), 90) for ref in result[:6]], "topCandidates": top_candidates, "limit": limit}
    return result, telemetry


def _build_anchor_context(payload: Dict[str, Any], refs: List[Dict[str, Any]]) -> Dict[str, Any]:
    if not refs:
        return {}
    anchor_id = _desired_anchor_id(payload)
    refs_sorted = sorted(refs, key=lambda ref: (safe_int(ref.get("sort"), safe_int(ref.get("index"), 10**9)), safe_int(ref.get("index"), 10**9)))
    anchor = None
    if anchor_id:
        for ref in refs_sorted:
            if normalize_text(ref.get("id")) == anchor_id:
                anchor = ref
                break
    if anchor is None:
        inferred = _infer_placement_fields(payload, payload.get("task") if isinstance(payload.get("task"), dict) else {})
        inferred_id = normalize_text(inferred.get("placementAfterAssignmentId"))
        if inferred_id:
            for ref in refs_sorted:
                if normalize_text(ref.get("id")) == inferred_id:
                    anchor = ref
                    anchor_id = inferred_id
                    break
    seed_tokens = _reference_seed_tokens(payload)
    possible_duplicates: List[Dict[str, Any]] = []
    query_text = " ".join([
        normalize_text(payload.get("prompt")),
        normalize_text(payload.get("sourceText")),
        normalize_text(payload.get("titleHint")),
        normalize_text(((payload.get("task") or {}).get("targetSkill") if isinstance(payload.get("task"), dict) else "")),
        normalize_text(((payload.get("task") or {}).get("microGoal") if isinstance(payload.get("task"), dict) else "")),
    ]).strip()
    signature_hints: List[Dict[str, Any]] = []
    if seed_tokens:
        scored: List[tuple[int, Dict[str, Any]]] = []
        for ref in refs_sorted:
            title = normalize_text(ref.get("title")).lower()
            desc = normalize_text(ref.get("descriptionSummary")).lower()
            shared = sum(1 for token in seed_tokens if token and (token in title or token in desc))
            if shared <= 0:
                continue
            scored.append((shared, ref))
        scored.sort(key=lambda x: x[0], reverse=True)
        for score, ref in scored[:5]:
            possible_duplicates.append({
                "id": ref.get("id"),
                "title": truncate_text(ref.get("title"), 120),
                "descriptionSummary": truncate_text(ref.get("descriptionSummary"), 160),
                "difficulty": ref.get("difficulty"),
                "score": score,
            })
    if DUPLICATE_SIGNATURES and query_text:
        signature_scored: List[tuple[float, Dict[str, Any], Dict[str, Any]]] = []
        for ref in refs_sorted:
            ref_text = " ".join([
                normalize_text(ref.get("title")),
                normalize_text(ref.get("descriptionSummary") or ref.get("description")),
            ]).strip()
            if not ref_text:
                continue
            metrics = similarity_signature_report(query_text, ref_text)
            combined = float(metrics.get("combined") or 0.0)
            if combined <= 0.35:
                continue
            signature_scored.append((combined, ref, metrics))
        signature_scored.sort(key=lambda item: item[0], reverse=True)
        for combined, ref, metrics in signature_scored[: max(1, SIMILARITY_SIGNATURE_HINTS_LIMIT)]:
            signature_hints.append({
                "id": ref.get("id"),
                "title": truncate_text(ref.get("title"), 120),
                "combined": round(combined, 4),
                "shingleJaccard": round(float(metrics.get("shingleJaccard") or 0.0), 4),
                "simhashSimilarity": round(float(metrics.get("simhashSimilarity") or 0.0), 4),
                "hammingDistance": int(metrics.get("hammingDistance") or 64),
            })
    nearby: List[Dict[str, Any]] = []
    if anchor is not None:
        anchor_idx = next((idx for idx, ref in enumerate(refs_sorted) if normalize_text(ref.get("id")) == normalize_text(anchor.get("id"))), 0)
        for ref in refs_sorted[max(0, anchor_idx - 2): min(len(refs_sorted), anchor_idx + 3)]:
            nearby.append({
                "id": ref.get("id"),
                "title": truncate_text(ref.get("title"), 120),
                "difficulty": ref.get("difficulty"),
                "sort": ref.get("sort"),
                "descriptionSummary": truncate_text(ref.get("descriptionSummary"), 160),
            })
    duplicate_clusters_preview = []
    if DUPLICATE_CLUSTERING:
        cluster_items = []
        for ref in refs_sorted[: max(2, DUPLICATE_CLUSTER_LIMIT)]:
            cluster_items.append({
                "id": ref.get("id"),
                "title": truncate_text(ref.get("title"), 120),
                "descriptionSummary": truncate_text(ref.get("descriptionSummary") or ref.get("description"), 160),
                "source": "referenceAssignment",
            })
        duplicate_clusters_preview = cluster_duplicate_candidates(cluster_items, threshold=DUPLICATE_CLUSTER_THRESHOLD, limit=max(2, DUPLICATE_CLUSTER_LIMIT))
    return {
        "anchorAssignmentId": normalize_text(anchor.get("id")) if isinstance(anchor, dict) else None,
        "anchorTitle": truncate_text(anchor.get("title"), 120) if isinstance(anchor, dict) else None,
        "nearbyAssignments": nearby,
        "possibleDuplicates": possible_duplicates,
        "duplicateSignatureHints": signature_hints,
        "duplicateClustersPreview": duplicate_clusters_preview[:3],
    }


def _infer_requested_domain(payload: Dict[str, Any]) -> str:
    batch_memory = payload.get("batchMemory") if isinstance(payload.get("batchMemory"), dict) else {}
    agent_state = batch_memory.get("agentState") if isinstance(batch_memory.get("agentState"), dict) else {}
    constraints = batch_memory.get("constraints") if isinstance(batch_memory.get("constraints"), dict) else {}
    placement_plan = batch_memory.get("placementPlan") if isinstance(batch_memory.get("placementPlan"), list) else []
    placement_candidates = agent_state.get("placementCandidates") if isinstance(agent_state.get("placementCandidates"), list) else []

    placement_tokens: List[str] = []
    for item in [*placement_plan[:8], *placement_candidates[:8]]:
        if not isinstance(item, dict):
            continue
        placement_tokens.extend([
            normalize_text(item.get("concept")),
            normalize_text(item.get("titleHint")),
            normalize_text(item.get("reason")),
            normalize_text(item.get("afterAssignmentTitle")),
        ])

    prompt = " ".join([
        normalize_text(payload.get("prompt")),
        normalize_text(payload.get("sourceText")),
        normalize_text(payload.get("titleHint")),
        normalize_text(((payload.get("task") or {}).get("targetSkill") if isinstance(payload.get("task"), dict) else "")),
        normalize_text(((payload.get("task") or {}).get("microGoal") if isinstance(payload.get("task"), dict) else "")),
        normalize_text((((payload.get("batchMemory") or {}).get("userIntentSummary")) if isinstance(payload.get("batchMemory"), dict) else "")),
        normalize_text((agent_state.get("userIntentSummary") if isinstance(agent_state, dict) else "")),
        normalize_text((agent_state.get("pedagogyMode") if isinstance(agent_state, dict) else "")),
        " ".join(unique_string_list(constraints.get("mustStayBeforeConcepts"), 6)),
        " ".join(unique_string_list(constraints.get("avoidConcepts"), 6)),
        " ".join([token for token in placement_tokens if token]),
    ]).lower()
    if any(tok in prompt for tok in ["матриц", "matrix", "2d array"]):
        return "matrix"
    if any(tok in prompt for tok in ["мостик", "bridge", "подводящ", "guided", "walkthrough", "пошаг"]):
        return "bridge-pack"
    if any(tok in prompt for tok in ["ввод", "вывод", "cin", "cout", "scanf", "printf", "getline", "строк", "тип данн", "if", "условн"]):
        return "cpp-basic-io"
    if any(tok in prompt for tok in ["нович", "с нуля", "прост", "базов", "первокласс"]):
        return "cpp-basic-io"
    return "general"


def _domain_conflicts(domain: str, payload: Dict[str, Any]) -> bool:
    low = normalize_text(domain).lower()
    inferred = _infer_requested_domain(payload)
    if not low:
        return False
    if inferred == "general":
        return False
    if low == inferred:
        return False
    if low == "matrix" and inferred != "matrix":
        return True
    if inferred == "cpp-basic-io" and any(tok in low for tok in ["matrix", "oop", "graph", "tree", "dp"]):
        return True
    return False


def _compact_batch_memory(payload: Dict[str, Any]) -> Dict[str, Any]:
    memory = payload.get("batchMemory") if isinstance(payload.get("batchMemory"), dict) else {}
    if not memory:
        return {}
    learner = memory.get("learnerProfile") if isinstance(memory.get("learnerProfile"), dict) else {}
    pedagogy = memory.get("pedagogy") if isinstance(memory.get("pedagogy"), dict) else {}
    title_style = memory.get("titleStyle") if isinstance(memory.get("titleStyle"), dict) else {}
    constraints = memory.get("constraints") if isinstance(memory.get("constraints"), dict) else {}
    agent_state = memory.get("agentState") if isinstance(memory.get("agentState"), dict) else {}
    placement_plan: List[Dict[str, Any]] = []
    for item in (memory.get("placementPlan") if isinstance(memory.get("placementPlan"), list) else [])[:10]:
        if not isinstance(item, dict):
            continue
        placement_plan.append({
            "source": normalize_text(item.get("source")),
            "concept": truncate_text(item.get("concept"), 120),
            "afterAssignmentId": normalize_text(item.get("afterAssignmentId")),
            "afterAssignmentTitle": truncate_text(item.get("afterAssignmentTitle"), 120),
            "beforeAssignmentTitle": truncate_text(item.get("beforeAssignmentTitle"), 120),
            "reason": truncate_text(item.get("reason"), 180),
            "taskCount": safe_int(item.get("taskCount"), 1),
            "difficulty": safe_int(item.get("difficulty"), 1),
            "titleHint": truncate_text(item.get("titleHint"), 120),
            "taskFormat": normalize_text(item.get("taskFormat") or item.get("learningMode")),
            "titleExamples": unique_string_list(item.get("titleExamples"), 6),
        })
    return {
        "source": normalize_text(memory.get("source")),
        "batchKind": normalize_text(memory.get("batchKind")),
        "userIntentSummary": truncate_text(memory.get("userIntentSummary"), 220),
        "latestIntentKind": normalize_text(memory.get("latestIntentKind")),
        "latestExplicitInstruction": truncate_text(memory.get("latestExplicitInstruction"), 260),
        "latestTeachingScript": truncate_text(memory.get("latestTeachingScript"), 1200),
        "suppressBridgePlanLoop": bool(memory.get("suppressBridgePlanLoop")),
        "requireCourseAwarePlanning": bool(memory.get("requireCourseAwarePlanning")),
        "learnerProfile": {
            "audience": normalize_text(learner.get("audience")),
            "explainLikeChild": bool(learner.get("explainLikeChild")),
            "preferGuidedWalkthroughs": bool(learner.get("preferGuidedWalkthroughs")),
            "requireSectionIntroGuides": bool(learner.get("requireSectionIntroGuides")),
            "tone": normalize_text(learner.get("tone")),
            "vocabularyLevel": normalize_text(learner.get("vocabularyLevel")),
            "maxNewConceptsPerTask": safe_int(learner.get("maxNewConceptsPerTask"), 2),
        },
        "pedagogy": {
            "preferGuidedWalkthroughs": bool(pedagogy.get("preferGuidedWalkthroughs")),
            "requireSectionIntroGuides": bool(pedagogy.get("requireSectionIntroGuides")),
            "explainLikeChild": bool(pedagogy.get("explainLikeChild")),
            "tone": normalize_text(pedagogy.get("tone")),
            "vocabularyLevel": normalize_text(pedagogy.get("vocabularyLevel")),
            "maxNewConceptsPerTask": safe_int(pedagogy.get("maxNewConceptsPerTask"), 2),
            "preferTinySteps": bool(pedagogy.get("preferTinySteps")),
        },
        "titleStyle": {
            "pattern": truncate_text(title_style.get("pattern"), 160),
            "examples": unique_string_list(title_style.get("examples"), 8),
            "styleHints": unique_string_list(title_style.get("styleHints"), 8),
            "avoidGenericTitles": bool(title_style.get("avoidGenericTitles")),
        },
        "constraints": {
            "mustStayBeforeConcepts": unique_string_list(constraints.get("mustStayBeforeConcepts"), 6),
            "avoidConcepts": unique_string_list(constraints.get("avoidConcepts"), 6),
            "styleGoal": truncate_text(constraints.get("styleGoal"), 120),
            "titleGoal": truncate_text(constraints.get("titleGoal"), 120),
        },
        "agentState": {
            "workflowKind": normalize_text(agent_state.get("workflowKind")),
            "currentStage": normalize_text(agent_state.get("currentStage")),
            "objectiveKind": normalize_text(agent_state.get("objectiveKind")),
            "objectiveSummary": truncate_text(agent_state.get("objectiveSummary"), 240),
            "stageSummary": truncate_text(agent_state.get("stageSummary"), 220),
            "userIntentSummary": truncate_text(agent_state.get("userIntentSummary"), 220),
            "learnerAudience": normalize_text(agent_state.get("learnerAudience")),
            "pedagogyMode": normalize_text(agent_state.get("pedagogyMode")),
            "nextSuggestedAction": truncate_text(agent_state.get("nextSuggestedAction"), 140),
            "confidencePercent": safe_int(agent_state.get("confidencePercent"), 35),
            "confidenceReason": truncate_text(agent_state.get("confidenceReason"), 180),
            "selfCritique": truncate_text(agent_state.get("selfCritique"), 220),
            "blockerSummary": truncate_text(agent_state.get("blockerSummary"), 180),
            "needsClarification": bool(agent_state.get("needsClarification")),
            "autonomyMode": normalize_text(agent_state.get("autonomyMode")),
            "readyForGeneration": bool(agent_state.get("readyForGeneration")),
            "activeGoals": unique_string_list(agent_state.get("activeGoals"), 6),
            "activeConstraints": unique_string_list(agent_state.get("activeConstraints"), 6),
            "styleHints": unique_string_list(agent_state.get("styleHints"), 8),
            "evidenceLedger": unique_string_list(agent_state.get("evidenceLedger"), 6),
            "openQuestions": unique_string_list(agent_state.get("openQuestions"), 6),
            "riskFlags": unique_string_list(agent_state.get("riskFlags"), 5),
            "completionCriteria": unique_string_list(agent_state.get("completionCriteria"), 6),
            "subtasks": [
                {
                    "key": normalize_text(item.get("key")),
                    "title": truncate_text(item.get("title"), 120),
                    "status": normalize_text(item.get("status")),
                    "summary": truncate_text(item.get("summary"), 180),
                }
                for item in (agent_state.get("subtasks") if isinstance(agent_state.get("subtasks"), list) else [])[:6]
                if isinstance(item, dict)
            ],
            "planSteps": [
                {
                    "key": normalize_text(item.get("key")),
                    "title": truncate_text(item.get("title"), 120),
                    "status": normalize_text(item.get("status")),
                    "summary": truncate_text(item.get("summary"), 180),
                    "recommendedAction": normalize_text(item.get("recommendedAction")),
                    "successSignal": truncate_text(item.get("successSignal"), 140),
                    "blockedBy": truncate_text(item.get("blockedBy"), 140),
                }
                for item in (agent_state.get("planSteps") if isinstance(agent_state.get("planSteps"), list) else [])[:8]
                if isinstance(item, dict)
            ],
            "decisionCandidates": [
                {
                    "name": normalize_text(item.get("name")),
                    "why": truncate_text(item.get("why"), 180),
                    "status": normalize_text(item.get("status")),
                }
                for item in (agent_state.get("decisionCandidates") if isinstance(agent_state.get("decisionCandidates"), list) else [])[:5]
                if isinstance(item, dict)
            ],
            "selectedPlacementAfterAssignmentId": normalize_text(agent_state.get("selectedPlacementAfterAssignmentId") or agent_state.get("placementAfterAssignmentId")),
            "selectedPlacementAfterAssignmentTitle": truncate_text(agent_state.get("selectedPlacementAfterAssignmentTitle") or agent_state.get("placementAfterAssignmentTitle"), 120),
            "placementCandidates": [
                {
                    "source": normalize_text(item.get("source")),
                    "concept": truncate_text(item.get("concept"), 120),
                    "afterAssignmentId": normalize_text(item.get("afterAssignmentId")),
                    "afterAssignmentTitle": truncate_text(item.get("afterAssignmentTitle"), 120),
                    "reason": truncate_text(item.get("reason"), 160),
                    "taskCount": safe_int(item.get("taskCount"), 1),
                    "difficulty": safe_int(item.get("difficulty"), 1),
                    "taskFormat": normalize_text(item.get("taskFormat")),
                    "titleHint": truncate_text(item.get("titleHint"), 120),
                }
                for item in (agent_state.get("placementCandidates") if isinstance(agent_state.get("placementCandidates"), list) else [])[:6]
                if isinstance(item, dict)
            ],
        },
        "placementPlan": placement_plan,
        "courseAuditSummary": truncate_text(((memory.get("courseAudit") or {}) if isinstance(memory.get("courseAudit"), dict) else {}).get("Summary") or ((memory.get("courseAudit") or {}) if isinstance(memory.get("courseAudit"), dict) else {}).get("summary"), 220),
    }




PLACEMENT_STOPWORDS = {
    "базовый", "простая", "простое", "простые", "форматированный", "форматированным", "формат", "вывод", "ввод",
    "c", "cpp", "c++", "задача", "число", "числа", "строка", "данные", "данных", "работа", "консолью",
}


def _placement_keywords(*values: Any) -> List[str]:
    tokens: List[str] = []
    seen = set()
    for value in values:
        for token in re.split(r"[^\wа-яА-Я]+", normalize_text(value).lower()):
            if len(token) < 3 or token in PLACEMENT_STOPWORDS:
                continue
            if token in seen:
                continue
            seen.add(token)
            tokens.append(token)
    return tokens[:10]


def _ordered_reference_assignments_for_placement(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    refs = compact_reference_assignments(payload, limit=MAX_REFERENCE_ASSIGNMENTS, description_len=140, include_cases=False)
    usable = [ref for ref in refs if isinstance(ref, dict) and normalize_text(ref.get("id"))]
    usable.sort(key=lambda ref: (safe_int(ref.get("sort"), safe_int(ref.get("index"), 10**9)), safe_int(ref.get("index"), 10**9)))
    return usable


def _read_placement_fields(source: Any) -> Dict[str, Any]:
    if not isinstance(source, dict):
        return {}
    placement = source.get("placement") if isinstance(source.get("placement"), dict) else {}
    after_id = normalize_text(
        source.get("placementAfterAssignmentId")
        or source.get("afterAssignmentId")
        or placement.get("afterAssignmentId")
        or placement.get("placementAfterAssignmentId")
    )
    after_title = normalize_text(
        source.get("placementAfterTitle")
        or source.get("afterAssignmentTitle")
        or placement.get("afterAssignmentTitle")
        or placement.get("placementAfterTitle")
        or placement.get("anchorTitle")
        or placement.get("title")
    )
    reason = normalize_text(
        source.get("placementReason")
        or placement.get("reason")
        or placement.get("placementReason")
    )
    return {
        "placementAfterAssignmentId": after_id or None,
        "placementAfterTitle": after_title or None,
        "placementReason": reason or None,
    }


def _infer_placement_fields(payload: Dict[str, Any], task_like: Dict[str, Any]) -> Dict[str, Any]:
    existing = _read_placement_fields(task_like)
    if existing.get("placementAfterAssignmentId"):
        return existing
    refs = _ordered_reference_assignments_for_placement(payload)
    if not refs:
        return {"placementAfterAssignmentId": None, "placementAfterTitle": None, "placementReason": None}

    target_diff = safe_int(task_like.get("difficultyTarget"), safe_int(task_like.get("difficulty"), safe_int(payload.get("difficulty"), 2)))
    keywords = _placement_keywords(task_like.get("targetSkill"), task_like.get("microGoal"), task_like.get("titleHint"), payload.get("prompt"))
    prompt_low = normalize_text(payload.get("prompt")).lower()

    def score(ref: Dict[str, Any]) -> tuple:
        title = normalize_text(ref.get("title")).lower()
        desc = normalize_text(ref.get("descriptionSummary")).lower()
        shared = sum(1 for kw in keywords if kw and (kw in title or kw in desc))
        ref_diff = safe_int(ref.get("difficulty"), target_diff)
        before_bonus = 2 if ref_diff <= target_diff else 0
        type_bonus = 1 if normalize_text(ref.get("type")).lower() == normalize_text(payload.get("assignmentType")).lower() else 0
        io_bonus = 1 if any(tok in prompt_low for tok in ["ввод", "вывод", "getline", "scanf", "printf", "cin", "cout"]) and any(tok in title for tok in ["ввод", "вывод", "строк", "формат", "числ"]) else 0
        sort_value = safe_int(ref.get("sort"), safe_int(ref.get("index"), 0))
        diff_penalty = abs(ref_diff - target_diff)
        return (shared * 5 + before_bonus + type_bonus + io_bonus - diff_penalty, sort_value)

    best = max(refs, key=score)
    best_score, _ = score(best)
    if best_score <= 0:
        eligible = [ref for ref in refs if safe_int(ref.get("difficulty"), target_diff) <= target_diff]
        best = eligible[-1] if eligible else refs[-1]
    reason = existing.get("placementReason") or (
        f"Поставить после «{normalize_text(best.get('title'))}», чтобы новая задача логично продолжала текущую лестницу курса."
        if normalize_text(best.get("title")) else "Поставить после ближайшего подходящего задания курса."
    )
    return {
        "placementAfterAssignmentId": normalize_text(best.get("id")) or None,
        "placementAfterTitle": normalize_text(best.get("title")) or None,
        "placementReason": reason,
    }

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
            "titleHint": truncate_text(item.get("titleHint"), 140),
            "targetSkill": normalize_text(item.get("targetSkill")),
            "microGoal": truncate_text(item.get("microGoal"), 220),
            "difficultyTarget": item.get("difficultyTarget"),
            "whyItExists": truncate_text(item.get("whyItExists"), 220),
            "taskFormat": normalize_text(item.get("taskFormat") or item.get("learningMode")),
            "placementAfterAssignmentId": normalize_text(item.get("placementAfterAssignmentId")),
            "placementAfterTitle": truncate_text(item.get("placementAfterTitle"), 120),
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
    batch_memory = _compact_batch_memory(payload)
    return {
        "assignmentType": normalize_text(payload.get("assignmentType") or "code-test") or "code-test",
        "mode": normalize_text(payload.get("mode") or "topic-pack") or "topic-pack",
        "count": max(1, safe_int(payload.get("count"), 1)),
        "difficulty": max(1, min(3, safe_int(payload.get("difficulty"), 2))),
        "domainHints": [
            hint for hint in [
                "matrix" if ("матриц" in prompt_low or "matrix" in prompt_low) else "",
                "oop" if ("ооп" in prompt_low or "oop" in prompt_low or "класс" in prompt_low or "class" in prompt_low) else "",
                "performance" if ("оптим" in prompt_low or "эффектив" in prompt_low or "fast" in prompt_low or "sparse" in prompt_low) else "",
            ] if hint
        ],
        "mustInclude": unique_tokens[:8],
        "referenceTitleHints": unique_string_list([*ref_titles[:5], *(((batch_memory.get("titleStyle") or {}) if isinstance(batch_memory.get("titleStyle"), dict) else {}).get("examples") or [])], 8),
        "sourcePrompt": truncate_text(prompt, 220),
        "courseAwarePlanning": bool(batch_memory.get("requireCourseAwarePlanning")),
        "mustStayBeforeConcepts": (((batch_memory.get("constraints") or {}) if isinstance(batch_memory.get("constraints"), dict) else {}).get("mustStayBeforeConcepts") or []),
        "preferGuidedWalkthroughs": bool((((batch_memory.get("pedagogy") or {}) if isinstance(batch_memory.get("pedagogy"), dict) else {}).get("preferGuidedWalkthroughs"))),
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
    batch_memory = _compact_batch_memory(payload)
    title_style = batch_memory.get("titleStyle") if isinstance(batch_memory.get("titleStyle"), dict) else {}
    recent_titles = unique_string_list([*title_hints[:5], *(title_style.get("examples") or [])], 8)
    teaching_style = [
        "tiptap-or-legacy-text",
        "public-and-hidden-tests",
        "structured-i/o",
    ]
    if bool(((batch_memory.get("pedagogy") or {}) if isinstance(batch_memory.get("pedagogy"), dict) else {}).get("preferGuidedWalkthroughs")):
        teaching_style.append("guided-walkthrough-friendly")
    if bool(((batch_memory.get("learnerProfile") or {}) if isinstance(batch_memory.get("learnerProfile"), dict) else {}).get("explainLikeChild")):
        teaching_style.append("very-simple-language")
    return {
        "referenceCount": len(refs),
        "assignmentTypes": types[:4],
        "languages": langs[:6],
        "recentReferenceTitles": recent_titles[:8],
        "teachingStyle": teaching_style,
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


def compact_payload_for_stage(job_type: Any, payload: Any) -> Dict[str, Any]:
    """Build a compact payload snapshot for prompt construction.

    Some prompt builders historically called this helper as
    ``compact_payload_for_stage(payload, stage_name)`` instead of the intended
    ``compact_payload_for_stage(stage_name, payload)``. Be permissive here so a
    single call-site regression does not take down the whole worker loop.
    """
    if isinstance(job_type, dict) and isinstance(payload, str):
        job_type, payload = payload, job_type
    if not isinstance(payload, dict):
        logger.warning(f"compact_payload_for_stage expected dict payload, got {type(payload).__name__}")
        return {}

    compact: Dict[str, Any] = {}
    job_type = normalize_text(job_type).lower()
    compact_mode = normalize_text(payload.get("__compactMode")).lower()
    is_course_stage = job_type == "assignment_course_profile_build"
    is_gap_stage = job_type == "assignment_gap_analysis"
    is_planner_stage = job_type in {"assignment_batch_plan", "assignment_batch_replan"}
    is_brief_stage = job_type in {"assignment_brief_generate", "assignment_brief_repair"}
    is_draft_stage = job_type in {"draft_generate", "draft_body_generate", "draft_title_generate", "draft_title_repair", "assignment_generate_from_text", "assignment_repair"}
    ultra_compact = compact_mode == "ultra"

    keep_scalar = ["assignmentType", "mode", "count", "difficulty", "prompt", "batchId", "courseId", "requestType", "batchItemId", "instructionStrictness", "userInstructionSnapshot", "teachingScript"]
    if is_planner_stage:
        keep_scalar = ["assignmentType", "mode", "count", "difficulty", "prompt", "batchId", "courseId", "requestType", "instructionStrictness", "userInstructionSnapshot", "teachingScript"]
    elif is_gap_stage:
        keep_scalar = ["assignmentType", "mode", "count", "prompt", "batchId", "courseId", "requestType", "instructionStrictness", "userInstructionSnapshot", "teachingScript"]
    elif is_course_stage:
        keep_scalar = ["assignmentType", "mode", "count", "difficulty", "prompt", "batchId", "courseId", "instructionStrictness", "userInstructionSnapshot", "teachingScript"]

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
        ref_limit = 8 if ultra_compact else 12
        ref_desc_len = 120
    elif is_brief_stage:
        ref_limit = 6
        ref_desc_len = 110
        include_cases = True
    elif is_draft_stage:
        ref_limit = 10 if ultra_compact else 16
        ref_desc_len = 130
        include_cases = True

    if SELECTION_TELEMETRY:
        compact["referenceAssignments"], selection_telemetry = _select_reference_assignments_for_stage(payload, limit=ref_limit, description_len=ref_desc_len, include_cases=include_cases, return_telemetry=True)
        selected_refs = compact["referenceAssignments"] if isinstance(compact.get("referenceAssignments"), list) else []
        selected_titles = [normalize_text(item.get("title"))[:120] for item in selected_refs[:6] if isinstance(item, dict) and normalize_text(item.get("title"))]
        if selected_titles:
            selection_telemetry["selectedTitles"] = selected_titles
        compact["selectionTelemetry"] = selection_telemetry
    else:
        compact["referenceAssignments"] = _select_reference_assignments_for_stage(payload, limit=ref_limit, description_len=ref_desc_len, include_cases=include_cases)
    anchor_context = _build_anchor_context(payload, compact["referenceAssignments"])
    if anchor_context:
        compact["anchorContext"] = anchor_context

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
            "titleHint": truncate_text(task.get("titleHint") or task.get("TitleHint"), 140),
            "targetSkill": task.get("targetSkill") or task.get("TargetSkill"),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 180),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
            "whyItExists": truncate_text(task.get("whyItExists") or task.get("WhyItExists"), 180),
            "taskFormat": normalize_text(task.get("taskFormat") or task.get("TaskFormat") or task.get("learningMode") or task.get("LearningMode")),
            "learningMode": normalize_text(task.get("learningMode") or task.get("LearningMode") or task.get("taskFormat") or task.get("TaskFormat")),
            "placementAfterAssignmentId": normalize_text(task.get("placementAfterAssignmentId") or task.get("PlacementAfterAssignmentId")),
            "placementAfterTitle": truncate_text(task.get("placementAfterTitle") or task.get("PlacementAfterTitle"), 140),
            "placementReason": truncate_text(task.get("placementReason") or task.get("PlacementReason"), 180),
            "antiDuplicateHints": unique_string_list(task.get("antiDuplicateHints") or task.get("AntiDuplicateHints"), 4),
        }
    if isinstance(payload.get("brief"), dict):
        compact["brief"] = compact_brief_object(payload["brief"])
    if isinstance(payload.get("briefReview"), dict):
        compact["briefReview"] = compact_brief_review_object(payload["briefReview"])

    optional_memory_keys = ["plannerFeedback", "decisionLogDigest", "batchMemory"] if (is_course_stage or is_gap_stage or is_planner_stage) else ["plannerFeedback", "decisionLogDigest", "antiPatternMemory", "institutionalMemory", "batchMemory"]
    for key in optional_memory_keys:
        value = payload.get(key)
        if key == "batchMemory" and isinstance(value, dict):
            compact[key] = _compact_batch_memory(payload)
        elif isinstance(value, dict):
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
    existing_domain = normalize_text(existing.get("domain"))
    inferred_domain = _infer_requested_domain(payload)
    domain = inferred_domain if inferred_domain != "general" and (not existing_domain or _domain_conflicts(existing_domain, payload)) else (existing_domain or inferred_domain or "general")
    difficulty = safe_int(existing.get("difficulty"), safe_int(payload.get("difficulty"), 3))
    count = max(1, safe_int(existing.get("count"), safe_int(payload.get("count"), 1)))
    must_include = unique_string_list(existing.get("mustInclude"), 8)
    if not must_include or _domain_conflicts(existing_domain, payload):
        seed = _reference_seed_tokens(payload)
        if domain == "matrix":
            must_include = ["matrix", "cin", "cout"]
        elif domain == "cpp-basic-io":
            must_include = unique_string_list(seed + ["cin", "cout", "basic types"], 8)
        else:
            must_include = unique_string_list(seed + [payload.get("assignmentType") or "task"], 6)
    avoid = unique_string_list(existing.get("avoid"), 8)
    if not avoid or _domain_conflicts(existing_domain, payload):
        ref_titles = [normalize_text(ref.get("title")) for ref in compact_reference_assignments(payload, limit=8, description_len=80, include_cases=False) if normalize_text(ref.get("title"))]
        avoid = unique_string_list(["generic titles", "duplicate tasks", *ref_titles[:5]], 10)
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
        placement = _read_placement_fields(item)
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
            "placementAfterAssignmentId": placement.get("placementAfterAssignmentId"),
            "placementAfterTitle": placement.get("placementAfterTitle"),
            "placementReason": placement.get("placementReason"),
            "taskFormat": normalize_text(item.get("taskFormat") or item.get("learningMode") or "exercise"),
            "learningMode": normalize_text(item.get("learningMode") or item.get("taskFormat") or "exercise"),
            "decisionLog": item.get("decisionLog")[:3] if isinstance(item.get("decisionLog"), list) else [{"stage": "batch_plan", "message": "Normalized planner task."}],
        })
    return tasks


def _synthesize_plan_tasks_from_batch_memory(payload: Dict[str, Any], count: int, difficulty: int, canonical: Dict[str, Any]) -> List[Dict[str, Any]]:
    batch_memory = _compact_batch_memory(payload)
    placement_plan = batch_memory.get("placementPlan") if isinstance(batch_memory.get("placementPlan"), list) else []
    if not placement_plan:
        agent_state = batch_memory.get("agentState") if isinstance(batch_memory.get("agentState"), dict) else {}
        placement_plan = agent_state.get("placementCandidates") if isinstance(agent_state.get("placementCandidates"), list) else []
    if not placement_plan:
        return []
    prefer_guides = bool((((batch_memory.get("pedagogy") or {}) if isinstance(batch_memory.get("pedagogy"), dict) else {}).get("preferGuidedWalkthroughs")))
    tasks: List[Dict[str, Any]] = []
    idx = 1
    for point in placement_plan:
        if not isinstance(point, dict):
            continue
        concept = normalize_text(point.get("concept") or point.get("titleHint") or f"slot-{idx}") or f"slot-{idx}"
        task_count = max(1, safe_int(point.get("taskCount"), 1))
        point_diff = max(1, min(3, safe_int(point.get("difficulty"), difficulty)))
        for local in range(task_count):
            if idx > count:
                break
            task_format = normalize_text(point.get("taskFormat") or ("guided-walkthrough" if prefer_guides and local == 0 else "exercise")) or "exercise"
            title_hint = normalize_text(point.get("titleHint")) or concept
            if task_format == "guided-walkthrough":
                micro_goal = f"Пошагово ввести навык «{concept}» очень простым языком и на одном маленьком действии."
                why = f"Сделать понятный путеводитель перед темой «{concept}», чтобы ученик смог пройти шаги и сразу получить правильный результат."
            else:
                micro_goal = f"Отдельно отработать навык «{concept}» без смешивания нескольких новых идей."
                why = normalize_text(point.get("reason")) or f"Добавить недостающую ступень перед темой «{concept}»."
            tasks.append({
                "index": idx,
                "titleHint": title_hint,
                "targetSkill": concept,
                "primarySkill": concept,
                "microGoal": micro_goal,
                "uniqueAngle": f"{concept} / point {idx}",
                "difficultyTarget": point_diff,
                "mustInclude": unique_string_list([concept, *(canonical.get("mustInclude") or [])], 4),
                "antiDuplicateHints": unique_string_list([*(canonical.get("avoid") or []), concept], 4),
                "whyItExists": why,
                "placementAfterAssignmentId": point.get("afterAssignmentId"),
                "placementAfterTitle": point.get("afterAssignmentTitle"),
                "placementReason": normalize_text(point.get("reason")) or why,
                "taskFormat": task_format,
                "learningMode": task_format,
                "decisionLog": [{"stage": "batch_plan", "message": f"Synthesized from chat batchMemory placement data ({normalize_text(point.get('source')) or 'memory'})."}],
            })
            idx += 1
        if idx > count:
            break
    return tasks

def _known_assignment_ids(payload: Dict[str, Any]) -> set[str]:
    ids: set[str] = set()
    for bucket_name in ("referenceAssignments", "recentAssignments"):
        bucket = payload.get(bucket_name) if isinstance(payload.get(bucket_name), list) else []
        for item in bucket:
            if isinstance(item, dict):
                value = normalize_text(item.get("id") or item.get("assignmentId") or item.get("Id"))
                if value:
                    ids.add(value)
    batch_memory = payload.get("batchMemory") if isinstance(payload.get("batchMemory"), dict) else {}
    placement_plan = batch_memory.get("placementPlan") if isinstance(batch_memory.get("placementPlan"), list) else []
    for item in placement_plan:
        if isinstance(item, dict):
            value = normalize_text(item.get("afterAssignmentId"))
            if value:
                ids.add(value)
    return ids


def _validate_batch_plan(payload: Dict[str, Any], canonical: Dict[str, Any], tasks: List[Dict[str, Any]]) -> Dict[str, Any]:
    checks: List[Dict[str, Any]] = []
    expected_count = max(1, safe_int(canonical.get("count"), len(tasks) or 1))
    checks.append({
        "name": "count-matches",
        "status": "passed" if len(tasks) == expected_count else "failed",
        "details": f"expected={expected_count} actual={len(tasks)}",
    })
    normalized_skills = [normalize_text(item.get("targetSkill")).casefold() for item in tasks if normalize_text(item.get("targetSkill"))]
    duplicates = len(normalized_skills) - len(set(normalized_skills))
    checks.append({
        "name": "unique-target-skills",
        "status": "passed" if duplicates == 0 else "warning",
        "details": "targetSkill values unique" if duplicates == 0 else f"duplicates={duplicates}",
    })
    known_ids = _known_assignment_ids(payload)
    unknown_ids = [normalize_text(item.get("placementAfterAssignmentId")) for item in tasks if normalize_text(item.get("placementAfterAssignmentId")) and normalize_text(item.get("placementAfterAssignmentId")) not in known_ids]
    checks.append({
        "name": "placement-ids-known",
        "status": "passed" if not unknown_ids else "warning",
        "details": "all placement ids known" if not unknown_ids else f"unknown ids: {unknown_ids[:4]}",
    })
    generic_count = 0
    for item in tasks:
        text_blob = " ".join([
            normalize_text(item.get("titleHint")),
            normalize_text(item.get("targetSkill")),
            normalize_text(item.get("microGoal")),
        ]).casefold()
        if not text_blob or text_blob in {"task", "задача"} or len(text_blob) < 16:
            generic_count += 1
    checks.append({
        "name": "no-generic-slots",
        "status": "passed" if generic_count == 0 else "warning",
        "details": "slot descriptions look specific" if generic_count == 0 else f"generic slots={generic_count}",
    })
    avoid_terms = [normalize_text(x).casefold() for x in (canonical.get("avoid") if isinstance(canonical.get("avoid"), list) else []) if normalize_text(x)]
    domain_conflicts = 0
    if avoid_terms:
        for item in tasks:
            blob = " ".join([normalize_text(item.get("targetSkill")), normalize_text(item.get("microGoal"))]).casefold()
            if any(term and term in blob for term in avoid_terms):
                domain_conflicts += 1
    checks.append({
        "name": "domain-consistency",
        "status": "passed" if domain_conflicts == 0 else "warning",
        "details": "no obvious conflicts with avoid[]" if domain_conflicts == 0 else f"conflicts={domain_conflicts}",
    })
    has_failed = any(item.get("status") == "failed" for item in checks)
    has_warning = any(item.get("status") == "warning" for item in checks)
    status = "failed" if has_failed else ("needs-review" if has_warning else "passed")
    passed = sum(1 for item in checks if item.get("status") == "passed")
    return {
        "status": status,
        "summary": f"Batch plan validation: {passed}/{len(checks)} passed.",
        "checks": checks,
    }


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
        tasks = _normalize_plan_tasks(_synthesize_plan_tasks_from_batch_memory(payload, max(1, safe_int(canonical.get("count"), 1)), safe_int(canonical.get("difficulty"), 2), canonical))
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
        placement = _infer_placement_fields(payload, task)
        task["placementAfterAssignmentId"] = placement.get("placementAfterAssignmentId")
        task["placementAfterTitle"] = placement.get("placementAfterTitle")
        task["placementReason"] = placement.get("placementReason")
        task["taskFormat"] = normalize_text(task.get("taskFormat") or task.get("learningMode") or ("exercise" if idx > 1 else "guided-walkthrough" if bool(((_compact_batch_memory(payload).get("pedagogy") or {}) if isinstance(_compact_batch_memory(payload).get("pedagogy"), dict) else {}).get("preferGuidedWalkthroughs")) else "exercise")) or "exercise"
        task["learningMode"] = normalize_text(task.get("learningMode") or task.get("taskFormat") or task["taskFormat"]) or task["taskFormat"]
    coverage = result.get("coverage") if isinstance(result.get("coverage"), dict) else {"coverageBand": "medium", "noveltyGoal": f"Produce {count} distinct {canonical.get('domain')} tasks"}
    decision_summary = result.get("decisionSummary") if isinstance(result.get("decisionSummary"), dict) else {"confidence": "medium", "source": "schema-repair-plan"}
    plan_validation = _validate_batch_plan(payload, canonical, tasks)
    return {
        "canonicalRequest": canonical,
        "coverage": coverage,
        "summary": normalize_text(result.get("summary")) or f"Batch plan repaired for {canonical.get('domain')} domain.",
        "decisionSummary": decision_summary,
        "plan": {"tasks": tasks},
        "planValidation": plan_validation,
    }



def _normalize_language_token(value: Any) -> str:
    raw = normalize_text(value).lower()
    if raw in {"c++", "cpp", "g++"}:
        return "cpp"
    if raw in {"c#", "csharp", "cs"}:
        return "csharp"
    if raw in {"py", "python"}:
        return "python"
    if raw in {"js", "javascript", "node"}:
        return "javascript"
    if raw == "java":
        return "java"
    if raw == "pascal":
        return "pascal"
    return raw


def _prompt_language_hints(payload: Dict[str, Any]) -> List[str]:
    haystack = " ".join([
        normalize_text(payload.get("prompt")),
        normalize_text(payload.get("sourceText")),
        normalize_text(payload.get("titleHint")),
    ]).lower()
    hints: List[str] = []
    if any(token in haystack for token in ["c++", "cpp", " c plus plus"]):
        hints.append("cpp")
    if any(token in haystack for token in ["c#", "csharp"]):
        hints.append("csharp")
    if "python" in haystack:
        hints.append("python")
    if "javascript" in haystack or " js " in f" {haystack} ":
        hints.append("javascript")
    if " java" in f" {haystack} ":
        hints.append("java")
    if "pascal" in haystack:
        hints.append("pascal")
    return unique_string_list(hints, 6)


def _reference_allowed_languages(payload: Dict[str, Any]) -> List[str]:
    counts: Dict[str, int] = {}
    refs_with_langs = 0
    for ref in list(payload.get("referenceAssignments") or []):
        if not isinstance(ref, dict):
            continue
        langs_for_ref: List[str] = []
        csv = normalize_text(ref.get("allowedLanguagesCsv") or ref.get("AllowedLanguagesCsv"))
        if csv:
            langs_for_ref.extend(_normalize_language_token(x) for x in csv.split(','))
        if isinstance(ref.get("allowedLanguages"), list):
            langs_for_ref.extend(_normalize_language_token(x) for x in ref.get("allowedLanguages"))
        langs_for_ref = [x for x in unique_string_list(langs_for_ref, 6) if x in {"cpp", "csharp", "python", "javascript", "java", "pascal"}]
        if not langs_for_ref:
            continue
        refs_with_langs += 1
        for lang in langs_for_ref:
            counts[lang] = counts.get(lang, 0) + 1
    if not counts:
        return []
    if len(counts) == 1:
        return list(counts.keys())
    dominant = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))[0]
    if dominant[1] >= max(2, int(refs_with_langs * 0.6 + 0.999)):
        return [dominant[0]]
    return [lang for lang, _ in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))[:3]]


def infer_allowed_languages(payload: Dict[str, Any]) -> List[str]:
    explicit = unique_string_list(payload.get("allowedLanguages") if isinstance(payload.get("allowedLanguages"), list) else [], 6)
    supported = unique_string_list(payload.get("supportedLanguages") if isinstance(payload.get("supportedLanguages"), list) else [], 6)
    prompt_hints = _prompt_language_hints(payload)
    ref_langs = _reference_allowed_languages(payload)

    if prompt_hints:
        filtered = [lang for lang in prompt_hints if not supported or lang in supported]
        if filtered:
            return filtered
    if explicit:
        return explicit
    if ref_langs:
        narrowed = [lang for lang in ref_langs if not supported or lang in supported]
        return narrowed or ref_langs
    if supported:
        return supported
    return []


def _extract_allowed_languages(payload: Dict[str, Any]) -> List[str]:
    return infer_allowed_languages(payload)


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


NO_INPUT_SENTINEL = ""

# Removed deterministic subject-template draft builders.
# Draft synthesis must stay LLM-first; sanitization may only wrap/normalize model output.

def _default_code_test_draft_for_seed(payload: Dict[str, Any]) -> Dict[str, Any] | None:
    return None


def _looks_like_actual_html(value: Any) -> bool:
    text = normalize_text(value)
    if not text or "<" not in text or ">" not in text:
        return False
    return bool(re.search(r"<\s*/?\s*(p|br|div|span|section|article|strong|b|em|i|u|code|pre|blockquote|ul|ol|li|h[1-6]|img|a)\b", text, flags=re.IGNORECASE))


def _normalize_test_input_value(value: Any) -> str:
    return "" if value is None else str(value)


def _is_site_incompatible_test_case(test: Dict[str, Any]) -> bool:
    return not isinstance(test, dict)


def _extract_blueprint_contract(payload: Dict[str, Any]) -> Dict[str, Any]:
    ctx = payload.get("structuredContext") if isinstance(payload.get("structuredContext"), dict) else {}
    if normalize_text(ctx.get("kind")) == "approved-chat-blueprint":
        must_keep = [normalize_text(x) for x in list(ctx.get("mustKeep") or []) if normalize_text(x)]
        avoid = [normalize_text(x) for x in list(ctx.get("avoid") or []) if normalize_text(x)]
        public_tests = []
        for item in list(ctx.get("publicTests") or []):
            if not isinstance(item, dict):
                continue
            public_tests.append({
                "input": _normalize_test_input_value(item.get("input")),
                "expectedOutput": normalize_text(item.get("expectedOutput")),
            })
        hidden_tests = []
        for item in list(ctx.get("hiddenTests") or []):
            if not isinstance(item, dict):
                continue
            hidden_tests.append({
                "input": _normalize_test_input_value(item.get("input")),
                "expectedOutput": normalize_text(item.get("expectedOutput")),
            })
        return {
            "kind": "approved-chat-blueprint",
            "title": normalize_text(ctx.get("title")),
            "fullCondition": normalize_text(ctx.get("fullCondition")),
            "conditionPreview": normalize_text(ctx.get("conditionPreview")),
            "goal": normalize_text(ctx.get("goal")),
            "mustKeep": must_keep,
            "avoid": avoid,
            "placementAfterAssignmentId": normalize_text(ctx.get("placementAfterAssignmentId") or ctx.get("afterAssignmentId")),
            "placementAfterTitle": normalize_text(ctx.get("placementAfterTitle") or ctx.get("afterAssignmentTitle")),
            "placementReason": normalize_text(ctx.get("placementReason")),
            "publicTests": public_tests,
            "hiddenTests": hidden_tests,
            "hasPublicTestsField": "publicTests" in ctx,
            "hasHiddenTestsField": "hiddenTests" in ctx,
        }
    return {}


def _extract_fixed_output_literal(contract: Dict[str, Any], payload: Dict[str, Any]) -> str:
    texts = [
        contract.get("fullCondition"),
        contract.get("conditionPreview"),
        payload.get("sourceText"),
        payload.get("teachingScript"),
        payload.get("userInstructionSnapshot"),
        payload.get("prompt"),
    ]
    for text in texts:
        norm = normalize_text(text)
        if not norm:
            continue
        for pattern in [
            r'cout\s*<<\s*"([^"]{1,120})"',
            r"cout\s*<<\s*'([^']{1,120})'",
            r'вывед[ие][тс]?\s+на\s+экран\s+(?:слово|фразу|строку)\s+"([^"]{1,120})"',
            r'вывед[ие][тс]?\s+на\s+экран\s+(?:слово|фразу|строку)\s+«([^»]{1,120})»',
        ]:
            m = re.search(pattern, norm, flags=re.IGNORECASE)
            if m:
                return normalize_text(m.group(1))
    return ""


def _contains_input_call_markers(text: str) -> bool:
    hay = normalize_text(text).lower()
    if not hay:
        return False
    markers = [
        " cin", "cin ", "scanf", "printf", "getline",
        "считай", "считать", "прочитай", "прочитать", "введи", "ввести",
        "входные данные", "прочесть",
    ]
    return any(marker in hay for marker in markers)


def _blueprint_requires_no_input(contract: Dict[str, Any], payload: Dict[str, Any]) -> bool:
    texts = [
        contract.get("fullCondition"),
        contract.get("conditionPreview"),
        payload.get("sourceText"),
        payload.get("teachingScript"),
        payload.get("prompt"),
        payload.get("userInstructionSnapshot"),
    ]
    hay = "\n".join(normalize_text(x).lower() for x in texts if normalize_text(x))
    if any(token in hay for token in [
        "входные данные не требуются",
        "ничего считывать",
        "без ввода",
        "программа не должна ничего считывать",
        "игнорируй ввод",
        "ввод отсутствует",
    ]):
        return True
    fixed_output = _extract_fixed_output_literal(contract, payload)
    must_keep = " ".join(normalize_text(x).lower() for x in list(contract.get("mustKeep") or []) if normalize_text(x))
    public_tests = contract.get("publicTests") if isinstance(contract.get("publicTests"), list) else []
    explicit_input_examples = any(
        normalize_text((x or {}).get("input")) not in {"", NO_INPUT_SENTINEL}
        for x in public_tests if isinstance(x, dict)
    )
    if fixed_output and not explicit_input_examples and not _contains_input_call_markers(hay) and not _contains_input_call_markers(must_keep):
        return True
    if 'cout <<' in hay and not _contains_input_call_markers(hay.replace('cout <<', '')):
        return True
    return False


def _derive_contract_code_policy(contract: Dict[str, Any], fixed_output: str, no_input: bool) -> Tuple[List[str], List[str]]:
    must_keep = [normalize_text(x).lower() for x in list(contract.get("mustKeep") or []) if normalize_text(x)]
    avoid = [normalize_text(x).lower() for x in list(contract.get("avoid") or []) if normalize_text(x)]
    required: List[str] = []
    forbidden: List[str] = []
    if fixed_output or any("cout" in x for x in must_keep):
        required.append("cout")
    if any("cin" in x for x in must_keep):
        required.append("cin")
    if any("scanf" in x for x in must_keep):
        required.append("scanf")
    if any("printf" in x for x in must_keep):
        required.append("printf")
    if no_input:
        forbidden.extend(["cin", "scanf", "printf"])
    for token in avoid:
        if "scanf" in token:
            forbidden.append("scanf")
        if "printf" in token:
            forbidden.append("printf")
        if "cin" in token:
            forbidden.append("cin")
        if "cout" in token:
            forbidden.append("cout")
    required, forbidden, _ = strip_conflicting_lists(required, forbidden)
    return unique_string_list(required, 8), unique_string_list(forbidden, 10)


def _apply_blueprint_contract_to_code_test_draft(draft: Dict[str, Any], payload: Dict[str, Any]) -> Dict[str, Any]:
    contract = _extract_blueprint_contract(payload)
    if contract.get("kind") != "approved-chat-blueprint":
        return draft
    aligned = dict(draft)
    title = contract.get("title")
    if title:
        aligned["title"] = title
    full_condition = contract.get("fullCondition") or contract.get("conditionPreview")
    if full_condition:
        aligned["description"] = full_condition

    fixed_output = _extract_fixed_output_literal(contract, payload)
    no_input = _blueprint_requires_no_input(contract, payload)

    def _normalize_tests(tests: Any) -> List[Dict[str, Any]]:
        items = [dict(x) for x in list(tests or []) if isinstance(x, dict)]
        result: List[Dict[str, Any]] = []
        for item in items:
            inp = NO_INPUT_SENTINEL if no_input else _normalize_test_input_value(item.get("input"))
            exp = normalize_text(item.get("expectedOutput"))
            if fixed_output:
                exp = fixed_output + ("\n" if not fixed_output.endswith("\n") else "")
            if not exp:
                continue
            result.append({"input": inp, "expectedOutput": exp})
        return result

    public_tests = _normalize_tests(aligned.get("publicTests"))
    hidden_tests = _normalize_tests(aligned.get("hiddenTests"))

    if contract.get("publicTests"):
        seed_tests = [dict(x) for x in contract.get("publicTests") if isinstance(x, dict)]
        public_tests = []
        for item in seed_tests:
            exp = normalize_text(item.get("expectedOutput"))
            if fixed_output:
                exp = fixed_output + ("\n" if not fixed_output.endswith("\n") else "")
            if not exp:
                continue
            public_tests.append({"input": NO_INPUT_SENTINEL if no_input else _normalize_test_input_value(item.get("input")), "expectedOutput": exp})

    if fixed_output:
        desired = fixed_output + ("\n" if not fixed_output.endswith("\n") else "")
        if not public_tests:
            base_input = NO_INPUT_SENTINEL if no_input else "0"
            public_tests = [{"input": base_input, "expectedOutput": desired}]
        if not hidden_tests:
            hidden_tests = [{"input": NO_INPUT_SENTINEL if no_input else "1", "expectedOutput": desired}]
        aligned["referenceSolutionPython"] = (
            f'import sys\n'
            f'def solve():\n'
            f'    _ = sys.stdin.read()\n'
            f'    print({fixed_output!r})\n'
            f'if __name__ == "__main__":\n'
            f'    solve()'
        )

    if no_input:
        public_tests = [{**t, "input": NO_INPUT_SENTINEL} for t in public_tests]
        hidden_tests = [{**t, "input": NO_INPUT_SENTINEL} for t in hidden_tests]

    if public_tests or contract.get("hasPublicTestsField"):
        aligned["publicTests"] = public_tests
    if hidden_tests or contract.get("hasHiddenTestsField"):
        aligned["hiddenTests"] = hidden_tests

    must_keep = [x.casefold() for x in list(contract.get("mustKeep") or [])]
    required = unique_string_list(aligned.get("requiredCalls") or [], 8)
    forbidden = unique_string_list(aligned.get("forbiddenCalls") or [], 10)

    if any("cout" in x for x in must_keep) or fixed_output:
        required.append("cout")
        forbidden = [x for x in forbidden if x.casefold() != "cout"]
    if any("int main" in x for x in must_keep):
        forbidden = [x for x in forbidden if x.casefold() not in {"main", "int main"}]
    if no_input:
        forbidden.extend(["scanf", "printf", "cin"])
        required = [x for x in required if x.casefold() not in {"scanf", "printf", "cin"}]

    required, forbidden, _ = strip_conflicting_lists(required, forbidden)
    aligned["requiredCalls"] = unique_string_list(required, 8)
    aligned["forbiddenCalls"] = unique_string_list(forbidden, 10)

    placement_after_id = normalize_text(contract.get("placementAfterAssignmentId"))
    if placement_after_id:
        aligned["placementAfterAssignmentId"] = placement_after_id
    placement_after_title = normalize_text(contract.get("placementAfterTitle"))
    if placement_after_title:
        aligned["placementAfterTitle"] = placement_after_title
    placement_reason = normalize_text(contract.get("placementReason"))
    if placement_reason:
        aligned["placementReason"] = placement_reason

    return aligned


def _limit_hidden_tests(hidden_tests: Any) -> List[Dict[str, Any]]:
    tests = [dict(x) for x in list(hidden_tests or []) if isinstance(x, dict)]
    seen = set()
    unique: List[Dict[str, Any]] = []
    for test in tests:
        if _is_site_incompatible_test_case(test):
            continue
        key = (_normalize_test_input_value(test.get("input")), normalize_text(test.get("expectedOutput")))
        if key in seen:
            continue
        seen.add(key)
        unique.append({"input": key[0], "expectedOutput": key[1]})
    return unique[: max(1, MAX_HIDDEN_TESTS)]


_TITLE_LEADING_FILLERS = [
    "Корректное ", "Точное ", "Точный ", "Базовый ", "Базовая ",
    "Простое ", "Простой ", "Смешанный ", "Учебное ", "Новый ", "Новая ",
    "Задача на ", "Упражнение на ",
]
_TITLE_BANNED_EXACT = {"задание", "новая задача", "code-test", "task", "draft"}


def _reference_title_examples(payload: Dict[str, Any]) -> List[str]:
    refs = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    seen: set[str] = set()
    result: List[str] = []
    for ref in refs:
        if not isinstance(ref, dict):
            continue
        title = normalize_text(ref.get("title"))
        low = title.casefold()
        if not title or low in seen:
            continue
        seen.add(low)
        result.append(title)
    return result


def _avg_reference_title_words(payload: Dict[str, Any]) -> int:
    refs = _reference_title_examples(payload)
    if not refs:
        return 3
    counts = [len(title.split()) for title in refs if title]
    return max(2, min(4, round(sum(counts) / max(1, len(counts)))))


def _clean_title_candidate(title: Any, payload: Dict[str, Any]) -> str:
    clean = normalize_text(title)
    if not clean:
        return ""
    clean = re.sub(r"\s+[—-]\s*(revised|draft|final|version)\b.*$", "", clean, flags=re.I).strip()
    clean = re.sub(r"\s*\((?:draft|final|revised|version|черновик|новый).*$", "", clean, flags=re.I).strip()
    clean = clean.strip(" .:-")
    words = clean.split()
    for filler in _TITLE_LEADING_FILLERS:
        if clean.lower().startswith(filler.lower()) and len(words) >= 3:
            clean = clean[len(filler):].strip()
            words = clean.split()
            break
    avg_words = _avg_reference_title_words(payload)
    if " — " in clean and len(words) > avg_words + 1:
        head = clean.split(" — ", 1)[0].strip()
        if len(head.split()) >= 2:
            clean = head
            words = clean.split()
    if " с " in clean and len(words) >= max(4, avg_words + 1):
        head, tail = clean.split(" с ", 1)
        tail_low = tail.lower()
        if len(head.split()) >= 2 and any(token in tail_low for token in ["префикс", "формат", "разделител", "суффикс", "пояснен"]):
            clean = head.strip()
            words = clean.split()
    if len(words) > max(4, avg_words + 1):
        clean = " ".join(words[: max(2, avg_words + 1)])
    return truncate_text(clean.strip(), 72)


def _looks_generic_title(title: str) -> bool:
    low = normalize_text(title).lower()
    if not low or low in _TITLE_BANNED_EXACT:
        return True
    if low.startswith("задание ") or low.startswith("задача "):
        return True
    if len(low.split()) == 1 and low in {"ввод", "вывод", "строка", "число", "формат"}:
        return True
    return False


def _rebalance_code_tests(draft: Dict[str, Any], payload: Dict[str, Any]) -> None:
    public_tests = [dict(x) for x in list(draft.get("publicTests") or []) if isinstance(x, dict) and not _is_site_incompatible_test_case(dict(x))]
    hidden_tests = [dict(x) for x in list(draft.get("hiddenTests") or []) if isinstance(x, dict) and not _is_site_incompatible_test_case(dict(x))]
    quality = payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {}
    prefer_public_more = bool(quality.get("preferPublicTestsMoreThanHidden", True))
    if not prefer_public_more:
        draft["publicTests"] = public_tests
        draft["hiddenTests"] = _limit_hidden_tests(hidden_tests)
        return
    while public_tests and hidden_tests and len(public_tests) <= len(hidden_tests):
        public_tests.append(hidden_tests.pop(0))
    draft["publicTests"] = public_tests
    draft["hiddenTests"] = _limit_hidden_tests(hidden_tests)


def _normalize_generated_draft_fields(draft: Dict[str, Any], payload: Dict[str, Any]) -> Dict[str, Any]:
    normalized = dict(draft)
    desc = normalized.get("description")
    if isinstance(desc, str) and desc.strip():
        normalized["description"] = strip_html_to_text(desc) if _looks_like_actual_html(desc) else normalize_text(desc)
    title = normalize_text(normalized.get("title"))
    if not title or title == "__PENDING_TITLE__":
        fallback_title = normalize_text(payload.get("titleHint")) or normalize_text(((payload.get("brief") or {}).get("titleHint")))
        if not fallback_title:
            fallback_title = extract_first_meaningful_sentence(normalized.get("description"), 64)
        normalized["title"] = fallback_title or "Задание"
    normalized["title"] = _derive_course_style_title(payload, normalized)
    placement = _infer_placement_fields(payload, {**payload.get("task", {}), **normalized})
    normalized["placementAfterAssignmentId"] = placement.get("placementAfterAssignmentId")
    normalized["placementAfterTitle"] = placement.get("placementAfterTitle")
    normalized["placementReason"] = placement.get("placementReason")
    contract = _extract_blueprint_contract(payload)
    if contract.get("kind") == "approved-chat-blueprint":
        if normalize_text(contract.get("placementAfterAssignmentId")):
            normalized["placementAfterAssignmentId"] = normalize_text(contract.get("placementAfterAssignmentId"))
        if normalize_text(contract.get("placementAfterTitle")):
            normalized["placementAfterTitle"] = normalize_text(contract.get("placementAfterTitle"))
        if normalize_text(contract.get("placementReason")):
            normalized["placementReason"] = normalize_text(contract.get("placementReason"))
    if normalize_text(normalized.get("assignmentType")).lower() == "code-test":
        _rebalance_code_tests(normalized, payload)
        normalized = _apply_blueprint_contract_to_code_test_draft(normalized, payload)
        contract = _extract_blueprint_contract(payload)
        if contract.get("kind") == "approved-chat-blueprint":
            if normalize_text(contract.get("title")):
                normalized["title"] = normalize_text(contract.get("title"))
            if normalize_text(contract.get("fullCondition") or contract.get("conditionPreview")):
                normalized["description"] = normalize_text(contract.get("fullCondition") or contract.get("conditionPreview"))
    if normalize_text(normalized.get("assignmentType")).lower() == "code-test":
        normalized = _ensure_solvable_code_test_draft(normalized, payload)
    return normalized


def _derive_course_style_title(payload: Dict[str, Any], draft: Dict[str, Any]) -> str:
    refs = _reference_title_examples(payload)
    ref_lows = {title.casefold() for title in refs}
    avg_words = _avg_reference_title_words(payload)
    candidates = [
        draft.get("title"),
        ((payload.get("task") or {}).get("titleHint") or (payload.get("task") or {}).get("targetSkill") or (payload.get("task") or {}).get("TargetSkill")),
        ((payload.get("brief") or {}).get("titleHint")),
        payload.get("titleHint"),
        extract_first_meaningful_sentence(draft.get("description"), 72),
    ]
    fallback = ""
    for candidate in candidates:
        clean = _clean_title_candidate(candidate, payload)
        if not clean:
            continue
        if not fallback:
            fallback = clean
        if _looks_generic_title(clean):
            continue
        if clean.casefold() in ref_lows and len(clean.split()) > avg_words:
            continue
        return clean
    return fallback or "Задание"


def _normalize_code_policy_lists(draft: Dict[str, Any]) -> None:
    draft["requiredCalls"] = unique_string_list(draft.get("requiredCalls"), 6)
    draft["forbiddenCalls"] = unique_string_list(draft.get("forbiddenCalls"), 10)


def _ensure_solvable_code_test_draft(draft: Dict[str, Any], payload: Dict[str, Any]) -> Dict[str, Any]:
    repaired = dict(draft)
    _normalize_code_policy_lists(repaired)
    contract = _extract_blueprint_contract(payload)
    fixed_output = _extract_fixed_output_literal(contract, payload)
    no_input = _blueprint_requires_no_input(contract, payload)
    if contract.get("kind") == "approved-chat-blueprint":
        repaired = _apply_blueprint_contract_to_code_test_draft(repaired, payload)
        if fixed_output:
            desired = fixed_output + ("\n" if not fixed_output.endswith("\n") else "")
            if not isinstance(repaired.get("publicTests"), list) or not repaired.get("publicTests"):
                repaired["publicTests"] = [{"input": "" if no_input else "0", "expectedOutput": desired}]
            if not isinstance(repaired.get("hiddenTests"), list):
                repaired["hiddenTests"] = []
    return repaired

def _synthesize_generation_result(payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(result, dict):
        result = {}

    schema_version = normalize_text(payload.get("schemaVersion")) or "draft-v2"
    canonical_only = schema_version == "draft-v2"

    def _map_test_list(value: Any) -> List[Dict[str, Any]]:
        mapped: List[Dict[str, Any]] = []
        for item in list(value or []):
            if not isinstance(item, dict):
                continue
            mapped_item = {
                "input": _normalize_test_input_value(item.get("input") if canonical_only else (item.get("input") or item.get("stdin") or item.get("in"))),
                "expectedOutput": normalize_text(item.get("expectedOutput") if canonical_only else (item.get("expectedOutput") or item.get("expected_output") or item.get("output") or item.get("stdout") or item.get("out"))),
            }
            if _is_site_incompatible_test_case(mapped_item):
                continue
            mapped.append(mapped_item)
        return [x for x in mapped if x.get("input") is not None and x.get("expectedOutput") is not None]

    draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
    if draft is None:
        draft_like = None
        if result.get("title") and result.get("description"):
            draft_like = {
                "assignmentType": normalize_text((result.get("assignmentType") if canonical_only else (result.get("assignmentType") or result.get("assignment_type"))) or payload.get("assignmentType") or "code-test"),
                "title": normalize_text(result.get("title")),
                "description": normalize_text(result.get("description")),
                "publicTests": _map_test_list(result.get("publicTests") if canonical_only else (result.get("publicTests") or result.get("public_tests"))),
                "hiddenTests": _map_test_list(result.get("hiddenTests") if canonical_only else (result.get("hiddenTests") or result.get("hidden_tests"))),
                "referenceSolutionPython": result.get("referenceSolutionPython") if canonical_only else (result.get("referenceSolutionPython") or result.get("reference_solution_python") or result.get("solution") or result.get("python_solution")),
                "allowedLanguages": result.get("allowedLanguages") if canonical_only else (result.get("allowedLanguages") or result.get("allowed_languages")),
                "forbiddenCalls": result.get("forbiddenCalls") if canonical_only else (result.get("forbiddenCalls") or result.get("forbidden_calls")),
                "requiredCalls": result.get("requiredCalls") if canonical_only else (result.get("requiredCalls") or result.get("required_calls")),
                "placementAfterAssignmentId": result.get("placementAfterAssignmentId") or result.get("afterAssignmentId"),
                "placementAfterTitle": result.get("placementAfterTitle") or result.get("afterAssignmentTitle"),
                "placementReason": result.get("placementReason"),
                "attachments": result.get("attachments"),
                "meta": result.get("meta"),
            }
        elif all(result.get(key) is not None for key in ("assignmentType", "title", "description")):
            draft_like = {k: v for k, v in result.items() if k not in {"summary", "decisionSummary", "draftValidation", "status", "score", "checks", "findings", "topReferences", "topOverlaps", "mutationAnalysis"}}

        if isinstance(draft_like, dict):
            result = dict(result)
            result["draft"] = draft_like
            result["summary"] = normalize_text(result.get("summary")) or truncate_text(normalize_text(draft_like.get("description")), 180)
            if not isinstance(result.get("decisionSummary"), dict):
                result["decisionSummary"] = {"confidence": "medium", "source": "schema-wrap"}

    if isinstance(result.get("draft"), dict):
        result["draft"] = _normalize_generated_draft_fields(result["draft"], payload)
        assignment_type = normalize_text(result["draft"].get("assignmentType")).lower()
        if assignment_type == "code-test" and schema_version != "draft-v2" and isinstance(result["draft"].get("codePolicy"), dict):
            policy = result["draft"].get("codePolicy") or {}
            if not isinstance(result["draft"].get("requiredCalls"), list) and isinstance(policy.get("requiredCalls"), list):
                result["draft"]["requiredCalls"] = unique_string_list(policy.get("requiredCalls"), 6)
            if not isinstance(result["draft"].get("forbiddenCalls"), list) and isinstance(policy.get("forbiddenCalls"), list):
                result["draft"]["forbiddenCalls"] = unique_string_list(policy.get("forbiddenCalls"), 10)
        meta = result["draft"].get("meta") if isinstance(result["draft"].get("meta"), dict) else {}
        if assignment_type == "code-test":
            explicit_languages = _extract_allowed_languages(payload)
            draft_langs = unique_string_list(result["draft"].get("allowedLanguages") if isinstance(result["draft"].get("allowedLanguages"), list) else [], 6)
            if explicit_languages:
                draft_langs = [lang for lang in draft_langs if lang in explicit_languages]
                result["draft"]["allowedLanguages"] = draft_langs or explicit_languages
                meta["expectedAllowedLanguages"] = explicit_languages
            elif draft_langs:
                result["draft"]["allowedLanguages"] = draft_langs
            else:
                result["draft"].pop("allowedLanguages", None)
            if isinstance(payload.get("qualityGates"), dict):
                meta["qualityGates"] = {
                    key: payload["qualityGates"].get(key)
                    for key in ["minPublicTests", "minHiddenTests", "minTotalTests", "preferPublicTestsMoreThanHidden"]
                    if payload["qualityGates"].get(key) is not None
                }
        else:
            result["draft"].pop("allowedLanguages", None)
        meta.setdefault("generationSource", "llm")
        result["draft"]["meta"] = meta
    result.setdefault("schemaVersion", normalize_text(payload.get("schemaVersion")) or "draft-v2")
    return result



def _synthesize_assignment_overview(payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    assignment = payload.get("assignment") if isinstance(payload.get("assignment"), dict) else {}
    assignment_id = normalize_text(result.get("assignmentId") or assignment.get("id") or assignment.get("assignmentId") or payload.get("assignmentId"))
    overview = result.get("overview") if isinstance(result.get("overview"), dict) else {}
    merged = dict(overview)
    for key in (
        "isImportant", "importanceScore", "importanceReasons", "pedagogicalRole", "teachingStyle",
        "studentStage", "conceptsIntroduced", "conceptsReinforced", "prerequisites", "surfaceSignals", "courseValue",
    ):
        if key in result and key not in merged:
            merged[key] = result.get(key)

    if not isinstance(merged.get("isImportant"), bool):
        try:
            merged["isImportant"] = float(merged.get("importanceScore") or 0) >= 0.7
        except Exception:
            merged["isImportant"] = False
    try:
        if not isinstance(merged.get("importanceScore"), (int, float)):
            merged["importanceScore"] = float(merged.get("importanceScore")) if normalize_text(merged.get("importanceScore")) else None
    except Exception:
        merged["importanceScore"] = None
    if merged.get("importanceScore") is None:
        merged["importanceScore"] = 0.35
    if not normalize_text(merged.get("pedagogicalRole")):
        merged["pedagogicalRole"] = "skill-drill"
    if not normalize_text(merged.get("teachingStyle")):
        merged["teachingStyle"] = "practice-first"
    if not normalize_text(merged.get("studentStage")):
        merged["studentStage"] = "beginner"
    for list_key, limit in (("importanceReasons", 8), ("conceptsIntroduced", 6), ("conceptsReinforced", 6), ("prerequisites", 6), ("surfaceSignals", 6)):
        merged[list_key] = unique_string_list(merged.get(list_key), limit)

    title = normalize_text(assignment.get("title") or payload.get("titleHint") or "Задание")
    if not normalize_text(merged.get("courseValue")):
        merged["courseValue"] = f"{title} помогает курсу как {normalize_text(merged.get('pedagogicalRole')) or 'skill-drill'}."

    summary = normalize_text(result.get("summary"))
    if not summary:
        reasons = merged.get("importanceReasons") if isinstance(merged.get("importanceReasons"), list) else []
        lead = normalize_text(reasons[0]) if reasons else normalize_text(merged.get("courseValue"))
        summary = truncate_text(f"{title}: {lead}", 220)

    suggestions = unique_string_list(result.get("suggestions"), 6)
    return {
        "assignmentId": assignment_id or None,
        "kind": normalize_text(result.get("kind") or "course-overview") or "course-overview",
        "summary": summary,
        "overview": merged,
        "suggestions": suggestions,
    }

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

    if job_type == "assignment_analyze_existing":
        sanitized = _synthesize_assignment_overview(payload, sanitized)

    return sanitized