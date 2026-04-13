# AI chat update: blueprint UX, stale-memory cleanup, dead-chat delete

## Что исправлено

### 1) Chat-first blueprint UX
- `save_chat_blueprint` теперь показывает не только summary, но и реальный preview черновика:
  - title
  - goal
  - full/preview condition
  - mustKeep / avoid
  - public tests
- worker при fallback строит более конкретный blueprint из пользовательской инструкции и teaching-script, а не сохраняет только абстрактную выжимку.

### 2) Мостики больше не лезут в память поверх blueprint-flow
- Когда в памяти уже есть `currentDraftBlueprint`, chat memory:
  - не подмешивает старый `nextAgentStep` про мостики
  - не тащит `LastBridgePlan` в summary/facts
  - переключает `agentState.workflowKind` на `chat-blueprint`
- Из-за этого после обсуждения условий AI меньше заражается старым audit/bridge контекстом.

### 3) Удаление мёртвого чата
- Удаление AI-чата теперь:
  - отменяет незавершённые chat jobs
  - отвязывает связанные `AiBatches.ChatSessionId`
  - удаляет саму сессию
- Во фронте можно удалить чат прямо из списка.
- Если чат не открывается, UI всё равно даёт выбрать его и удалить.
- Кнопка удаления больше не блокируется только из-за `pending`.

## Изменённые файлы
- `taskforge/Services/AI/AiChatService.cs`
- `taskforge-ai-worker-external/worker.py`
- `taskforge-ai-worker-external/prompt_builder.py`
- `clientapp/src/pages/admin/AdminAiChatPage.jsx`

## Что проверено
- `python -m py_compile` для worker/prompt
- `python -m unittest tests.test_chat_memory_routing tests.test_chat_strict_mode tests.test_instruction_strictness`

## Что не проверено здесь
- `dotnet build` / Docker build
- полный frontend build


## Update: no-input sentinel + blueprint fidelity
- Для code-test пустой input больше не путешествует по пайплайну как пустая строка: он нормализуется в текстовый sentinel `пусто`.
- Публикация AI draft и обычное создание/обновление тест-кейсов теперь тоже нормализуют пустой input в `пусто`, чтобы не было расхождения между worker/self-check и проектной валидацией.
- finalize_chat_blueprint теперь прокидывает approved blueprint как structured context в generation job.
- Генератор усилил blueprint-fidelity: согласованный title/condition из чата считаются каноном, а для простых intro-output задач фиксированный литерал вывода и reference solution больше не должны уезжать в соседнюю тему вроде `System online`.
- В prompt builder добавлены явные правила: если задача без ввода, использовать sentinel `пусто`, а не пустой input.
