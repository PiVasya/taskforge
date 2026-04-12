# TaskForge External Worker Upgrade Status

## Уже было в проекте до наших крупных апдейтов
- Отдельный готовый внешний воркер `taskforge-ai-worker-external`.
- Stage-aware orchestration в `worker.py`.
- Compact payload и `anchorContext` в `payload.py`.
- `_generate_draft_via_substages(...)` уже существовал, но не был основным путём.
- Deterministic reviews/self-check для draft/runtime/test-strength/similarity.

## Что было добавлено в прошлых крупных волнах
- Feature flag `TASKFORGE_AI_DRAFT_SUBSTAGES` и включение substage-пайплайна по флагу/эвристике.
- Route-aware repair: prompt directive + deterministic fallback adjustments по `repairPlan.primaryRoute`.
- Sandbox-aware process runner с лимитами timeout/CPU/memory и richer runtime diagnostics.
- Validator `planValidation` для batch plan (`count`, `unique targetSkill`, `placement ids`, generic slots, avoid conflicts).
- Единый `schemas.py`: registry схем по ключевым стадиям и worker-side validation.
- OpenRouter-native headers/provider/plugins и Structured Outputs через `json_schema` с fallback вниз.
- Chat strict mode: allowlist action names, whitelist id и запрет destructive actions без `confirmed=true`.
- `selectionTelemetry` в compact payload.

## Что сделано в этой волне (wave4)
- Добавлен **containerized sandbox runner** без сети: внешний воркер теперь умеет запускать code runtime не только process-mode, но и через `docker run --network none --read-only ...`.
- Добавлен отдельный `runner.Dockerfile` для sandbox image `taskforge-python-runner:wave4`.
- Добавлен **stage-aware provider routing**: разные provider order для planning/chat/draft/repair/review + env overrides через `TASKFORGE_AI_STAGE_ROUTING_JSON`.
- Добавлен **signature-based duplicate detection**: новый модуль `similarity_signatures.py`, расширенный `run_similarity_review(...)`, `duplicateSignatureHints` в `anchorContext`.
- В prompt layer теперь учитываются не только `possibleDuplicates`, но и `duplicateSignatureHints`.
- В лог стартового LLM-вызова добавлены `routing` и `plugins`, чтобы видеть реальное поведение stage profiles.

## Что уже было в проекте ещё до этой волны и не надо дублировать в следующих шагах
- Базовый route-aware repair уже есть.
- Базовый sandbox-aware runner уже есть.
- Substage draft pipeline уже есть и умеет включаться флагом.
- OpenRouter structured outputs и schema registry уже есть.
- Chat strict mode и selection telemetry уже есть.

## Что ещё осталось на следующие крупные волны
- Полностью автономный sandbox-контур уровня отдельного runner service / orchestration, если захочешь уйти дальше текущего `docker run`-подхода.
- Более сильный anti-duplicate слой поверх signature hints: clustering / threshold tuning / batch peer draft comparison.
- Stage-aware cost and usage telemetry из ответов OpenRouter.
- Постепенная де-ollama-ция имён модулей/env без ломки совместимости.
- Backend-side агрегация selection telemetry и duplicate metrics в scorecards/artifacts.

## Что сделано в этой волне (wave5)
- Канонический импорт внешнего LLM-адаптера перенесён в `llm_client.py`; `ollama.py` оставлен как compatibility shim.
- Добавлена usage/cost telemetry из OpenAI-compatible/OpenRouter ответов: prompt/completion/total tokens и cost попадают в `llm-request-success` и во внутренний `__llmMeta`.
- Добавлен новый слой `duplicate_clusters.py`: сигнатурная кластеризация похожих referenceAssignments и peer drafts.
- `anchorContext` теперь включает `duplicateClustersPreview`, а similarity / batch-context reviews учитывают кластеры, а не только попарную похожесть.
- Добавлен workflow `.github/workflows/test-external-ai-worker.yml` с `compileall + pytest` для worker-only изменений.

## Что уже не нужно повторять в следующих шагах
- De-ollama путь уже начат: новый код должен импортировать `llm_client`, а не `ollama`, если нет строгой причины сохранять legacy-имя.
- Usage/cost telemetry уже снимается из ответов LLM-провайдера и доступна в логах.
- Duplicate analysis уже умеет не только hints, но и кластерный взгляд по reference/peer drafts.

## Что ещё осталось после wave5
- Backend-side агрегация usage/cost telemetry и duplicate metrics в scorecards/artifacts.
- Тонкая настройка порогов duplicate clustering по реальным курсам и batch peer drafts.
- Дальнейшая зачистка legacy env-names с безопасной обратной совместимостью.

## Wave6 — worker telemetry → backend artifacts + scorecard enrichment

### Что было уже в проекте
- На стороне backend уже существовали `AiArtifact`, `AiDecisionLog`, `AiReferenceSnapshot`, scorecard-агрегация и foundry sync.
- Во внешнем воркере уже были `selectionTelemetry`, `duplicateClustersPreview`, `__llmMeta`, stage-aware routing и усиленный duplicate layer.

### Что добавлено в этой волне
- Внешний воркер теперь собирает `__workerTelemetry` и отправляет его в backend отдельным `telemetryJson`, а не прячет только в локальных логах.
- Backend сохраняет `worker-telemetry`, `selection-telemetry` и `duplicate-clusters` как `AiArtifact` без новой таблицы.
- Foundry scorecard теперь обогащается `llmTelemetry`: providers, models, tokens, estimatedCost, selection anchors и largest duplicate cluster.
- Добавлены конфигурируемые пороги duplicate cluster warning/fail и summary в similarity review.
- Legacy-путь `call_ollama` ещё совместим, но внешний orchestration-код продолжает уходить на `llm_client` и worker telemetry, а не на ollama-first semantics.

### Что остаётся дальше
- Вынести telemetry summary выше по стеку: batch quality ledger / admin DTO / decision summaries.
- Подобрать реальные course-specific duplicate thresholds на живых данных, а не только через env.
- Продолжить зачистку legacy env/name хвоста в документации и compose.
