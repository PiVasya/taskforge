# TaskForge AI Foundry — Wave 15

## Что добавлено

### 1. Historical slot priors
- Добавлен `AiBatchItem.HistoricalSlotPriorsJson`.
- Для каждого batch item теперь собирается history-aware snapshot по похожим item из прошлых batch того же курса и типа задания.
- В slot priors входят:
  - strongExamples
  - weakExamples
  - routeHints
  - recommendedDifficultyBand
- Эти данные теперь подмешиваются в:
  - brief generate
  - brief review
  - brief repair
  - reference pack build
  - draft generation additional payload

### 2. Publish pack
- Добавлен `AiBatch.PublishPackJson`.
- После `assignment_batch_publish_prepare` backend собирает backend-ready publish pack:
  - readyDrafts
  - blockedDrafts
  - publishableCount
  - blockedCount
  - shouldPublish
- Это уже заготовка под будущий Draft Pack / publication UI.

### 3. Stronger memory usage
- Память теперь не только batch-level (`HistoricalPlannerPriorsJson`), но и item-level.
- Это усиливает reuse сильных/слабых паттернов именно для конкретного `targetSkill`.

## Что это даёт
- planning/replan остаётся course-aware;
- brief/reference/draft stages становятся ещё и slot-aware;
- публикация получает не только audit, но и готовый draft pack для backend-решения.

## Миграции
Не добавлялись.
Изменялись только entities / dto / services / worker / backend orchestration.
