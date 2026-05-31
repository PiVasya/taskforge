# TaskForge .NET AI Agent Runtime v2

Новая AI-папка заменяет deprecated `taskforge-ai-worker-external` не как буквальный порт Python-сценариев, а как нормальный .NET agent runtime:

- Microsoft Agent Framework `AIAgent` / `ChatClientAgent` поверх `IChatClient`.
- OpenRouter оставлен как OpenAI-compatible провайдер.
- Доменные возможности вынесены в C# function tools.
- Сложные операции идут через workflows: контекст -> план -> draft/audit/edit -> validation -> critique -> repair -> approval artifact.
- Реальные write-действия не выполняются напрямую из worker. Backend остаётся владельцем БД и применяет artifacts/approval.

## Запуск

```bash
export TaskForgeAgent__ApiKey="OPENROUTER_KEY"
export TaskForgeAgent__Model="openai/gpt-4o-mini"
export TaskForgeInternalApi__BaseUrl="http://ai-api:8080"
export TaskForgeInternalApi__ApiKey="INTERNAL_AGENT_KEY"
dotnet run --project taskforge-ai-agent-dotnet/TaskForge.AiAgent.csproj
```

## Почему это не Python scenario-engine

Старый Python worker выбирал `scenario_id` через rule-router, затем просил LLM вернуть JSON. Новый runtime делает иначе:

1. Backend выдаёт run через существующий `/api/internal/agent/claim-next`.
2. .NET worker выбирает безопасный workflow.
3. Workflow запускает агента с tools и контекстом.
4. Для draft-flow включены validation, test run, critic loop и bounded retry.
5. Для write-действий формируются `approval_request` и/или artifacts, а не прямые записи в БД.
6. Backend получает тот же envelope (`assistantMessage`, `scenarioId`, `artifacts`, `memoryPatch`) и может использовать текущий UI.

## Основные workflow

| Workflow | Когда используется | Что делает |
|---|---|---|
| `open_chat` | обычный диалог | агент отвечает и вызывает read-only tools при необходимости |
| `course_audit` | анализ курса, пробелы, gap audit | делает план, аудит, artifact `course_gap_audit` |
| `assignment_draft_workflow` | создать/сгенерировать задание | draft -> validation -> tests -> critic -> repair -> approval artifact |
| `polish_assignment_draft` | вылизать выбранное AI-задание | сохраняет selectedTask, позицию before/after, прогоняет validation/critic и готовит `polished_assignment_draft` |
| `course_edit_workflow` | изменить курс/задания | готовит `course_edit_proposal` + `approval_request`, без авто-применения |

## Важные ограничения

- В этом репозитории worker не пишет напрямую в БД. Это сделано специально.
- OpenRouter должен использовать модель с нормальной поддержкой tools/function calling.
- Для production желательно прогнать `dotnet build`, unit/integration tests и проверить реальные OpenRouter tool calls.
