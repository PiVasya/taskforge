# AI Foundry Wave 8

Что добавлено без миграций:
- `assignment_brief_review`
- `assignment_brief_repair`
- `assignment_batch_context_review`
- `BriefReviewJson` и `ContextReviewJson` в `AiBatchItem`
- batch-context payload с sibling draft summaries
- brief pipeline: brief generate -> brief review -> brief repair (при необходимости) -> draft generate
- draft review chain расширен batch-context review
- decision logs продолжают писаться на новых стадиях

Что НЕ делалось:
- миграции
- фронтенд
