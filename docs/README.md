# TaskForge — локальная K3s/YugabyteDB ветка

Это исходники **изолированной `cluster-v2`** для запуска на компьютере
разработчика. В этом дереве **нет** старых production/dev Compose-сборок.
Работающий TaskForge на VPS не является частью этой среды.

## Архитектура

- **Education** — курсы, карты, группы и доступы.
- **Tasks** — определения заданий всех пяти типов и проверка test/math.
- **Solutions** — единственный владелец пользовательских попыток пяти типов,
  результаты, история, рейтинг, квоты и события админской ленты.
- **Execution** — техническое исполнение программ и SQL.

Подробности: [граница владения](architecture/TASK_DATA_OWNERSHIP.md).

## Перед первым запуском

На ПК с .NET 10 SDK, **из корня проекта**:

```bash
bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions
```

Это создаёт **две EF-миграции в исходниках**, но не применяет SQL.
Открой полученные `.cs` и `.Designer.cs`; проверь, что в Tasks удаляется
только старая таблица `TaskAttempts`, а в Solutions она создаётся.
После этого запусти проверки C# и только затем разверни пустую локальную БД.

Справочник: [локальная установка K3s](operations/README_K3S_BRANCH.md),
[CI и диагностика](operations/K3S_CI_RUNBOOK.md).

## Проверки

```bash
bash scripts/tests/repository.sh  # Статика/контракты; требует готовых EF snapshots
bash scripts/tests/fast.sh        # В том числе .NET 10 и Go
bash scripts/tests/all.sh         # Плюс фронтенд-бандл и одноразовая YugabyteDB
```

Тесты по исходникам и мокам не заменяют интеграционные проверки в настоящей
YugabyteDB. До генерации EF-миграций проверка ownership snapshots должна падать.
