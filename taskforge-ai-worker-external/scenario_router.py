from __future__ import annotations

import re
from typing import Any, Dict, List

from scenario_catalog import SCENARIO_DEFINITIONS, SCENARIO_BY_ID
from text_utils import normalize_text


def _collect_signal_parts(payload: Dict[str, Any]) -> List[str]:
    parts: List[str] = []
    for key in ("prompt", "sourceText", "notes", "titleHint", "teachingScript", "userInstructionSnapshot"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            parts.append(value.strip())
    for holder_key in ("brief", "task", "batchMemory", "scenario"):
        holder = payload.get(holder_key) if isinstance(payload.get(holder_key), dict) else {}
        for sub_key in ("summary", "generationPrompt", "targetSkill", "microGoal", "MicroGoal", "userIntentSummary", "latestExplicitInstruction", "latestTeachingScript", "pedagogyMode", "id", "name"):
            value = holder.get(sub_key)
            if isinstance(value, str) and value.strip():
                parts.append(value.strip())
    agent_state = (((payload.get("batchMemory") or {}).get("agentState")) if isinstance(payload.get("batchMemory"), dict) else {})
    if isinstance(agent_state, dict):
        for sub_key in ("userIntentSummary", "objectiveSummary", "pedagogyMode", "latestIntentKind"):
            value = agent_state.get(sub_key)
            if isinstance(value, str) and value.strip():
                parts.append(value.strip())
    return parts


def _build_haystack(payload: Dict[str, Any]) -> str:
    return " ".join(_collect_signal_parts(payload)).lower()


def detect_scenario_profile(payload: Dict[str, Any], requested_count: int | None = None) -> Dict[str, Any]:
    explicit = payload.get("scenario") if isinstance(payload.get("scenario"), dict) else {}
    explicit_id = normalize_text(explicit.get("id")).lower()
    if explicit_id and explicit_id in SCENARIO_BY_ID:
        base = dict(SCENARIO_BY_ID[explicit_id])
        base["score"] = 999
        base["matchedSignals"] = ["explicit-scenario-id"]
        base["requestedCount"] = max(1, int(requested_count or payload.get("count") or base.get("default_count") or 1))
        return base

    hay = _build_haystack(payload)
    count = max(1, int(requested_count or payload.get("count") or 1))
    best = None
    best_score = -10**9
    matched: List[str] = []

    for scenario in SCENARIO_DEFINITIONS:
        score = 0
        local_hits: List[str] = []
        for alias in scenario.get("aliases") or []:
            if alias and alias.lower() in hay:
                score += 4 if len(alias) >= 10 else 3
                local_hits.append(alias)
        for anti in scenario.get("anti") or []:
            if anti and anti.lower() in hay:
                score -= 4 if len(anti) >= 10 else 3
        if scenario.get("prefer_single_deep_task") and count <= 1:
            score += 2
        if scenario.get("id") in {"step-by-step-ladder", "micro-program-series"} and count > 1:
            score += 2
        if scenario.get("require_explicit_if") and re.search(r"\bif\b", hay):
            score += 2
        if score > best_score:
            best = scenario
            best_score = score
            matched = local_hits[:6]

    base = dict(best or SCENARIO_BY_ID["general-topic-pack"])
    if best_score <= 0:
        base = dict(SCENARIO_BY_ID["general-topic-pack"])
        matched = []
    base["score"] = max(best_score, 0)
    base["matchedSignals"] = matched
    base["requestedCount"] = count
    if base.get("id") in {"step-by-step-ladder", "micro-program-series"} and re.search(r"\bif\b", hay):
        base["require_explicit_if"] = True
    return base


def scenario_generation_mode(profile: Dict[str, Any], requested_count: int | None = None) -> str:
    count = max(1, int(requested_count or profile.get("requestedCount") or profile.get("default_count") or 1))
    if count <= 1 or profile.get("prefer_single_deep_task"):
        return "single-draft"
    return normalize_text(profile.get("default_mode") or "topic-pack") or "topic-pack"


def scenario_requires_explicit_if(profile: Dict[str, Any]) -> bool:
    return bool(profile.get("require_explicit_if"))


def is_progression_scenario(profile: Dict[str, Any]) -> bool:
    return normalize_text(profile.get("id")) in {"step-by-step-ladder", "micro-program-series"}


def scenario_prompt_appendix(profile: Dict[str, Any]) -> str:
    sid = normalize_text(profile.get("id"))
    if sid == "micro-program-series" and profile.get("require_explicit_if"):
        return (
            "\n- Сценарий: серия маленьких программ на освоение if. Это не bridge-pack и не подготовительные булевы проверки. "
            "Нужны самостоятельные мини-программы с явным if и очень маленьким шагом сложности.\n"
        )
    if sid == "step-by-step-ladder":
        return "\n- Сценарий: лесенка. Каждое следующее задание должно быть лишь немного сложнее предыдущего, без резких скачков.\n"
    if sid == "single-deep-task":
        return "\n- Сценарий: одна сильная задача. Не дроби идею на серию микрошагов и не превращай запрос в лесенку.\n"
    if sid == "pretopic-bridges":
        return "\n- Сценарий: мягкие мостики перед новой темой. Сохраняй плавный переход и не выстреливай в слишком взрослую задачу сразу.\n"
    if sid == "russian-language-tests":
        return "\n- Сценарий: тесты по русскому языку. Не подменяй предмет программированием и не уходи в code-test форму.\n"
    return ""
