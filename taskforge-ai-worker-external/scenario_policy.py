from __future__ import annotations

from typing import Dict, Any

_DIRECT_RESULT_MARKERS = [
    "покажи итог", "покажи только итог", "итоговый набор", "без промежуточных фраз", "без промежуточных сообщений",
    "без промежуточного согласования", "без промежуточных согласований", "без согласования", "не проси одобрение",
    "не проси у меня одобрение", "не спрашивай", "готовый результат", "сразу"
]

_GENERATION_VERBS = ["сделай", "создай", "сгенер", "подготов", "собери", "нужна", "нужно", "хочу"]
_GENERATION_TARGETS = ["задач", "задачк", "сери", "лесенк", "пакет", "программ", "мини-программ", "маленьких программ", "тест", "quiz", "упражнен"]


def looks_like_task_generation_intent(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    if not any(marker in low for marker in _GENERATION_VERBS):
        return False
    if not any(marker in low for marker in _GENERATION_TARGETS):
        return False
    if any(marker in low for marker in ["покажи существующие", "перечисли существующие", "список задан", "какие уже есть"]):
        return False
    return True


def requests_direct_result_without_progress(text: str) -> bool:
    low = (text or "").strip().lower()
    if not low:
        return False
    return any(marker in low for marker in _DIRECT_RESULT_MARKERS)


def scenario_supports_direct_generation(profile: Dict[str, Any]) -> bool:
    sid = str((profile or {}).get("id") or "").strip().lower()
    family = str((profile or {}).get("family") or "").strip().lower()
    if family in {"audit", "reordering"}:
        return False
    if sid == "practice-to-theory":
        return False
    return True


def scenario_should_bypass_blueprint(profile: Dict[str, Any], prefer_autonomy: bool, latest_user_text: str) -> bool:
    if not scenario_supports_direct_generation(profile):
        return False
    if prefer_autonomy:
        return True
    return requests_direct_result_without_progress(latest_user_text)
