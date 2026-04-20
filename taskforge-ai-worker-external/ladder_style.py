from __future__ import annotations

import re
from typing import Any, Dict, List, Tuple

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
            for key in (
                "summary",
                "generationPrompt",
                "targetSkill",
                "microGoal",
                "MicroGoal",
                "userIntentSummary",
                "latestExplicitInstruction",
                "latestTeachingScript",
                "objectiveSummary",
            ):
                value = holder.get(key)
                if isinstance(value, str) and value.strip():
                    parts.append(value.strip())
    hay = " ".join(parts)
    if not hay:
        return ""
    for pattern in _CONCEPT_PATTERNS:
        match = pattern.search(hay)
        if not match:
            continue
        candidate = _normalize_concept(match.group(1))
        if candidate:
            return candidate
    return ""


def ladder_style_appendix(profile: Dict[str, Any], payload: Dict[str, Any]) -> str:
    sid = normalize_text(profile.get("id"))
    if sid not in {"step-by-step-ladder", "micro-program-series"}:
        return ""
    concept = extract_learning_concept(payload)
    count = _extract_series_count(payload)
    concept_line = (
        f"- Точная учебная цель серии: «{concept}». Не подменяй её соседней темой и не уезжай в другую конструкцию.\n"
        if concept
        else "- Сохраняй именно ту тему, которую описал пользователь, и не подменяй её другой идеей.\n"
    )
    return (
        "\n- Сценарий: лесенка. Здесь захардкожен только стиль very-friendly guided walkthrough, а не конкретная тема.\n"
        "- Каждая задача должна выглядеть как маленькое пошаговое обучение, похожее на первое понятное задание курса.\n"
        "- Обязательная структура: короткий живой заголовок -> дружелюбное вступление -> блок «Следуй шагам:» -> нумерованные шаги -> спокойная финальная фраза о результате запуска.\n"
        "- После важных шагов давай короткие пояснения в скобках простым языком.\n"
        "- Один слот лесенки = одна самостоятельная завершённая задача. Нельзя ссылаться на соседние задачи серии.\n"
        "- Рост сложности должен быть плавным. В одном слоте нельзя вводить слишком много новых идей сразу.\n"
        "- Запрещено делать сухой олимпиадный statement, начинать текст с «Напиши программу...», или скатываться в обезличенный code-test тон.\n"
        "- Нельзя описывать всю серию целиком внутри одного draft. Нужен только текущий шаг.\n"
        f"- В этой серии шагов: {count}. Используй мягкую прогрессию от базового действия к уверенной практике.\n"
        f"{concept_line}"
    )


def looks_like_ladder_style(draft: Dict[str, Any]) -> bool:
    score, _ = ladder_style_score(draft)
    return score >= 0.72


def looks_too_dry_for_ladder(draft: Dict[str, Any]) -> bool:
    desc = normalize_text(draft.get("description")).strip().lower()
    if not desc:
        return True
    dry_starts = (
        "напиши программу",
        "в единственной строке",
        "даны",
        "на вход подается",
        "на вход подаётся",
        "требуется вывести",
    )
    return desc.startswith(dry_starts)


def ladder_style_score(draft: Dict[str, Any]) -> Tuple[float, List[str]]:
    title = normalize_text(draft.get("title"))
    description = normalize_text(draft.get("description"))
    explanation = normalize_text(draft.get("explanation"))
    text = "\n".join([title, description, explanation]).strip()
    low = text.lower()
    reasons: List[str] = []
    score = 0.0

    if title and len(title) <= 60:
        score += 0.10
    else:
        reasons.append("заголовок слишком длинный или пустой")

    has_intro = any(token in low for token in ["давай", "сейчас", "эта программа", "это задание", "она будет", "он будет"])
    if has_intro:
        score += 0.18
    else:
        reasons.append("нет дружелюбного вступления")

    if "следуй шагам" in low:
        score += 0.22
    else:
        reasons.append("нет отдельного блока «Следуй шагам:»")

    numbered_steps = len(re.findall(r"(?:^|\n)\s*\d+\.", text))
    if numbered_steps >= 3:
        score += 0.18
    else:
        reasons.append("мало нумерованных шагов")

    if text.count("(") >= 2 and text.count(")") >= 2:
        score += 0.12
    else:
        reasons.append("не хватает коротких пояснений в скобках")

    if any(token in low for token in ["после запуска", "посмотри", "увидишь", "появится", "на экране"]):
        score += 0.12
    else:
        reasons.append("нет спокойного финала о результате запуска")

    if not looks_too_dry_for_ladder(draft):
        score += 0.08
    else:
        reasons.append("описание стартует слишком сухо")

    if any(token in low for token in ["следующем шаге", "в следующей задаче", "в этой серии мы", "дальше мы"]):
        score -= 0.12
        reasons.append("задача ссылается на серию вместо самостоятельного шага")

    score = max(0.0, min(1.0, score))
    return score, reasons


def ladder_structure_findings(draft: Dict[str, Any]) -> List[Dict[str, str]]:
    score, reasons = ladder_style_score(draft)
    findings: List[Dict[str, str]] = []
    if score >= 0.72:
        return findings
    mapping = {
        "нет дружелюбного вступления": "Добавь 1-3 коротких человеческих предложения перед шагами.",
        "нет отдельного блока «Следуй шагам:»": "Явно добавь блок «Следуй шагам:» перед списком шагов.",
        "мало нумерованных шагов": "Разверни решение в нумерованный список конкретных действий.",
        "не хватает коротких пояснений в скобках": "После важных шагов дай короткие пояснения в скобках простым языком.",
        "нет спокойного финала о результате запуска": "Заверши задачу фразой о том, что ученик увидит после запуска.",
        "описание стартует слишком сухо": "Не начинай с «Напиши программу...». Сначала дружелюбно введи задачу.",
        "задача ссылается на серию вместо самостоятельного шага": "Убери ссылки на другие шаги и сделай задачу полностью самостоятельной.",
        "заголовок слишком длинный или пустой": "Сделай короткий человеческий заголовок без канцелярита.",
    }
    for reason in reasons:
        findings.append({"reason": reason, "repair": mapping.get(reason, "Подровняй структуру под формат friendly walkthrough.")})
    return findings


def build_slot_descriptor(index: int, total: int) -> str:
    if index <= 0:
        index = 1
    if total <= 0:
        total = 1
    return _SLOT_DESCRIPTORS[min(index - 1, len(_SLOT_DESCRIPTORS) - 1)]


def _extract_series_count(payload: Dict[str, Any]) -> int:
    value = payload.get("count")
    if isinstance(value, int) and value > 0:
        return value
    try:
        parsed = int(str(value or "0"))
        return parsed if parsed > 0 else 1
    except Exception:
        return 1


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
