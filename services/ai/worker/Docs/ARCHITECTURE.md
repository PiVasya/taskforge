# AI worker architecture

Текущая AI-часть состоит из двух уровней.

## 1. Управляющий уровень

`AdaptiveAgentLoopWorkflow` управляет run-ом. Он не генерирует задания напрямую, а выбирает безопасный маршрут.

Простой разговор может пройти так:

```text
inspect_context -> classify_request -> answer_directly -> finish
```

Работа с курсом обычно проходит богаче:

```text
inspect_context
-> classify_request
-> map_course_structure
-> extract_course_style
-> find_learning_gaps
-> plan_course_enrichment
-> delegate_assignment_draft / delegate_course_audit / delegate_course_edit
-> review_delegated_result
-> finish
```

Массовый анализ/переразметка курса использует отдельный безопасный путь:

```text
inspect_context
-> classify_request
-> load_editable_assignments
-> map_course_structure
-> analyze_assignment_complexity
-> propose_assignment_patch_set
-> review_patch_set
-> finish
```

`finish` не принимается, пока ожидающий delegated result или patch set не прошёл соответствующий review.

Модель выбирает следующий шаг, но backend проверяет действие, сохраняет `AgentLoopState` и не даёт потерять накопленную память.

## 2. Рабочие workflow

Специализированные workflow делают конкретную работу:

- `AssignmentDraftWorkflow` — создаёт задания и прогоняет validation/critic/repair;
- `CourseAuditWorkflow` — анализирует курс и ищет пробелы;
- `CourseEditWorkflow` — готовит patch без авто-применения;
- `PolishAssignmentDraftWorkflow` — дорабатывает выбранное задание;
- `OpenChatWorkflow` — отвечает в чат без записи в курс.

## 3. Course-aware инструменты

Перед генерацией заданий agent loop может подготовить:

- `courseMap` — карта курса, типов заданий, языков, сложности и timeline понятий;
- `courseStyleProfile` — стиль существующих заданий, тестов, описаний и названий;
- `courseGapReport` — список слабых мест и предложений для bridge tasks;
- `courseEnrichmentBrief` — единый brief, который передаётся в downstream workflow;
- `editableAssignments` — нормализованный набор заданий для batch-анализа;
- `assignmentComplexityReport` — объяснимая оценка сложности перед mass rerating;
- `pendingPatchSet` / reviewed patch state — безопасный diff до применения.

Это помогает модели не генерировать из воздуха и не забывать, что она уже увидела в курсе.

## Принцип качества

AI может предложить результат, но результат не считается готовым без проверок. Для заданий это значит:

```text
черновик -> проверка структуры -> проверка тестов -> критика -> исправление -> скрытый материал -> review_delegated_result
```

Система сохраняет только те черновики, которые прошли проверки.
