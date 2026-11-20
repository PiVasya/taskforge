# TaskForge — проверки изолированной ветки K3s

Проверки различаются по тому, что они действительно доказывают.

| Команда | Что она проверяет | Чего она не доказывает |
|---|---|---|
| `bash scripts/tests/repository.sh` | Модели ownership, историю EF, манифесты, моки утилит CI | Работу API с БД |
| `bash scripts/tests/fast.sh` | Предыдущее + .NET build/unit, Node, Go | Успех миграций на YugabyteDB |
| `bash scripts/tests/all.sh` | Предыдущее + React build и временную YugabyteDB | Полный пользовательский E2E через K3s |
| `python3 scripts/ci/check-database-table-ownership.py` | Соответствие EF snapshots целевому списку таблиц | Применённые миграции |
| `python3 scripts/ci/check-ef-migrations.py --targets assignment,solutions,education` | Отсутствие EF pending model changes (требует .NET 10) | Фактическую работу схемы |

До генерации миграций Tasks/Solutions первая проверка обязана выдавать
ошибку. Это не успех и не причина отключать проверку:

```bash
bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions
```

Имеющиеся тесты в памяти и статические проверки не заменяют тесты
на реальной YugabyteDB, HTTP-сценарии test/math/code/SQL/image,
удаление/объединение аккаунтов, историю, рейтинг и SSE.
