from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, Iterable, List

from agent_core.contracts import AgentContextSnapshot, NormalizedMessage, ScenarioDefinition, ScenarioRoute


def _has_any(text: str, values: Iterable[str]) -> bool:
    return any(value in text for value in values)


def _as_dict(value: Any) -> Dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _has_current_draft(context: AgentContextSnapshot) -> bool:
    memory = _as_dict(context.raw_payload.get("memory"))
    return bool(_as_dict(memory.get("currentDraftBlueprint")) or context.recent_drafts)


@dataclass(frozen=True)
class Rule:
    scenario_id: str
    must_contain_any: List[str]
    must_not_contain_any: List[str]
    priority: int
    reason: str


SCENARIO_RULES: List[Rule] = [
    Rule(
        scenario_id="course_analysis",
        must_contain_any=["анализ", "изучи", "изучить", "разбери", "посмотри", "пойми", "проверь курс", "структур", "карта курса"],
        must_not_contain_any=["не анализ"],
        priority=96,
        reason="Пользователь просит изучить/проанализировать курс или набор курсов.",
    ),
    Rule(
        scenario_id="course_gap_audit",
        must_contain_any=["дыр", "пробел", "скач", "не хватает", "слаб", "аудит", "застр", "переход", "перед"],
        must_not_contain_any=["не анализ"],
        priority=95,
        reason="Пользователь просит найти пробелы, скачки сложности или переходы между темами.",
    ),
    Rule(
        scenario_id="guided_ladder",
        must_contain_any=["лесен", "пошаг", "маленьк", "с нуля", "как на скрин", "микро", "ступен", "обучал"],
        must_not_contain_any=["одну сложную", "без разж"],
        priority=90,
        reason="Пользователь просит обучающую лесенку или микро-шаги.",
    ),
    Rule(
        scenario_id="style_matched_tasks",
        must_contain_any=["в стиле курса", "как в курсе", "похож", "как текущ", "ещё задач", "создай задач", "сделай задач", "придумай задач", "сгенер"],
        must_not_contain_any=["другим стилем"],
        priority=82,
        reason="Пользователь просит создать задачи/черновики.",
    ),
    Rule(
        scenario_id="bridge_tasks",
        must_contain_any=["мостик", "мост", "между заданиями", "между темами", "подвести"],
        must_not_contain_any=[],
        priority=84,
        reason="Пользователь просит мостик между заданиями или темами.",
    ),
    Rule(
        scenario_id="draft_revision",
        must_contain_any=["исправ", "передел", "упрост", "сложнее", "мягче", "поправ", "не так"],
        must_not_contain_any=[],
        priority=80,
        reason="Пользователь просит правку существующего результата.",
    ),
]


class ScenarioRouter:
    def select(
        self,
        message: NormalizedMessage,
        context: AgentContextSnapshot,
        scenarios: List[ScenarioDefinition],
    ) -> ScenarioRoute:
        scenario_ids = {s.id for s in scenarios}

        if message.wants_revision and "draft_revision" in scenario_ids and _has_current_draft(context):
            return ScenarioRoute(
                scenario_id="draft_revision",
                confidence=97,
                reason="Пользователь правит уже созданный AI-черновик; нужно сохранить контекст и стиль предыдущего результата.",
                execution_mode="single",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if message.wants_analysis and message.wants_gap_audit and message.wants_ladder:
            return ScenarioRoute(
                scenario_id="course_analysis",
                secondary_scenario_id="course_gap_audit",
                confidence=98,
                reason="Нужно сначала изучить курсы/курс, затем найти дыры; лесенку можно запросить следующим сообщением или отдельным шагом.",
                execution_mode="chain",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if message.wants_gap_audit and message.wants_ladder:
            return ScenarioRoute(
                scenario_id="course_gap_audit",
                secondary_scenario_id="guided_ladder",
                confidence=96,
                reason="Пользователь просит найти пробел и сразу сделать обучающую лесенку.",
                execution_mode="chain",
                requested_count=message.requested_count or 5,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if message.wants_analysis and (message.wants_gap_audit or message.target_concept):
            return ScenarioRoute(
                scenario_id="course_analysis",
                secondary_scenario_id="course_gap_audit",
                confidence=94,
                reason="Пользователь просит изучить курс/курсы и проверить переход к целевой теме.",
                execution_mode="chain",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        candidates: List[tuple[int, Rule]] = []
        for rule in SCENARIO_RULES:
            if rule.scenario_id not in scenario_ids:
                continue
            if _has_any(message.lowered, rule.must_contain_any) and not _has_any(message.lowered, rule.must_not_contain_any):
                candidates.append((rule.priority, rule))

        if message.wants_generation and message.wants_ladder:
            return ScenarioRoute(
                scenario_id="guided_ladder",
                confidence=94,
                reason="Пользователь просит создать обучающую лесенку.",
                execution_mode="single",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if message.wants_generation:
            return ScenarioRoute(
                scenario_id="style_matched_tasks",
                confidence=90,
                reason="Пользователь просит создать задачи; генерация будет выполнена только реальным LLM-вызовом.",
                execution_mode="single",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if candidates:
            _, best = sorted(candidates, key=lambda item: item[0], reverse=True)[0]
            return ScenarioRoute(
                scenario_id=best.scenario_id,
                confidence=best.priority,
                reason=best.reason,
                execution_mode="single",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        return ScenarioRoute(
            scenario_id="free_chat",
            confidence=70,
            reason="Явный сценарий не найден; запускается обычный AI-чат с доступным контекстом курсов.",
            execution_mode="single",
            requested_count=message.requested_count,
            target_concept=message.target_concept,
            requested_style=message.requested_style,
        )
