from __future__ import annotations

from typing import Any, Dict, List

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot

BASE_SYSTEM = """
Ты AI-агент TaskForge. Отвечай только валидным JSON-объектом без markdown.
Не выдумывай, что видел курс, если в контексте нет данных. Но если selectedCourseId/selectedCourseTitle/selectedCourse заполнены, этот курс точно найден и его нельзя объявлять отсутствующим. Если context содержит matchedCourses, сначала используй самый релевантный matchedCourses[0]. Если context содержит courseCatalog/courseContexts, используй их даже если selectedCourseId=null.
Нельзя использовать шаблонные заглушки. Любой результат должен быть сгенерирован по реальному контексту и пользовательскому запросу.
Если данных недостаточно, честно укажи это в warnings и предложи, какие данные нужны. Если в context есть courseMap/courseOutline/courseDigest с conceptHints, используй их как главный источник порядка заданий; focusAssignments — только фокусный срез. Не говори, что тема отсутствует, пока не проверил весь courseMap/courseOutline, а не только первые задания.
Сохранение в курс запрещено для генерации черновиков: возвращай только blueprint/draft/report. Исключение — сценарий course_edit: он возвращает assignment_update_batch, который backend применяет к существующим заданиям.
Для обучающих задач в стиле TaskForge не сокращай описание до одной фразы: пиши полноценные пошаговые инструкции как в начальных задачах курса.
""".strip()


def context_user_block(context: AgentContextSnapshot, task: str, schema: Dict[str, Any], max_chars: int = 78000) -> str:
    return (
        f"Задача сценария:\n{task.strip()}\n\n"
        "Контекст TaskForge JSON:\n"
        f"{build_ai_context(context, max_chars=max_chars)}\n\n"
        "Верни JSON строго по смыслу этой схемы. Схема не является данными для копирования:\n"
        f"{schema}\n"
    )


def as_str(value: Any, default: str = "") -> str:
    text = str(value).strip() if value is not None else ""
    return text or default


def as_list(value: Any) -> List[Any]:
    return value if isinstance(value, list) else []


def as_dict(value: Any) -> Dict[str, Any]:
    return value if isinstance(value, dict) else {}



TASKFORGE_TRAINING_TASK_STYLE = """
Стиль обучающих задач TaskForge, который нужно считать эталонным, если пользователь просит «как Задание 1/1.1», «лесенка лесенка», «пошагово», «каждым шагом пишется что надо написать в код».

Обязательные правила:
1. Язык сохраняй из курса/предыдущего черновика. Для code-test используй runner-id языка и соответствующее поле эталонного решения: C++ -> language="cpp" + referenceSolutionCpp, C# -> language="csharp" + referenceSolutionCsharp, Python -> referenceSolutionPython, JavaScript -> referenceSolutionJavascript, Pascal -> referenceSolutionPascal, Java -> referenceSolutionJava. Для test/math кодовое решение не требуется, но стиль и терминологию курса сохраняй. Не переключай язык курса.
2. Описание задачи должно быть не кратким условием, а полноценной обучалкой: короткое дружелюбное вступление + «Следуй шагам:» + нумерованные шаги, где прямо написано, какие строки/конструкции нужно набрать. Обязательно ставь переводы строк между вступлением, «Следуй шагам:», каждым шагом и финальной фразой.
3. В шагах можно использовать inline code-фрагменты на языке курса: например #include <iostream> для C++, Console.WriteLine(...) для C#, print(...) для Python, console.log(...) для JavaScript, begin/end для Pascal, System.out.println(...) для Java. Если фрагмент длиннее одной строки, используй fenced code block с языком.
4. После каждого важного шага добавляй пояснение в скобках простым языком, как в исходных задачах: «(эта строка подключает библиотеку...)».
5. Новые конструкции вводи строго по одной: сначала сравнение без if, потом простой if, потом if/else, потом else if, потом &&/||.
6. Публичный тест должен быть понятным и минимальным. hiddenTests допустимы, но не должны заменять обучающий текст.
7. Не используй англоязычные output-слова без причины, если курс использует русские пояснения. Но expectedOutput должен точно совпадать с условием.
8. Не называй задачи абстрактно «Первая проверка». Заголовки должны быть похожи на курс: «Задание 19.1. Проверяем больше ли число нуля», «Задание 19.2. Первое условие if».
9. Если пользователь просит задачи перед конкретным заданием, сохраняй placement before/after из прошлого анализа или gap_report.
10. Каждая задача должна быть пригодна как отдельное задание в системе: description, publicTests, allowedLanguages, inputMode, difficulty, эталонное решение.
""".strip()
