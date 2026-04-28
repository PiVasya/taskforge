from __future__ import annotations

from typing import Dict, List

from agent_core.contracts import ScenarioDefinition
from scenarios.base import Scenario
from scenarios.bridge_tasks import BridgeTasksScenario
from scenarios.course_analysis import CourseAnalysisScenario
from scenarios.course_gap_audit import CourseGapAuditScenario
from scenarios.course_edit import CourseEditScenario
from scenarios.draft_revision import DraftRevisionScenario
from scenarios.free_chat import FreeChatScenario
from scenarios.guided_ladder import GuidedLadderScenario
from scenarios.polish_assignment_draft import PolishAssignmentDraftScenario
from scenarios.style_matched_tasks import StyleMatchedTasksScenario


class ScenarioRegistry:
    def __init__(self, scenarios: List[Scenario]) -> None:
        self._scenarios: Dict[str, Scenario] = {scenario.id: scenario for scenario in scenarios}

    def get(self, scenario_id: str) -> Scenario:
        if scenario_id not in self._scenarios:
            raise KeyError(f"Unknown scenario_id: {scenario_id}")
        return self._scenarios[scenario_id]

    def definitions(self) -> List[ScenarioDefinition]:
        return [scenario.definition for scenario in self._scenarios.values()]

    def ids(self) -> List[str]:
        return list(self._scenarios.keys())


def build_default_registry() -> ScenarioRegistry:
    return ScenarioRegistry([
        CourseAnalysisScenario(),
        CourseGapAuditScenario(),
        CourseEditScenario(),
        GuidedLadderScenario(),
        StyleMatchedTasksScenario(),
        BridgeTasksScenario(),
        DraftRevisionScenario(),
        PolishAssignmentDraftScenario(),
        FreeChatScenario(),
    ])
