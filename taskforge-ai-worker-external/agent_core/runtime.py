from __future__ import annotations

from typing import Any, Dict, List

from agent_core.context import ContextSupervisor
from agent_core.contracts import IncomingAgentMessage, ResultEnvelope, ScenarioResult, deep_to_dict
from agent_core.memory import MemoryStore
from agent_core.message import MessageNormalizer
from agent_core.router import ScenarioRouter
from agent_core.validators import ResultValidator
from log import log_event
from scenarios.registry import ScenarioRegistry, build_default_registry


class AgentRuntime:
    def __init__(self, registry: ScenarioRegistry | None = None) -> None:
        self.memory = MemoryStore()
        self.context = ContextSupervisor(self.memory)
        self.router = ScenarioRouter()
        self.validator = ResultValidator()
        self.registry = registry or build_default_registry()

    def handle_chat_turn(self, job: Dict[str, Any]) -> Dict[str, Any]:
        payload = job.get("payload") if isinstance(job.get("payload"), dict) else job
        incoming = IncomingAgentMessage.from_payload(payload)
        normalized = MessageNormalizer.normalize(incoming)
        snapshot = self.context.build_snapshot(incoming, allow_stale=True)
        route = self.router.select(normalized, snapshot, self.registry.definitions())
        log_event(
            "agent-route-selected",
            run_id=str(job.get("id") or payload.get("runId") or ""),
            scenario_id=route.scenario_id,
            secondary_scenario_id=route.secondary_scenario_id,
            confidence=route.confidence,
            reason=route.reason,
        )

        results: List[ScenarioResult] = []
        for scenario_id in [route.scenario_id, route.secondary_scenario_id]:
            if not scenario_id:
                continue
            scenario = self.registry.get(scenario_id)
            result = scenario.run(snapshot, route, previous_results=results)
            result = self.validator.validate(result, snapshot)
            results.append(result)

        final = results[-1]
        artifacts = [
            {
                "type": r.type,
                "scenarioId": r.scenario_id,
                "title": r.data.get("title") or r.summary[:80],
                "data": deep_to_dict(r.data),
                "confidence": r.confidence,
                "warnings": r.warnings,
                "validation": r.validation,
            }
            for r in results
        ]
        result_data = {"type": final.type, **deep_to_dict(final.data)}
        envelope = ResultEnvelope(
            kind="agent_result",
            scenario_id=final.scenario_id,
            status="completed_with_warnings" if final.warnings else "completed",
            assistant_message=final.summary,
            artifacts=artifacts,
            suggested_next_actions=self._suggest_next_actions(final),
            memory_patch=self.memory.build_patch(final.scenario_id, result_data),
            debug={
                "route": deep_to_dict(route),
                "contextFreshness": (snapshot.course_digest or {}).get("freshness"),
                "assignmentCount": (snapshot.course_digest or {}).get("assignmentCount"),
                "warnings": final.warnings,
            },
        )
        return envelope.to_dict()

    def refresh_context(self, job: Dict[str, Any]) -> Dict[str, Any]:
        payload = job.get("payload") if isinstance(job.get("payload"), dict) else job
        incoming = IncomingAgentMessage.from_payload(payload)
        snapshot = self.context.build_snapshot(incoming, allow_stale=False)
        result = self.registry.get("course_analysis").run(snapshot, route=None, previous_results=[])
        result = self.validator.validate(result, snapshot)
        envelope = ResultEnvelope(
            kind="agent_context_refresh_result",
            scenario_id="course_analysis",
            status="completed_with_warnings" if result.warnings else "completed",
            assistant_message=result.summary,
            artifacts=[{"type": result.type, "scenarioId": result.scenario_id, "data": result.data}],
            suggested_next_actions=[{"name": "course_gap_audit", "label": "Найти дыры в курсе"}],
            memory_patch=self.memory.build_patch("course_analysis", {"type": result.type, **result.data}),
            debug={"assignmentCount": (snapshot.course_digest or {}).get("assignmentCount")},
        )
        return envelope.to_dict()

    def run_scenario(self, job: Dict[str, Any]) -> Dict[str, Any]:
        payload = job.get("payload") if isinstance(job.get("payload"), dict) else job
        scenario_id = str(payload.get("scenarioId") or payload.get("scenario_id") or "course_analysis")
        incoming = IncomingAgentMessage.from_payload(payload)
        normalized = MessageNormalizer.normalize(incoming)
        snapshot = self.context.build_snapshot(incoming, allow_stale=True)
        route = self.router.select(normalized, snapshot, self.registry.definitions())
        route.scenario_id = scenario_id
        scenario = self.registry.get(scenario_id)
        result = scenario.run(snapshot, route, previous_results=[])
        result = self.validator.validate(result, snapshot)
        envelope = ResultEnvelope(
            kind="agent_result",
            scenario_id=result.scenario_id,
            status="completed_with_warnings" if result.warnings else "completed",
            assistant_message=result.summary,
            artifacts=[{"type": result.type, "scenarioId": result.scenario_id, "data": result.data}],
            suggested_next_actions=self._suggest_next_actions(result),
            memory_patch=self.memory.build_patch(result.scenario_id, {"type": result.type, **result.data}),
            debug={"forcedScenario": scenario_id},
        )
        return envelope.to_dict()

    @staticmethod
    def _suggest_next_actions(result: ScenarioResult) -> List[Dict[str, str]]:
        if result.type == "course_analysis_report":
            return [
                {"name": "course_gap_audit", "label": "Найти дыры в курсе"},
                {"name": "style_matched_tasks", "label": "Создать задачи в стиле курса"},
            ]
        if result.type == "gap_audit_report":
            return [{"name": "guided_ladder", "label": "Собрать лесенку по главной дыре"}]
        if result.type in {"task_ladder_blueprint", "task_draft_bundle"}:
            return [
                {"name": "draft_revision", "label": "Поправить черновики"},
                {"name": "persist_later", "label": "Сохранение в курс подключим отдельным слоем"},
            ]
        return []
