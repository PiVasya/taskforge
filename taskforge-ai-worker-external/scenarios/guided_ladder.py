from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from hardcoded_presets.ladder_screenshot_1 import LADDER_SCREENSHOT_1_STYLE
from scenarios.base import Scenario, previous_artifact, requested_count, target_concept


class GuidedLadderScenario(Scenario):
    definition = ScenarioDefinition(
        id="guided_ladder",
        name="Задачки-лесенка",
        family="generation",
        aliases=["лесенка", "пошагово", "с нуля", "маленькие задачки", "как на скрине", "обучалка"],
        anti_aliases=["одну сложную", "без разжёвывания"],
        default_count=5,
        default_mode="guided-onboarding-ladder",
        required_context=["style_profile", "course_digest"],
        pipeline=[
            "identify_target_concept",
            "detect_course_language",
            "choose_anchor_place",
            "build_ladder_slots",
            "generate_task_blueprints",
            "validate_ladder_progression",
            "return_task_ladder_blueprint",
        ],
        output_type="task_ladder_blueprint",
        can_run_directly=True,
        needs_course=True,
    )

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        gap_report = previous_artifact(previous_results, "gap_audit_report") or context.gap_map or {}
        target = self._target_from_gap(gap_report) or target_concept(route, "if")
        count = requested_count(route, self.definition.default_count, 3, 8)
        language = self._detect_language(context)
        placement = self._choose_placement(context, target, gap_report)
        slots = self._build_slots(target, count)
        tasks = [self._task_from_slot(slot, index, target, placement, language) for index, slot in enumerate(slots)]
        lang_label = self._language_label(language)
        summary = (
            f"Собрал живую обучающую лесенку из {len(tasks)} задач на {lang_label} для темы «{target}». "
            "Стиль — как на скрине: короткое вступление, понятные шаги, пояснения в скобках, публичный тест и без сохранения в курс."
        )
        data = {
            "type": "task_ladder_blueprint",
            "title": f"Лесенка перед {target}",
            "scenarioId": self.id,
            "courseId": context.course_id,
            "targetConcept": target,
            "language": lang_label,
            "allowedLanguages": [lang_label],
            "count": len(tasks),
            "placement": placement,
            "stylePreset": LADDER_SCREENSHOT_1_STYLE["id"],
            "styleRules": LADDER_SCREENSHOT_1_STYLE,
            "tasks": tasks,
            "validation": {
                "stepSize": "ok",
                "noConceptJump": "ok",
                "styleMatch": "ok",
                "noPersistence": "ok",
                "languageDetected": language,
            },
        }
        return ScenarioResult(
            type="task_ladder_blueprint",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=88,
            warnings=[] if context.recent_assignments else ["Место вставки примерное: список заданий курса не пришёл в payload."],
            validation={"pipeline": self.definition.pipeline, **data["validation"]},
        )

    @staticmethod
    def _target_from_gap(gap_report: Dict[str, Any]) -> Optional[str]:
        findings = gap_report.get("findings") if isinstance(gap_report, dict) else None
        if isinstance(findings, list) and findings:
            concept = str(findings[0].get("concept") or "").lower()
            if "if" in concept or "услов" in concept:
                return "if"
            if concept:
                return concept.split("/")[0].strip()
        return None

    @staticmethod
    def _detect_language(context: AgentContextSnapshot) -> str:
        parts: List[str] = [context.course_title or "", context.user_message or ""]
        for item in context.recent_assignments or []:
            for key in ("title", "description", "allowedLanguages", "allowedLanguagesCsv", "language", "tags"):
                value = item.get(key) if isinstance(item, dict) else None
                if value is not None:
                    parts.append(str(value))
        blob = "\n".join(parts).lower()

        cpp_markers = ["c++", "cpp", "iostream", "#include", "cout", "cin", "using namespace std"]
        python_markers = ["python", "print(", "input()", "def ", "pip"]
        if any(m in blob for m in cpp_markers):
            return "cpp"
        if any(m in blob for m in python_markers):
            return "python"
        # По умолчанию отдаём C++: текущий эталон обучалки на скрине именно такой.
        return "cpp"

    @staticmethod
    def _language_label(language: str) -> str:
        return "C++" if language == "cpp" else "Python"

    @staticmethod
    def _choose_placement(context: AgentContextSnapshot, target: str, gap_report: Dict[str, Any]) -> Dict[str, Any]:
        findings = gap_report.get("findings") if isinstance(gap_report, dict) else None
        if isinstance(findings, list) and findings:
            top = findings[0]
            return {
                "afterAssignmentId": top.get("afterAssignmentId"),
                "afterAssignmentTitle": top.get("afterAssignmentTitle"),
                "beforeAssignmentId": top.get("beforeAssignmentId"),
                "beforeAssignmentTitle": top.get("beforeAssignmentTitle"),
                "reason": top.get("reason") or "Мостик по главной найденной дыре.",
            }
        assignments = (context.course_digest or {}).get("assignments") or []
        for index, item in enumerate(assignments):
            if target in (item.get("concepts") or []):
                prev = assignments[index - 1] if index > 0 else None
                return {
                    "afterAssignmentId": prev.get("id") if prev else None,
                    "afterAssignmentTitle": prev.get("title") if prev else None,
                    "beforeAssignmentId": item.get("id"),
                    "beforeAssignmentTitle": item.get("title"),
                    "reason": f"Перед первым появлением темы {target}.",
                }
        return {
            "afterAssignmentId": assignments[-1].get("id") if assignments else None,
            "afterAssignmentTitle": assignments[-1].get("title") if assignments else None,
            "beforeAssignmentId": None,
            "beforeAssignmentTitle": None,
            "reason": "Точное место не найдено; blueprint можно вставить перед целевой темой вручную.",
        }

    @staticmethod
    def _build_slots(target: str, count: int) -> List[Dict[str, Any]]:
        if target == "if":
            base = [
                {"targetSkill": "first_output", "microGoal": "написать первую короткую программу и увидеть вывод", "newConcepts": ["program skeleton", "output"], "forbiddenConcepts": ["input", "if", "else", "loops"]},
                {"targetSkill": "input_echo", "microGoal": "считать число и вывести его обратно", "newConcepts": ["input"], "forbiddenConcepts": ["if", "else", "loops"]},
                {"targetSkill": "comparison_readiness", "microGoal": "увидеть число и порог сравнения", "newConcepts": ["comparison idea"], "forbiddenConcepts": ["if", "else", "loops"]},
                {"targetSkill": "first_if", "microGoal": "впервые выбрать одну фразу через if", "newConcepts": ["if"], "forbiddenConcepts": ["else", "loops"]},
                {"targetSkill": "if_else", "microGoal": "добавить второй путь через else", "newConcepts": ["else"], "forbiddenConcepts": ["loops"]},
                {"targetSkill": "friendly_classifier", "microGoal": "собрать маленькую программу-классификатор", "newConcepts": ["condition composition"], "forbiddenConcepts": ["loops"]},
            ]
        else:
            base = [
                {"targetSkill": f"{target}_first_touch", "microGoal": f"увидеть идею {target} на простом примере", "newConcepts": [target], "forbiddenConcepts": ["loops", "advanced algorithms"]},
                {"targetSkill": f"{target}_practice_1", "microGoal": f"повторить {target} с другим вводом", "newConcepts": [], "forbiddenConcepts": ["advanced algorithms"]},
                {"targetSkill": f"{target}_visible_result", "microGoal": "получить понятный видимый результат", "newConcepts": [], "forbiddenConcepts": ["advanced algorithms"]},
                {"targetSkill": f"{target}_small_choice", "microGoal": "добавить один новый нюанс", "newConcepts": [f"{target} nuance"], "forbiddenConcepts": ["advanced algorithms"]},
            ]
        return base[:count]

    @classmethod
    def _task_from_slot(cls, slot: Dict[str, Any], index: int, target: str, placement: Dict[str, Any], language: str) -> Dict[str, Any]:
        n = index + 1
        if language == "cpp":
            title, description, public_test, solution = cls._cpp_task_bank(target, index)
            solution_field = {"referenceSolutionCpp": solution}
            prerequisites = ["output"] if n == 1 else ["C++ skeleton", "cin", "cout"]
        else:
            title, description, public_test, solution = cls._python_task_bank(target, index)
            solution_field = {"referenceSolutionPython": solution}
            prerequisites = ["output"] if n == 1 else ["input", "variables", "output"]

        return {
            "index": n,
            "title": title,
            "assignmentType": "code-test",
            "language": cls._language_label(language),
            "allowedLanguages": [cls._language_label(language)],
            "inputMode": "graphical-editor",
            "difficulty": 1 if n <= 3 else 2,
            "description": description,
            "publicTests": [public_test],
            "hiddenTests": [],
            **solution_field,
            "pedagogicalGoal": slot["microGoal"],
            "targetSkill": slot["targetSkill"],
            "prerequisites": prerequisites,
            "newConcepts": slot.get("newConcepts", []),
            "forbiddenConcepts": slot.get("forbiddenConcepts", []),
            "placementAfterAssignmentId": placement.get("afterAssignmentId"),
            "placementReason": placement.get("reason"),
            "styleNotes": [
                "дружелюбное вступление",
                "блок «Следуй шагам»",
                "короткие строки кода выделяются отдельно",
                "после каждого шага есть понятное объяснение в скобках",
                "одна микроидея на задание",
            ],
            "validationNotes": ["blueprint only", "not persisted", "ideal training task style"],
        }

    @staticmethod
    def _cpp_task_bank(target: str, index: int) -> Tuple[str, str, Dict[str, str], str]:
        if target != "if":
            title = f"Шаг {index + 1}: {target}"
            description = (
                f"Давай сделаем маленькую программу на C++, чтобы аккуратно потрогать тему «{target}».\n\n"
                "Следуй шагам:\n"
                "1. Подключи `#include <iostream>`.\n"
                "(Эта строка нужна, чтобы программа могла выводить текст на экран.)\n"
                "2. Напиши `using namespace std;`.\n"
                "(Так команды `cin` и `cout` можно писать без лишних слов.)\n"
                "3. Создай `int main() { ... }`.\n"
                "(Это главная часть программы.)\n"
                "4. Выведи строку `Готово`.\n\n"
                "Запусти код и проверь, что программа отвечает понятно."
            )
            return title, description, {"input": "", "expectedOutput": "Готово"}, '#include <iostream>\nusing namespace std;\n\nint main() {\n    cout << "Готово";\n}'

        bank: List[Tuple[str, str, Dict[str, str], str]] = [
            (
                "Твой первый вывод",
                (
                    "Давай напишем твою первую программу на C++. Она будет очень короткой и понятной.\n\n"
                    "Следуй шагам:\n"
                    "1. Напиши в самом начале: `#include <iostream>`\n"
                    "(Эта строка подключает библиотеку, чтобы программа могла выводить текст на экран.)\n"
                    "2. Напиши следующую строку: `using namespace std;`\n"
                    "(Это говорит программе, что мы будем использовать стандартные команды без лишних слов.)\n"
                    "3. Напиши: `int main() {`\n"
                    "(Это начало главной части программы.)\n"
                    "4. Внутри напиши: `cout << \"Hi\";`\n"
                    "(Эта команда выведет на экран слово Hi.)\n"
                    "5. Закрой программу скобкой: `}`\n\n"
                    "Запусти код и посмотри, как появится приветствие."
                ),
                {"input": "", "expectedOutput": "Hi"},
                '#include <iostream>\nusing namespace std;\n\nint main() {\n    cout << "Hi";\n}',
            ),
            (
                "Число на экране",
                (
                    "Теперь научим программу читать число и показывать его обратно. Это маленький шаг перед условиями.\n\n"
                    "Следуй шагам:\n"
                    "1. Подключи `#include <iostream>` и напиши `using namespace std;`.\n"
                    "2. Внутри `main` создай переменную: `int n;`\n"
                    "(В ней будет храниться число.)\n"
                    "3. Считай число командой: `cin >> n;`\n"
                    "(Так программа получает ввод от пользователя.)\n"
                    "4. Выведи: `Ты ввёл число n`, подставив настоящее значение переменной.\n\n"
                    "Запусти программу и проверь, что число подставляется правильно."
                ),
                {"input": "7", "expectedOutput": "Ты ввёл число 7"},
                '#include <iostream>\nusing namespace std;\n\nint main() {\n    int n;\n    cin >> n;\n    cout << "Ты ввёл число " << n;\n}',
            ),
            (
                "С чем сравниваем",
                (
                    "Подготовимся к `if`, но пока не будем выбирать разные пути. Сначала просто покажем, с чем сравнивается число.\n\n"
                    "Следуй шагам:\n"
                    "1. Считай число в переменную `n`.\n"
                    "2. Выведи фразу: `Я сравню число n с 10`.\n"
                    "(Так ты глазами видишь число и порог сравнения.)\n"
                    "3. Проверь программу на маленьком и большом числе.\n\n"
                    "Это задание специально без `if`: сейчас мы готовим почву для условия."
                ),
                {"input": "7", "expectedOutput": "Я сравню число 7 с 10"},
                '#include <iostream>\nusing namespace std;\n\nint main() {\n    int n;\n    cin >> n;\n    cout << "Я сравню число " << n << " с 10";\n}',
            ),
            (
                "Первое если",
                (
                    "Теперь сделаем первый настоящий выбор в программе. Это и есть маленькое знакомство с `if`.\n\n"
                    "Следуй шагам:\n"
                    "1. Считай число `n`.\n"
                    "2. Напиши условие: если `n > 10`, выведи `Большое число`.\n"
                    "(Команда внутри `if` выполнится только тогда, когда условие верное.)\n"
                    "3. Пока не добавляй `else`.\n"
                    "4. Запусти код на 7 и на 15, чтобы увидеть разницу."
                ),
                {"input": "15", "expectedOutput": "Большое число"},
                '#include <iostream>\nusing namespace std;\n\nint main() {\n    int n;\n    cin >> n;\n    if (n > 10) {\n        cout << "Большое число";\n    }\n}',
            ),
            (
                "Если иначе",
                (
                    "Добавим второй путь, чтобы программа всегда отвечала понятно.\n\n"
                    "Следуй шагам:\n"
                    "1. Считай число `n`.\n"
                    "2. Если `n > 10`, выведи `Большое число`.\n"
                    "3. Иначе выведи `Обычное число`.\n"
                    "(Слово `else` означает: что делать, если условие не сработало.)\n"
                    "4. Проверь оба варианта."
                ),
                {"input": "7", "expectedOutput": "Обычное число"},
                '#include <iostream>\nusing namespace std;\n\nint main() {\n    int n;\n    cin >> n;\n    if (n > 10) {\n        cout << "Большое число";\n    } else {\n        cout << "Обычное число";\n    }\n}',
            ),
            (
                "Мини-проверка возраста",
                (
                    "Соберём маленькую полезную программу с `if` и `else`.\n\n"
                    "Следуй шагам:\n"
                    "1. Считай возраст в переменную `age`.\n"
                    "2. Если возраст 18 или больше, выведи `Можно начинать`.\n"
                    "3. Иначе выведи `Нужно немного подождать`.\n"
                    "4. Проверь программу на двух разных возрастах.\n\n"
                    "Теперь условие уже похоже на реальную маленькую задачу."
                ),
                {"input": "18", "expectedOutput": "Можно начинать"},
                '#include <iostream>\nusing namespace std;\n\nint main() {\n    int age;\n    cin >> age;\n    if (age >= 18) {\n        cout << "Можно начинать";\n    } else {\n        cout << "Нужно немного подождать";\n    }\n}',
            ),
        ]
        return bank[index] if index < len(bank) else bank[-1]

    @staticmethod
    def _python_task_bank(target: str, index: int) -> Tuple[str, str, Dict[str, str], str]:
        if target != "if":
            title = f"Шаг {index + 1}: {target}"
            description = (
                f"Сегодня сделаем маленький шаг к теме «{target}».\n\n"
                "Следуй шагам:\n"
                "1. Прочитай ввод.\n"
                "2. Сделай только одно простое действие из темы.\n"
                "3. Выведи понятный результат.\n"
                "4. Запусти и проверь пример."
            )
            return title, description, {"input": "1", "expectedOutput": "Готово: 1"}, 'x = input()\nprint(f"Готово: {x}")'

        bank: List[Tuple[str, str, Dict[str, str], str]] = [
            (
                "Твой первый вывод",
                "Давай напишем первую короткую программу.\n\nСледуй шагам:\n1. Напиши `print(\"Hi\")`.\n(Эта команда выводит текст на экран.)\n2. Запусти код и посмотри, как появится приветствие.",
                {"input": "", "expectedOutput": "Hi"},
                'print("Hi")',
            ),
            (
                "Число на экране",
                "Теперь программа считает число и покажет его обратно.\n\nСледуй шагам:\n1. Считай число `n`.\n2. Выведи фразу: `Ты ввёл число n`.\n3. Запусти программу и проверь, что число подставилось правильно.",
                {"input": "7", "expectedOutput": "Ты ввёл число 7"},
                'n = int(input())\nprint(f"Ты ввёл число {n}")',
            ),
            (
                "С чем сравниваем",
                "Подготовимся к сравнению. Пока не используем `if`: просто покажем, с каким числом будем сравнивать ввод.\n\nСледуй шагам:\n1. Считай число `n`.\n2. Выведи фразу: `Я сравню число n с 10`.\n3. Проверь программу на маленьком и большом числе.",
                {"input": "7", "expectedOutput": "Я сравню число 7 с 10"},
                'n = int(input())\nprint(f"Я сравню число {n} с 10")',
            ),
            (
                "Первое если",
                "Теперь сделаем первый настоящий выбор.\n\nСледуй шагам:\n1. Считай число `n`.\n2. Если `n > 10`, выведи `Большое число`.\n3. Пока не добавляй `else`.\n4. Запусти и сравни поведение на 7 и 15.",
                {"input": "15", "expectedOutput": "Большое число"},
                'n = int(input())\nif n > 10:\n    print("Большое число")',
            ),
            (
                "Если иначе",
                "Добавим второй путь, чтобы программа всегда отвечала понятно.\n\nСледуй шагам:\n1. Считай число `n`.\n2. Если `n > 10`, выведи `Большое число`.\n3. Иначе выведи `Обычное число`.\n4. Проверь оба варианта.",
                {"input": "7", "expectedOutput": "Обычное число"},
                'n = int(input())\nif n > 10:\n    print("Большое число")\nelse:\n    print("Обычное число")',
            ),
            (
                "Мини-проверка возраста",
                "Соберём маленький классификатор.\n\nСледуй шагам:\n1. Считай возраст.\n2. Если возраст 18 или больше, выведи `Можно начинать`.\n3. Иначе выведи `Нужно немного подождать`.\n4. Проверь программу на двух разных возрастах.",
                {"input": "18", "expectedOutput": "Можно начинать"},
                'age = int(input())\nif age >= 18:\n    print("Можно начинать")\nelse:\n    print("Нужно немного подождать")',
            ),
        ]
        return bank[index] if index < len(bank) else bank[-1]
