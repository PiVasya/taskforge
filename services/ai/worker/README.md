# TaskForge AI Worker

Актуальный AI-worker для TaskForge. Он работает не как один прямой prompt, а как управляемый agent loop:

1. модель выбирает следующий шаг из безопасного списка действий;
2. backend выполняет выбранный шаг;
3. результат шага сохраняется в общий `AgentLoopState`;
4. следующий шаг получает уже накопленную память run-а;
5. итоговые задания проходят validation/critic/repair перед сохранением как скрытые черновики.

## Зачем так сделано

Главная цель — качество заданий и непрерывная память внутри AI-run. Модель не должна забывать, что увидела в курсе, какие ограничения дал пользователь, какие ошибки нашёл валидатор и что уже было сделано на прошлых шагах.

Поэтому рабочая память хранится не только в prompt-е модели, а в структуре `AgentLoopState` и прокидывается в специализированные workflow через payload/memory.

## Запуск

```bash
export TaskForgeAgent__ApiKey="OPENROUTER_KEY"
export TaskForgeAgent__Model="<openrouter-qwen-model-id>" # например Qwen/OpenRouter; точный id модели задаётся в .env
export TaskForgeAgent__OpenAiCompatibleBaseUrl="https://openrouter.ai/api/v1"
export TaskForgeAgent__EnableAdaptiveAgentLoop="true"
export TaskForgeAgent__MaxAgentLoopSteps="12"
export TaskForgeAgent__MaxAgentStateCharacters="64000"
export TaskForgeInternalApi__BaseUrl="http://ai-api:8080"
export TaskForgeInternalApi__ApiKey="INTERNAL_AGENT_KEY"

dotnet run --project services/ai/worker/TaskForge.AiAgent.csproj
```

## Основная архитектура

| Компонент | Назначение |
|---|---|
| `AdaptiveAgentLoopWorkflow` | общий цикл: решение модели -> действие -> наблюдение -> новое решение |
| `AgentLoopDecisionClient` | просит модель выбрать один следующий шаг в строгом JSON |
| `AgentLoopState` | память run-а: запрос, контекст, intent, шаги, наблюдения, материалы |
| `AgentIntentClassifier` | быстрый детерминированный классификатор запроса и fallback, если модель ошиблась |
| `AssignmentDraftWorkflow` | генерация заданий с планом, картой навыков, валидацией, критикой и repair |
| `CourseAuditWorkflow` | анализ курса, поиск пробелов и скачков сложности |
| `CourseEditWorkflow` | подготовка безопасного пакета правок без авто-применения |
| `PolishAssignmentDraftWorkflow` | доработка выбранного задания до скрытого черновика |
| `OpenChatWorkflow` | обычный ответ ассистента, когда не нужна генерация/аудит/правки |

## Разрешённые действия agent loop

Модель может выбрать только действия из текущего allowlist (одиночно или безопасным `actions[]` batch):

- `inspect_context` — сохранить доступный контекст чата, курса, заданий и вложений в общую память;
- `classify_request` — определить intent, ограничения и нужный сценарий;
- `load_editable_assignments` — загрузить и нормализовать все задания, доступные для массового анализа/правок;
- `map_course_structure` — построить карту курса: порядок, типы, языки, сложность, rating и timeline понятий;
- `extract_course_style` — извлечь стиль существующих заданий: названия, описания, тесты, теги, язык;
- `find_learning_gaps` — найти пробелы, скачки сложности, слабые тесты и места для bridge tasks;
- `analyze_assignment_complexity` — оценить сложность каждого задания перед массовой переразметкой difficulty/rating;
- `plan_course_enrichment` — собрать единый brief для генерации/правок курса;
- `search_course` — найти релевантные задания в уже загруженном контексте курса;
- `propose_assignment_patch_set` — подготовить ограниченный patch set с diff и причинами;
- `review_patch_set` — проверить patch set до завершения run;
- `review_delegated_result` — проверить результат рабочего workflow до завершения;
- `delegate_assignment_draft` — перейти в генератор заданий;
- `delegate_course_audit` — перейти в анализ курса;
- `delegate_course_edit` — подготовить правки курса;
- `delegate_polish_assignment` — доработать выбранное задание;
- `answer_directly` — ответить обычным сообщением;
- `finish` — завершить run только после необходимых review/gates.

Модель выбирает маршрут, но код держит ограничения: лимит шагов, обязательный контекст/классификацию, запрет непроверенного `finish`, review делегированных результатов и patch set, а также финальные проверки качества.

## Качество заданий

Генерация заданий не сохраняет первый попавшийся ответ модели. Для draft-flow используется цепочка:

```text
контекст -> план -> карта навыков -> черновик -> validation -> critic -> repair -> скрытый draft artifact
```

Если черновик не проходит проверки, он не сохраняется как задание. Лучше вернуть понятную ошибку, чем положить в курс мусор.

## Логи и экспорт

Каждый шаг сохраняется как `AiStep`. На фронте журнал AI должен показывать:

- выбранное действие;
- краткую причину выбора;
- входные аргументы;
- наблюдение после выполнения;
- состояние run-а после шага;
- материалы, созданные ассистентом.

Это нужно, чтобы быстро скинуть полный AI-отчёт в чат и понять, где агент потерял качество.

## Важные ограничения

- Worker не пишет напрямую в основную БД курсов.
- Все опасные изменения идут через artifacts/approval.
- Модель OpenRouter можно менять без изменения кода через `TaskForgeAgent__Model`.
- Слабые/дешёвые модели могут хуже выбирать маршрут, поэтому fallback и валидаторы обязательны.

## Batch agent actions and patch sets

AI worker supports adaptive batch decisions: a model can return several safe actions in `actions[]`, and the backend executes them sequentially with persistent logs. Course-wide edits are produced as `course_patch_set` artifacts, not silent writes. The frontend renders them in a dedicated patch menu with GitHub-like diffs and dry-run/apply controls.
