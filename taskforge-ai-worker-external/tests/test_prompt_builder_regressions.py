import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import prompt_builder  # type: ignore


LONG_DESC = (
    "Это достаточно длинное описание задания для проверки регрессионного сценария. "
    "Нужно убедиться, что prompt builder больше не падает из-за перепутанного порядка аргументов. "
    "Описание специально длиннее минимального порога и содержит несколько фраз про вход, вывод и ограничения."
)


class PromptBuilderRegressionTests(unittest.TestCase):
    def test_build_chat_turn_prompt_mentions_remediation_agent_loop(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {
                "agentState": {
                    "objectiveKind": "course-gap-remediation",
                    "objectiveSummary": "Найти пробелы по курсу, проверить соседние задания и собрать решения",
                    "currentStage": "discover",
                    "confidencePercent": 42,
                    "openQuestions": ["Нужно подтвердить пробелы по курсу."],
                    "decisionCandidates": [{"name": "analyze_course_progression", "why": "Нужен discovery", "status": "preferred"}],
                    "subtasks": [
                        {"key": "discover-gaps", "title": "Найти пробелы", "status": "current"},
                        {"key": "verify-context", "title": "Проверить соседние задания", "status": "pending"},
                    ],
                    "planSteps": [
                        {"key": "discover", "title": "Discovery по курсу", "status": "current", "recommendedAction": "analyze_course_progression", "successSignal": "Есть findings аудита."},
                        {"key": "verify", "title": "Проверка соседних заданий", "status": "pending", "recommendedAction": "inspect_course_assignments", "blockedBy": "Сначала нужен discovery."},
                    ],
                    "blockerSummary": "Нужно подтвердить пробелы по курсу.",
                    "needsClarification": True,
                    "autonomyMode": "ask-first",
                }
            },
            "conversation": [{"role": "user", "content": "Сгенерируй задачи на все пробелы в курсе"}],
            "availableActions": [
                {"name": "advance_agent_stage"},
                {"name": "analyze_course_progression"},
                {"name": "inspect_course_assignments"},
                {"name": "prepare_bridge_plan"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("objectiveKind", prompt)
        self.assertIn("subtasks", prompt)
        self.assertIn("planSteps", prompt)
        self.assertIn("confidencePercent", prompt)
        self.assertIn("blockerSummary", prompt)
        self.assertIn("autonomyMode", prompt)
        self.assertIn("openQuestions", prompt)
        self.assertIn("decisionCandidates", prompt)
        self.assertIn("remediation-agent", prompt)
        self.assertIn("discover", prompt)
        self.assertIn("prepare_bridge_plan", prompt)


    def test_build_chat_turn_prompt_mentions_diagnostic_grounding_rules(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {
                "lastCourseAudit": {"summary": "Есть старый эвристический аудит"},
                "lastCourseInspection": {
                    "summary": "Открыла реальные задания",
                    "assignments": [{"id": "a1", "title": "Задание 1"}],
                    "observations": ["Первое задание уже выглядит как пошаговая обучалка по cout."],
                },
            },
            "conversation": [{"role": "user", "content": "Точно ли нету обучалки cout в курсе? посмотри точнее"}],
            "availableActions": [
                {"name": "analyze_course_progression"},
                {"name": "inspect_course_assignments"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("inspection > audit", prompt)
        self.assertIn("по реальным условиям", prompt)
        self.assertIn("подтвердилось / что не подтвердилось / что осталось проверить", prompt)
    def test_build_chat_turn_prompt_mentions_landmark_assignments_and_coverage(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "courseOverviewCoverage": {
                "totalAssignments": 80,
                "assignmentsWithOverview": 72,
                "assignmentsMissingOverview": 8,
                "coverageRatio": 0.9,
            },
            "landmarkAssignments": [
                {
                    "id": "a1",
                    "sort": 0,
                    "difficulty": 2,
                    "title": "Задание 1. Твой первый вывод",
                    "type": "code-test",
                    "aiOverview": {
                        "summary": "Пошаговая обучалка по первой программе.",
                        "isImportant": True,
                        "importanceScore": 0.97,
                        "pedagogicalRole": "guided-intro",
                        "importanceReasons": ["Открывает курс и задаёт стиль объяснений."],
                    },
                }
            ],
            "conversation": [{"role": "user", "content": "Посмотри, какие задания в курсе самые важные"}],
            "availableActions": [
                {"name": "inspect_course_assignments"},
                {"name": "analyze_course_progression"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("landmarkAssignments", prompt)
        self.assertIn("courseOverviewCoverage", prompt)
        self.assertIn("persisted AI overview", prompt)
        self.assertIn("guided-intro/bridge/milestone", prompt)

    def test_build_chat_turn_prompt_mentions_auto_overview_bootstrap(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "autoOverviewBootstrap": {
                "courseId": "c1",
                "queuedJobsCount": 12,
                "assignmentsMissingOverview": 24,
                "autoTriggered": True,
            },
            "conversation": [{"role": "user", "content": "Проанализируй курс и найди пробелы"}],
            "availableActions": [
                {"name": "analyze_course_progression"},
                {"name": "inspect_course_assignments"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("autoOverviewBootstrap", prompt)
        self.assertIn("система сама подтягивает обзоры", prompt)

    def test_build_chat_turn_prompt_mentions_anchor_and_count_rules_for_multi_mode(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "actionMode": "multi",
            "conversation": [{"role": "user", "content": "Сделай 5 задач перед 20 заданием как первая задача"}],
            "availableActions": [
                {"name": "inspect_course_assignments"},
                {"name": "save_chat_blueprint"},
                {"name": "revise_chat_blueprint"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("multi-режим", prompt)
        self.assertIn("перед 20 заданием", prompt)
        self.assertIn("должны совпадать по количеству", prompt)
        self.assertIn("как первая задача", prompt)
        self.assertIn("if/else", prompt)



    def test_build_draft_generate_prompt_accepts_dict_payload(self):
        payload = {
            "assignmentType": "code-test",
            "schemaVersion": "draft-v2",
            "prompt": "Сгенерируй одну задачу на базовый ввод и вывод в C++",
            "titleHint": "Одно значение и печать",
            "targetSkill": "C++: базовый ввод и вывод — Одно значение и печать",
            "microGoal": "Считать одно значение и вывести его в требуемом формате.",
            "difficulty": 1,
            "count": 1,
            "qualityGates": {},
            "task": {
                "titleHint": "Одно значение и печать",
                "targetSkill": "C++: базовый ввод и вывод — Одно значение и печать",
                "microGoal": "Считать одно значение и вывести его в требуемом формате.",
                "difficultyTarget": 1,
            },
            "courseProfile": {
                "summary": "Минимальный профиль курса",
                "courseDigest": {"languages": ["cpp"]},
                "courseProfile": {},
            },
            "gapAnalysis": {
                "summary": "Минимальный gap analysis",
                "gapAnalysis": {"missingTopics": ["formatted output"]},
                "coverage": {},
            },
        }
        prompt = prompt_builder.build_draft_generate_prompt({"type": "assignment_generate_from_text"}, payload)
        self.assertIn("TaskForge AI", prompt)
        self.assertIn("Одно значение и печать", prompt)


    def test_build_chat_turn_prompt_mentions_prepare_bridge_plan(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {"lastCourseAudit": {"summary": "Есть пробелы"}, "agentState": {"currentStage": "audit-ready", "userIntentSummary": "Сначала изучить курс, потом собрать мостики", "nextSuggestedAction": "prepare_bridge_plan"}},
            "conversation": [{"role": "user", "content": "собери план мостиков"}],
            "availableActions": [
                {"name": "analyze_course_progression"},
                {"name": "inspect_course_assignments"},
                {"name": "prepare_bridge_plan"},
                {"name": "show_bridge_plan"},
                {"name": "revise_bridge_plan"},
                {"name": "advance_agent_stage"},
                {"name": "queue_generate_bridge_batch"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("prepare_bridge_plan", prompt)
        self.assertIn("inspect_course_assignments", prompt)
        self.assertIn("show_bridge_plan", prompt)
        self.assertIn("revise_bridge_plan", prompt)
        self.assertIn("advance_agent_stage", prompt)
        self.assertIn("agentState", prompt)
        self.assertIn("prepare_bridge_plan", prompt)
        self.assertIn("Новый явный запрос пользователя важнее старой подсказки из памяти", prompt)
        self.assertIn("show_bridge_plan и revise_bridge_plan подходят только когда пользователь прямо просит", prompt)
        self.assertIn("diagnost", prompt.lower())


    def test_build_batch_plan_prompt_uses_batch_memory_for_guided_walkthroughs(self):
        payload = {
            "assignmentType": "code-test",
            "prompt": "Нужно укрепить базу перед циклами",
            "count": 4,
            "difficulty": 1,
            "mode": "topic-pack",
            "referenceAssignments": [
                {"id": "a1", "title": "Задание 1. Вывод через cout", "description": LONG_DESC, "difficulty": 1, "sort": 1, "type": "code-test", "allowedLanguagesCsv": "cpp"},
                {"id": "a2", "title": "Задание 2. Вывод через printf", "description": LONG_DESC, "difficulty": 1, "sort": 2, "type": "code-test", "allowedLanguagesCsv": "cpp"},
            ],
            "batchMemory": {
                "learnerProfile": {"audience": "young-beginners", "explainLikeChild": True, "preferGuidedWalkthroughs": True, "requireSectionIntroGuides": True},
                "pedagogy": {"preferGuidedWalkthroughs": True, "requireSectionIntroGuides": True, "explainLikeChild": True},
                "titleStyle": {"examples": ["Задание 1. Вывод через cout", "Задание 2. Вывод через printf"], "styleHints": ["короткие конкретные названия"]},
                "placementPlan": [{"afterAssignmentId": "a1", "afterAssignmentTitle": "Задание 1. Вывод через cout", "concept": "printf", "reason": "мягко подвести к printf", "taskCount": 2, "difficulty": 1, "taskFormat": "guided-walkthrough"}],
                "constraints": {"mustStayBeforeConcepts": ["циклы"], "avoidConcepts": ["циклы"]},
                "agentState": {"userIntentSummary": "Сделать мягкие мостики перед циклами", "currentStage": "bridge-ready", "placementCandidates": [{"afterAssignmentId": "a1", "afterAssignmentTitle": "Задание 1. Вывод через cout", "concept": "printf", "reason": "мягко подвести к printf"}]},
            },
        }
        prompt = prompt_builder.build_batch_plan_prompt({"type": "assignment_batch_plan"}, payload)
        self.assertIn("batchMemory", prompt)
        self.assertIn("guided-walkthrough", prompt)
        self.assertIn("циклы", prompt)
        self.assertIn("agentState", prompt)

    def test_build_chat_turn_prompt_compacts_huge_payload(self):
        huge_text = "Очень длинный фрагмент контекста. " * 4000
        payload = {
            "sessionId": "s1",
            "sessionTitle": huge_text,
            "courseId": "c1",
            "memory": {
                "summary": huge_text,
                "latestExplicitInstruction": huge_text,
                "facts": [huge_text] * 10,
                "recentGoals": [huge_text] * 10,
                "recentActions": ["inspect_course_assignments"] * 10,
                "agentState": {
                    "objectiveSummary": huge_text,
                    "evidenceLedger": [huge_text] * 10,
                    "decisionCandidates": [{"name": "inspect_course_assignments", "why": huge_text, "status": "preferred"}] * 10,
                },
            },
            "conversation": [{"role": "user", "content": huge_text, "toolResults": [{"status": "done", "summary": huge_text}]}] * 12,
            "recentAssignments": [{"id": f"a{i}", "title": huge_text, "latestAiOverview": {"summary": huge_text, "importanceReasons": [huge_text, huge_text], "conceptsIntroduced": [huge_text] * 5}} for i in range(20)],
            "landmarkAssignments": [{"id": f"l{i}", "title": huge_text, "aiOverview": {"summary": huge_text, "importanceReasons": [huge_text, huge_text], "conceptsIntroduced": [huge_text] * 5}} for i in range(20)],
            "availableActions": [{"name": "inspect_course_assignments", "description": huge_text, "requiredArguments": ["courseId"] * 10, "optionalArguments": ["focus"] * 10}],
            "availableCourses": [{"id": f"c{i}", "title": huge_text} for i in range(30)],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertLess(len(prompt), 120000)
        self.assertIn("recentAssignments", prompt)
        self.assertIn("conversation", prompt)


if __name__ == "__main__":
    unittest.main()
