# Rating worker

Worker поддерживает read-model рейтинга `UserRatings`.

Схема теперь гибридная:

1. `solutions-api` и `tasks-api` при изменениях, влияющих на рейтинг, помечают пользователя в `RatingDirtyUsers`.
2. `rating-worker` раз в `Rating:DirtyIntervalMinutes` минут берёт пачку dirty-пользователей и полностью пересчитывает рейтинг только для них.
3. Раз в `Rating:FullRebuildIntervalHours` часов запускается полный пересчёт всех пользователей как страховка от пропущенных событий, удаления старых заданий/решений и ручных правок.

Важно: worker не прибавляет/вычитает очки дельтой. Для выбранного пользователя он строит рейтинг заново из текущего состояния:

- `SolutionSubmissions` из `solutions-api`;
- `UserImageTaskSolutions` из `solutions-api`;
- passed test/math attempts из `tasks-api`;
- актуальный `assignment.rating` из `tasks-api`.

Если задание удалено из `tasks-api`, его metadata больше не возвращается, и такое задание перестаёт давать очки при ближайшем пересчёте пользователя или при суточном full rebuild.

Миграции принадлежат `../api`; worker не владеет схемой БД.


## Startup/recovery

`tasks-api` является зависимостью read-model пересчёта. Запросы activity/assignment summaries используют ограниченный retry с backoff для connection failures, HTTP 408/429 и 5xx; постоянные 4xx не повторяются. После восстановления worker пишет отдельную запись об успешном recovery и продолжает тот же пересчёт, а не ждёт следующего периодического цикла. Runtime-образ содержит `libgssapi-krb5-2`, необходимую Npgsql на Debian runtime при загрузке Kerberos/GSSAPI support.

## PostgreSQL leaderboard read model

`UserRatings` holds the precomputed global score, solved count, accepted attempts
and last accepted activity. `RatingAcceptedActivities` holds one validated record
for every accepted code/image/test/math attempt, with the current assignment
rating, course and submission time. The API **never** loads all verdicts from
Tasks/Solutions to render the leaderboard; course and date filters aggregate the
stored activity projection using SQL.

The worker refreshes each affected user's summary and activity records in **one
transaction**. On first start, when checkpoint `RatingAcceptedActivitiesV1` is
missing, a full rebuild is required before `/api/leaderboard` becomes ready.
The API returns `503 RATING_PROJECTION_BUILDING` without charging leaderboard
energy until the initial rebuild completes. Subsequent dirty changes are
handled on the normal configured interval; a full daily rebuild remains a
safety net. The per-user account merge/delete operations remove obsolete
activity records.

Schema owner: Solutions API; generate `AddRatingAcceptedActivities` migration
with `./scripts/generate-migrations.sh AddRatingAcceptedActivities solutions`
on the operator workstation before deploying. Deploy Tasks API with the
`attemptId` / `courseId` additions before the new worker, since the worker
refuses incomplete attempt metadata instead of silently dropping ratings.
