from __future__ import annotations

import re
from typing import Any, Dict, List

from text_utils import normalize_text

_CONCEPT_PATTERNS = [
    re.compile(r"(?:по|на)\s+теме\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"тему\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"освоени[ея]\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"использовать\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"пользоваться\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"работ[аы]\s+с\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"уч[иа]т[ья]?\s+([^\n\r\.,;:!?]{2,80})", re.I),
]
_NOISE = {
    "с нуля", "пошагово", "по шагам", "для курса", "маленькими шагами", "несколько задач", "несколько программ",
    "серия задач", "серия программ", "лесенка", "задач", "программ", "программа", "задачи", "тема", "темы",
}
_SLOT_DESCRIPTORS = [
    "самый первый микрошаг: одно понятное действие и мгновенный видимый результат",
    "тот же навык, но уже с пользовательским вводом или с чуть более живым вариантом использования",
    "первое аккуратное усложнение: добавляется одна новая маленькая идея поверх базы",
    "спокойная комбинированная практика: два связанных шага, но без резкого скачка сложности",
    "чуть более взрослая задача на тот же навык в реалистичной формулировке",
    "итоговое закрепление всей лесенки без смешивания слишком многих новых идей",
]


def extract_learning_concept(payload: Dict[str, Any]) -> str:
    parts: List[str] = []
    for key in ("prompt", "sourceText", "notes", "titleHint", "teachingScript", "userInstructionSnapshot"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            parts.append(value.strip())
    batch_memory = payload.get("batchMemory") if isinstance(payload.get("batchMemory"), dict) else {}
    agent_state = batch_memory.get("agentState") if isinstance(batch_memory.get("agentState"), dict) else {}
    for holder in (payload.get("brief"), payload.get("task"), batch_memory, agent_state):
        if isinstance(holder, dict):
            for key in ("summary", "generationPrompt", "targetSkill", "microGoal", "MicroGoal", "userIntentSummary", "latestExplicitInstruction", "latestTeachingScript", "objectiveSummary"):
                value = holder.get(key)
                if isinstance(value, str) and value.strip():
                    parts.append(value.strip())
    hay = " ".join(parts)
    if not hay:
        return ""
    for pattern in _CONCEPT_PATTERNS:
        m = pattern.search(hay)
        if not m:
            continue
        candidate = _normalize_concept(m.group(1))
        if candidate:
            return candidate
    return ""


def ladder_style_appendix(profile: Dict[str, Any], payload: Dict[str, Any]) -> str:
    sid = normalize_text(profile.get("id"))
    if sid not in {"step-by-step-ladder", "micro-program-series"}:
        return ""
    concept = extract_learning_concept(payload)
    concept_line = (
        f"- Точная учебная цель серии: «{concept}». Не подменяй её соседней темой и не уезжай в другую конструкцию.\n"
        if concept else
        "- Сохраняй именно ту тему, которую описал пользователь, и не подменяй её другой идеей.\n"
    )
    return (
        "\n- Сценарий: лесенка. Здесь захардкожен только стиль very-friendly guided walkthrough, а не конкретная тема.\n"
        "- Каждая задача должна ощущаться как маленькое обучение, а не как сухая проверка.\n"
        "- Начинай с короткого дружелюбного вступления.\n"
        "- Дальше веди ученика через блок «Следуй шагам:».\n"
        "- Шаги должны быть нумерованными, конкретными и короткими.\n"
        "- После важных шагов давай маленькие пояснения в скобках простым языком.\n"
        "- В финале скажи, что именно ученик увидит после запуска.\n"
        "- Не начинай описание с сухого шаблона «Напиши программу...» без живого объяснения.\n"
        f"{concept_line}"
    )


def looks_like_ladder_style(draft: Dict[str, Any]) -> bool:
    text = " ".join([
        normalize_text(draft.get("title")),
        normalize_text(draft.get("description")),
        normalize_text(draft.get("explanation")),
    ])
    low = text.lower()
    has_intro = any(token in low for token in ["давай", "сейчас", "эта программа", "это задание", "она будет", "он будет"])
    has_steps = "следуй шагам" in low or bool(re.search(r"(?:^|\n)\s*1\.", text))
    has_explanations = "(" in text and ")" in text
    return has_intro and has_steps and has_explanations


def looks_too_dry_for_ladder(draft: Dict[str, Any]) -> bool:
    desc = normalize_text(draft.get("description")).strip().lower()
    if not desc:
        return True
    return desc.startswith("напиши программу") or desc.startswith("в единственной строке") or desc.startswith("даны")


def _normalize_concept(raw: str) -> str:
    value = normalize_text(raw)
    if not value:
        return ""
    value = value.strip('"«»\'“”()[] ')
    value = re.sub(r"^(именно|только|просто)\s+", "", value, flags=re.I)
    for noise in _NOISE:
        value = re.sub(rf"\b{re.escape(noise)}\b", "", value, flags=re.I)
    value = re.sub(r"\s+", " ", value).strip(" -—:")
    if len(value) < 2 or len(value) > 80:
        return ""
    return value
