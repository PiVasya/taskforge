# AI Foundry Quality Wave 1

Эта волна трогает только AI backend + worker.

Что добавлено:
- stage-поля в `AiJob` (`ParentJobId`, `StageCode`, `StageLabel`, `StageOrder`)
- таблица `AiArtifacts` для хранения результатов review-стадий
- автоматическая постановка двух review job после любой AI-генерации draft:
  - `assignment_structural_review`
  - `assignment_pedagogy_review`
- worker умеет выполнять structural/pedagogy review
- результаты review встраиваются в `draft.meta.aiReviews`
- если review падает, draft уходит в `needs-review` или `needs-fix`

Что пока НЕ сделано:
- batch generation
- batch items
- repair loop
- mutation testing
- frontend timeline

Это кусочек Foundry-плана: quality wave поверх уже существующей генерации.
