from __future__ import annotations

import re
from typing import Optional

from agent_core.contracts import IncomingAgentMessage, NormalizedMessage


COUNT_PATTERNS = [
    re.compile(r"(?:сделай|создай|дай|нужно|надо)\s+(\d{1,2})\s+(?:задач|задани|шаг)", re.IGNORECASE),
    re.compile(r"(\d{1,2})\s+(?:задач|задани|шаг)", re.IGNORECASE),
]

CONCEPT_ALIASES = {
    "if": ["if", "иф", "услов", "ветвлен", "ветвлени", "если", "else"],
    "input": ["input", "ввод", "считыван", "прочитать", "ввести"],
    "variables": ["переменн", "variable", "присваиван"],
    "loops": ["цикл", "for", "while", "повтор"],
    "arrays": ["массив", "список", "array", "list"],
    "functions": ["функци", "def", "метод"],
    "strings": ["строк", "string"],
    "math": ["математ", "формул", "арифмет"],
}


def _contains_any(text: str, values: list[str]) -> bool:
    return any(value in text for value in values)


def _requested_count(text: str) -> Optional[int]:
    for pattern in COUNT_PATTERNS:
        match = pattern.search(text)
        if match:
            try:
                value = int(match.group(1))
                if 1 <= value <= 20:
                    return value
            except Exception:
                return None
    return None


def _target_concept(text: str) -> Optional[str]:
    for concept, aliases in CONCEPT_ALIASES.items():
        if _contains_any(text, aliases):
            return concept
    return None


class MessageNormalizer:
    @staticmethod
    def normalize(message: IncomingAgentMessage) -> NormalizedMessage:
        text = message.raw_text or ""
        lowered = text.lower()
        wants_ladder = _contains_any(lowered, ["лесен", "пошаг", "маленьк", "с нуля", "как на скрин", "микро", "ступен"])
        wants_gap = _contains_any(lowered, ["дыр", "пробел", "скач", "не хватает", "слаб", "застр", "аудит", "переход", "перед if", "перед иф", "к ним"])
        wants_analysis = _contains_any(lowered, ["анализ", "изучи курс", "изучить курс", "разбери курс", "посмотри курс", "пойми курс", "проверь курс", "структур", "карта курса"])
        wants_style = _contains_any(lowered, ["в стиле курса", "как в курсе", "похож", "как текущ", "продолжи", "стиль"])
        generation_verbs = _contains_any(lowered, ["создай", "сделай", "придумай", "сгенер", "подготовь", "дай ", "накидай"])
        task_words = _contains_any(lowered, ["задач", "задани", "черновик", "упражнен"])
        wants_generation = generation_verbs and task_words
        wants_revision = _contains_any(lowered, [
            "исправ", "передел", "упрост", "сложнее", "мягче", "не так", "поправ",
            "эти же", "те же", "то же", "так же", "эти самые", "прям лесен", "каждым шагом",
            "как задача 1", "как задание 1", "пример задача 1", "пример задание 1"
        ])
        wants_background = _contains_any(lowered, ["на фоне", "фоном", "параллельно", "background"])
        direct_mode = _contains_any(lowered, ["сразу", "делай", "без вопросов", "не спрашивай", "можно хардкод", "разрешаю"])
        requested_style = "ladder_screenshot_1" if _contains_any(lowered, ["скрин", "лесен"]) else None
        return NormalizedMessage(
            text=text,
            lowered=lowered,
            wants_analysis=wants_analysis,
            wants_gap_audit=wants_gap,
            wants_generation=wants_generation,
            wants_ladder=wants_ladder,
            wants_style_match=wants_style,
            wants_revision=wants_revision,
            wants_background=wants_background,
            requested_count=_requested_count(lowered),
            target_concept=_target_concept(lowered),
            requested_style=requested_style,
            direct_mode=direct_mode,
        )
