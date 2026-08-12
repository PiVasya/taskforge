# AI quality gates

Перед production-проверкой AI-части нужно прогнать:

```bash
dotnet restore services/ai/worker/TaskForge.AiAgent.csproj
dotnet build services/ai/worker/TaskForge.AiAgent.csproj -c Release
dotnet test services/ai/worker/tests/TaskForge.AiAgent.Tests/TaskForge.AiAgent.Tests.csproj -c Release
```

## Проверки поведения

1. Обычный чат не должен создавать artifacts.
2. Запрос на генерацию задач должен идти через `inspect_context`, `classify_request`, `delegate_assignment_draft`, `review_delegated_result`, `finish`.
3. Задания с вводом должны получать достаточное количество тестов и hidden tests.
4. `starterCode` не должен содержать готовое решение; внутренний `referenceSolution` никогда не должен автоматически становиться `starterCode`.
5. Если валидатор отклонил черновик, он не должен сохраняться как скрытое задание.
6. Правки курса должны возвращаться как `course_edit_proposal` и требовать подтверждения.
7. Журнал AI должен содержать решения модели, наблюдения и итоговый state.
8. Массовая переразметка difficulty/rating должна проходить `load_editable_assignments -> map_course_structure -> analyze_assignment_complexity -> propose_assignment_patch_set -> review_patch_set` и не применяться молча.

## Smoke prompts

```text
Проанализируй курс и найди пробелы.
```

Ожидается материал `course_gap_audit`.

```text
Создай 5 обучающих code-test задач по вводу и выводу C++ в стиле курса.
```

Ожидаются скрытые draft artifacts и журнал с validation/critic/repair шагами.
