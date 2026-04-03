# TaskForge AI Foundry — Wave 16: Quality Ledger + Export Manifest

## Что добавлено

- Добавлен `AiBatch.QualityLedgerJson`.
- Добавлен `AiBatch.ExportManifestJson`.
- После `assignment_batch_publish_prepare` backend теперь строит:
  - финальный quality ledger по всем item'ам batch;
  - export manifest с операцией `publish-full`, `publish-partial` или `hold`.

## Что делает quality ledger

Quality ledger агрегирует:
- item scorecards;
- primary repair routes;
- brief/context findings;
- anti-pattern flags;
- readiness band по каждому item.

Это backend-ready сводка качества перед публикацией.

## Что делает export manifest

Export manifest собирает:
- итоговую operation-модель публикации;
- export order по slot index;
- ready/blocked состояние draft'ов;
- связку с publish pack и quality ledger.

Это финальный слой перед UI/внешней публикацией draft pack.

## Зачем это нужно

Это закрывает последний крупный backend-зазор между:
- publication audit как verdict,
- publish pack как набор draft'ов,
- и реальным backend-ready экспортным слоем.

## Миграции

Миграции не добавлялись. Изменены только entities / DTO / services / orchestration.
