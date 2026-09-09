# TaskForge 98 CI fix audit

> **HISTORICAL SNAPSHOT.** This file describes the state of an older TaskForge 98 CI incident and is not a source of current versions or architecture. For the current project state read `00_AI_READ_THIS_FIRST.md`, the service READMEs and the actual build files.


## Причина падения

`check-minecraft-plugin-runtime-invariants.sh` требует, чтобы в `plugins/minecraft` оставался только каталог `minecraft-plugin-folia` с двумя поддерживаемыми проектами:

- `TaskForgeFoliaPlugin` (`TaskForgeLink.jar`)
- `CustomMobTweaksPlugin` (`CustomMobTweaks.jar`)

В предыдущем архиве по ошибке остались два выведенных из эксплуатации Maven-проекта:

- `default-group-assigner`
- `world-loader-folia`

Поэтому CI корректно завершался с ошибкой `retired top-level Minecraft plugin projects returned` до начала Gradle-сборки.

## Исправление

- удалены оба устаревших проекта;
- CI-инвариант сохранён и теперь выводит фактически найденные каталоги при ошибке;
- актуализированы зафиксированные версии в `00_AI_READ_THIS_FIRST.md`:
  - TaskForgeLink 1.8.2;
  - CustomMobTweaks 2.1.1;
- Java-код плагинов, конфигурации механик, миграции и ModelSnapshot не изменялись.

## Проверки

```bash
bash scripts/ci/check-minecraft-plugin-runtime-invariants.sh
```

Ожидаемый результат:

```text
minecraft plugin runtime invariants ok
```
