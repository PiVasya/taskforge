# Database replication and split plan

## Сейчас

Если вся БД лежит только в РБ, то падение РБ убивает данные для всех регионов.

## Что нужно перед multi-region

1. PostgreSQL streaming replication или managed PostgreSQL HA.
2. Primary лучше держать не на edge-сервере, а в самом стабильном регионе, например Poland.
3. Israel/RB получают standby replicas.
4. API/worker stateless и переключаются через gateway/DNS/LB.

## Разрезание монолитной БД

Сначала фиксируем владельцев таблиц в `tools/database-splitter/table-ownership-map.json`.

Потом переносим таблицы в отдельные БД сервисов:

- `taskforge_identity`
- `taskforge_education`
- `taskforge_tasks`
- `taskforge_solutions`
- `taskforge_execution`
- `taskforge_ai`
- `taskforge_support`
- `taskforge_minecraft`
- `taskforge_files`
- `taskforge_notifications`
- `taskforge_observability`

Каждый перенос должен быть идемпотентным и журналируемым.
