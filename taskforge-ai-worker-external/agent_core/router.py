from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, List, Optional

from agent_core.contracts import AgentContextSnapshot, NormalizedMessage, ScenarioDefinition, ScenarioRoute


def _has_any(text: str, values: Iterable[str]) -> bool:
    return any(value in text for value in values)


@dataclass(frozen=True)
class Rule:
    scenario_id: str
    must_contain_any: List[str]
    must_not_contain_any: List[str]
    priority: int
    reason: str


SCENARIO_RULES: List[Rule] = [
    Rule(
        scenario_id="course_gap_audit",
        must_contain_any=["дыр", "пробел", "скач", "не хватает", "слаб", "аудит", "застр"],
        must_not_contain_any=["не анализ"],
        priority=95,
        reason="В сообщении есть запрос на поиск дыр/пробелов в курсе.",
    ),
    Rule(
        scenario_id="guided_ladder",
        must_contain_any=["лесен", "пошаг", "маленьк", "с нуля", "как на скрин", "микро", "ступен"],
        must_not_contain_any=["одну сложную", "без разж"],
        priority=90,
        reason="В сообщении есть запрос на задачки-лесенки или микро-шаги.",
    ),
    Rule(
        scenario_id="style_matched_tasks",
        must_contain_any=["в стиле курса", "как в курсе", "похож", "как текущ", "ещё задач", "создай задач", "сделай задач", "придумай задач"],
        must_not_contain_any=["другим стилем"],
        priority=75,
        reason="В сообщении есть запрос на создание задач в стиле курса.",
    ),
    Rule(
        scenario_id="draft_revision",
        must_contain_any=["исправ", "передел", "упрост", "сложнее", "мягче", "поправ", "не так"],
        must_not_contain_any=[],
        priority=80,
        reason="В сообщении есть запрос на правку существующего черновика/blueprint.",
    ),
    Rule(
        scenario_id="course_analysis",
        must_contain_any=["анализ", "разбери курс", "посмотри курс", "пойми курс", "структур", "карта курса"],
        must_not_contain_any=["не анализ"],
        priority=65,
        reason="В сообщении есть запрос на анализ курса.",
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
        candidates: List[tuple[int, Rule]] = []
        for rule in SCENARIO_RULES:
            if rule.scenario_id not in scenario_ids:
                continue
            if _has_any(message.lowered, rule.must_contain_any) and not _has_any(message.lowered, rule.must_not_contain_any):
                candidates.append((rule.priority, rule))

        if message.wants_gap_audit and message.wants_ladder:
            return ScenarioRoute(
                scenario_id="course_gap_audit",
                secondary_scenario_id="guided_ladder",
                confidence=96,
                reason="Пользователь просит найти дыру/пробел и сразу сделать лесенку.",
                execution_mode="chain",
                requested_count=message.requested_count or 5,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if message.wants_analysis and message.wants_gap_audit:
            return ScenarioRoute(
                scenario_id="course_analysis",
                secondary_scenario_id="course_gap_audit",
                confidence=90,
                reason="Пользователь просит анализ курса и поиск дыр.",
                execution_mode="chain",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if candidates:
            _, best = sorted(candidates, key=lambda item: item[0], reverse=True)[0]
            return ScenarioRoute(
                scenario_id=best.scenario_id,
                confidence=min(99, best.priority + (5 if context.course_id else 0)),
                reason=best.reason,
                execution_mode="single",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        if message.wants_generation:
            fallback = "guided_ladder" if message.wants_ladder else "style_matched_tasks"
            return ScenarioRoute(
                scenario_id=fallback,
                confidence=70,
                reason="Пользователь просит создать задачи; выбран безопасный generation fallback.",
                execution_mode="single",
                requested_count=message.requested_count,
                target_concept=message.target_concept,
                requested_style=message.requested_style,
            )

        return ScenarioRoute(
            scenario_id="course_analysis",
            confidence=55,
            reason="Не найден явный сценарий, поэтому выбран безопасный read-only анализ контекста.",
            execution_mode="single",
            requested_count=message.requested_count,
            target_concept=message.target_concept,
            requested_style=message.requested_style,
        )
