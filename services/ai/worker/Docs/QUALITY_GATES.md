# AI quality gates

Перед production-проверкой AI-части нужно прогнать:

```bash
dotnet restore services/ai/worker/TaskForge.AiAgent.csproj
dotnet build services/ai/worker/TaskForge.AiAgent.csproj -c Release
dotnet test services/ai/worker/tests/TaskForge.AiAgent.Tests/TaskForge.AiAgent.Tests.csproj -c Release
```

## Проверки поведения

1. Обычный чат не должен создавать artifacts.
2. Запрос на генерацию задач должен идти через `inspect_context`, `classify_request`, `delegate_assignment_draft`, `finish`.
3. Задания с вводом должны получать достаточное количество тестов и hidden tests.
4. `starterCode` не должен содержать готовое решение.
5. Если валидатор отклонил черновик, он не должен сохраняться как скрытое задание.
6. Правки курса должны возвращаться как `course_edit_proposal` и требовать подтверждения.
7. Журнал AI должен содержать решения модели, наблюдения и итоговый state.

## Smoke prompts

```text
Проанализируй курс и найди пробелы.
```

Ожидается материал `course_gap_audit`.

```text
Создай 5 обучающих code-test задач по вводу и выводу C++ в стиле курса.
```

Ожидаются скрытые draft artifacts и журнал с validation/critic/repair шагами.
