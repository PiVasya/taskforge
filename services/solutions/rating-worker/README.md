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
