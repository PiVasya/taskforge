# Rating worker

Новый рейтинг не должен пересчитываться при каждом открытии лидерборда.

Целевая схема:

1. `solutions-api` сохраняет попытку/вердикт.
2. Публикуется событие `SolutionVerdictChanged`.
3. `rating-worker` применяет событие к read-model таблицам:
   - `UserRatings`
   - `LeaderboardEntries`
   - `RatingProjectionCheckpoints`
4. `/api/solutions/leaderboard` читает уже готовую таблицу, а не сканирует все решения.

Миграции принадлежат `../api`; worker не владеет схемой БД.
