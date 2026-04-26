from __future__ import annotations

from typing import Any, Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from scenarios.base import Scenario, target_concept


class CourseGapAuditScenario(Scenario):
    definition = ScenarioDefinition(
        id="course_gap_audit",
        name="Поиск дыр в курсе",
        family="audit",
        aliases=["дырки", "пробелы", "скачки", "что пропущено", "найди слабые места"],
        anti_aliases=["не анализируй"],
        default_count=3,
        default_mode="gap-report",
        required_context=["course_digest", "concept_map", "style_profile"],
        pipeline=[
            "ensure_course_digest",
            "detect_gaps",
            "rank_gaps",
            "suggest_bridge_tasks",
            "return_gap_report",
        ],
        output_type="gap_audit_report",
        can_run_directly=True,
        needs_course=True,
    )

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        digest = context.course_digest or {"assignments": []}
        assignments = digest.get("assignments") or []
        findings: List[Dict[str, Any]] = []
        requested_target = target_concept(route, "if")
        findings.extend(self._missing_prerequisites(assignments, requested_target))
        findings.extend(self._difficulty_jumps(assignments))
        findings.extend(self._undertrained_concepts(assignments))
        ranked = sorted(findings, key=lambda item: int(item.get("severity") or 1), reverse=True)[:8]
        warnings: List[str] = []
        if not assignments:
            warnings.append("Нет списка заданий, аудит дыр построен без привязки к assignmentId.")
            ranked.append(self._generic_gap(requested_target))
        if not ranked:
            ranked.append({
                "id": "gap-general-1",
                "kind": "missing_bridge",
                "concept": requested_target,
                "reason": "Явных скачков не найдено, но можно добавить профилактическую лесенку перед следующей новой темой.",
                "severity": 2,
                "suggestedScenario": "guided_ladder",
                "suggestedTaskCount": 4,
                "suggestedDifficulty": 1,
            })
        summary = f"Нашёл {len(ranked)} потенциальных дыр/точек улучшения. Самая важная: {ranked[0].get('reason')}"
        data = {
            "type": "gap_audit_report",
            "title": "Поиск дыр в курсе",
            "courseId": context.course_id,
            "summary": summary,
            "findings": ranked,
        }
        return ScenarioResult(
            type="gap_audit_report",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=86 if assignments else 58,
            warnings=warnings,
            validation={"pipeline": self.definition.pipeline},
        )

    def _missing_prerequisites(self, assignments: List[Dict[str, Any]], requested_target: str) -> List[Dict[str, Any]]:
        findings: List[Dict[str, Any]] = []
        for index, item in enumerate(assignments):
            concepts = item.get("concepts") or []
            if requested_target in concepts or (requested_target == "if" and "if" in concepts):
                before = assignments[index - 1] if index > 0 else None
                prior_concepts = [c for a in assignments[:index] for c in (a.get("concepts") or [])]
                if requested_target == "if" and "comparison" not in prior_concepts:
                    findings.append({
                        "id": "gap-if-comparison",
                        "kind": "missing_prerequisite",
                        "concept": "if / условное ветвление",
                        "afterAssignmentId": before.get("id") if before else None,
                        "afterAssignmentTitle": before.get("title") if before else None,
                        "beforeAssignmentId": item.get("id"),
                        "beforeAssignmentTitle": item.get("title"),
                        "reason": "Перед первым if мало отдельной практики на сравнение значений и понимание true/false.",
                        "severity": 5,
                        "suggestedScenario": "guided_ladder",
                        "suggestedTaskCount": 5,
                        "suggestedDifficulty": 1,
                    })
                elif index > 0 and int(item.get("difficulty") or 1) - int(before.get("difficulty") or 1) >= 2:
                    findings.append({
                        "id": f"gap-prereq-{item.get('id')}",
                        "kind": "missing_bridge",
                        "concept": requested_target,
                        "afterAssignmentId": before.get("id"),
                        "afterAssignmentTitle": before.get("title"),
                        "beforeAssignmentId": item.get("id"),
                        "beforeAssignmentTitle": item.get("title"),
                        "reason": "Новая тема появляется сразу после более простой задачи; нужен мостик на 2-4 микрошага.",
                        "severity": 4,
                        "suggestedScenario": "guided_ladder",
                        "suggestedTaskCount": 4,
                        "suggestedDifficulty": 1,
                    })
                break
        return findings

    @staticmethod
    def _difficulty_jumps(assignments: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        findings: List[Dict[str, Any]] = []
        for prev, cur in zip(assignments, assignments[1:]):
            delta = int(cur.get("difficulty") or 1) - int(prev.get("difficulty") or 1)
            if delta >= 2:
                findings.append({
                    "id": f"gap-jump-{cur.get('id')}",
                    "kind": "difficulty_jump",
                    "concept": ", ".join(cur.get("concepts") or []) or "unknown",
                    "afterAssignmentId": prev.get("id"),
                    "afterAssignmentTitle": prev.get("title"),
                    "beforeAssignmentId": cur.get("id"),
                    "beforeAssignmentTitle": cur.get("title"),
                    "reason": f"Сложность растёт с {prev.get('difficulty')} до {cur.get('difficulty')} без промежуточной тренировки.",
                    "severity": min(5, 2 + delta),
                    "suggestedScenario": "bridge_tasks",
                    "suggestedTaskCount": min(5, delta + 1),
                    "suggestedDifficulty": int(prev.get("difficulty") or 1),
                })
        return findings

    @staticmethod
    def _undertrained_concepts(assignments: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        counts: Dict[str, List[Dict[str, Any]]] = {}
        for item in assignments:
            for concept in item.get("concepts") or []:
                counts.setdefault(concept, []).append(item)
        findings: List[Dict[str, Any]] = []
        for concept, items in counts.items():
            if concept in {"output", "input"}:
                continue
            if len(items) == 1:
                item = items[0]
                findings.append({
                    "id": f"gap-undertrained-{concept}",
                    "kind": "undertrained_concept",
                    "concept": concept,
                    "afterAssignmentId": item.get("id"),
                    "afterAssignmentTitle": item.get("title"),
                    "reason": f"Понятие {concept} встречается только один раз; стоит добавить 1-2 закрепляющих задания.",
                    "severity": 3,
                    "suggestedScenario": "style_matched_tasks",
                    "suggestedTaskCount": 2,
                    "suggestedDifficulty": item.get("difficulty", 1),
                })
        return findings

    @staticmethod
    def _generic_gap(requested_target: str) -> Dict[str, Any]:
        return {
            "id": "gap-generic-context-missing",
            "kind": "missing_context",
            "concept": requested_target,
            "reason": "Нужен snapshot заданий курса; пока можно подготовить универсальную лесенку без точного места вставки.",
            "severity": 3,
            "suggestedScenario": "guided_ladder",
            "suggestedTaskCount": 5,
            "suggestedDifficulty": 1,
        }
