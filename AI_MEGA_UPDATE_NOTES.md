# AI mega update

Этот архив закрывает проблемы, которые проявились в логах AI-чата:

- AI делал выводы по `focusAssignments`/первому срезу курса и не доходил до поздних тем (`loops`, `arrays`).
- Параллельное создание AI-черновиков могло показывать задания в порядке завершения worker'ов, а не в исходном `taskIndex`.
- Сценарий `polish_assignment_draft` был заточен под `code-test`: требовал code runner, тест-кейсы и эталонный код даже для тестовых/математических задач.
- Контекст `test`/`math` задач почти не попадал в course outline, поэтому AI плохо понимал не-code задания.

## Что изменено

### Контекст курса

- Worker теперь сначала извлекает полный `courseOutline`/`courseMap`/`courseDigest`, а не `focusAssignments`.
- `build_ai_context` добавляет компактный `courseMap` в начало JSON-контекста, чтобы поздние задания не отрезались при `compact_json`.
- Увеличены лимиты контекста до 78k символов в основных LLM-сценариях.
- Backend добавляет в outline `contentSummary` для `test` и `math` заданий: количество вопросов/блоков и короткие preview.
- Детектор концептов и scoring теперь учитывают `loops`, `arrays`, вопросы тестов и math-блоки.

### Типы заданий

Поддерживаются AI-черновики для:

- `code-test` — как раньше: публичные/скрытые тесты, эталонное решение, runner validation.
- `test` — `testSpec.settings` + `testSpec.questions`, структурная проверка, сохранение в `TaskTestSettings`/`TaskTestQuestions`.
- `math` — `mathSpec.settings` + `mathSpec.blocks`, структурная проверка, сохранение в `TaskMathSettings`/`TaskMathBlocks`.

`image-test` намеренно не создаётся этим апдейтом.

### Порядок и параллельное создание

- `sourceTaskIndex`, `beforeAssignmentId`, `afterAssignmentId` сохраняются в AI-артефакте и hidden draft.
- Backend сортирует AI-черновики внутри placement-сегмента по `SourceAgentTaskIndex`, поэтому параллельные worker'ы больше не должны ломать порядок `19.1 -> 19.8`.
- Сохранена идемпотентность hidden draft по `SourceAgentRunId + SourceAgentTaskIndex`.

### Валидаторы

- Worker-валидатор больше не отклоняет `test`/`math` из-за отсутствия C++ reference solution.
- `polish_assignment_draft`, `guided_ladder`, `bridge_tasks`, `style_matched_tasks`, `draft_revision` обновлены под `code-test|test|math`.

## Проверки в контейнере

- `python3 -m py_compile` для всех файлов `taskforge-ai-worker-external/**/*.py` — успешно.
- Простая sanity-проверка баланса фигурных скобок в `InternalAgentController.cs` — успешно.

`dotnet build` в контейнере не запускался: SDK `dotnet` в окружении отсутствует.
