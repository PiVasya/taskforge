# TaskForge Minecraft plugins

В репозитории собираются четыре плагина под Folia 26.1.2 и Java 25:

- `TaskForgeLink` — привязка аккаунта, чат, Minecraft-баланс и система смерти;
- `CustomMobTweaks` — настраиваемые усиления мобов;
- `DefaultGroupAssigner` — назначение стартовой группы через глобальный Folia scheduler;
- `WorldLoaderFolia` — безопасный помощник телепортации к уже зарегистрированным мирам.

Workflow `.github/workflows/minecraft-plugins-build.yml` собирает все четыре JAR и архив:

```text
TaskForgeLink.jar
CustomMobTweaks.jar
DefaultGroupAssigner.jar
WorldLoaderFolia.jar
SHA256SUMS.txt
taskforge-minecraft-plugins.zip
```

## Система смерти

Плагин перехватывает только итоговый список `PlayerDeathEvent#getDrops()`. Опыт остаётся ванильным. Если другой плагин или gamerule уже включил `keepInventory`, TaskForge не перехватывает предметы и не создаёт их копию.

После респавна показываются пять действий:

```text
[Координаты — 10]
[Сундук — 50]
[Вернуться — 100]
[Сундук + возврат — 150]
[Обычный дроп]
```

`Координаты` списывает 10 рейтинга, выпускает обычный дроп в исходной точке и отправляет точный namespace мира и XYZ. Если время предложения истекло, предметы выпадают обычно.

При недоступности Minecraft API либо рейтинговых backend-сервисов платные действия не выполняются: предметы бесплатно сохраняются в обычном сундуке, а возможное неоднозначное списание компенсируется идемпотентно.

### Сундук

- сначала ищется ближайший двойной сундук в радиусе 16 блоков;
- затем поиск расширяется по уже загруженным соседним чанкам до 256 блоков;
- если рядом всё занято, используется свободное место около spawn **того же точного мира**;
- заменяются только воздух, вода и лава; твёрдые блоки не ломаются;
- при временной невозможности поставить сундук предметы остаются в журнале и поиск повторяется; обычный дроп при аварии backend не используется;
- после создания игрок всегда получает namespace мира и координаты сундука.

### Возврат

- 100 рейтинга списываются сразу после нажатия;
- обычный дроп появляется в исходной точке смерти;
- игрок получает 5 секунд spectator-режима в радиусе 12 блоков;
- после таймера выбирается безопасная точка и восстанавливается предыдущий игровой режим;
- если безопасной точки нет, игрок возвращается в позицию до начала spectator, а игровой режим всё равно восстанавливается;
- оплаченная операция продолжается после переподключения или перезапуска.

### Мультимиры

Для смерти сохраняются UUID мира, точный `NamespacedKey` и точное Bukkit-имя. Разрешение выполняется только в порядке:

```text
UUID -> NamespacedKey -> точное имя
```

Нет подстановки первого мира, хаба, `minecraft:overworld` или `terra:*`. Плагин не редактирует `plugins/Worlds/worlds.dat`, Portals, Terra и `bukkit.yml`.

`WorldLoaderFolia` также не создаёт namespace-миры. При установленном Worlds источником правды остаётся `plugins/Worlds/worlds.dat`; незагруженный мир нужно загрузить через Worlds, а не создавать ванильную замену.

## Основные параметры

```yaml
deathRecovery:
  enabled: true
  offerSeconds: 300
  spectatorSeconds: 5
  maxDistance: 12
  chestSearchRadius: 16
  emergencyChestSearchRadius: 256
  safeSearchRadius: 8
  coordinatesCost: 10
  chestCost: 50
  teleportCost: 100
  backendTimeoutMillis: 5000
```

## Локальная сборка

Нужны Java 25, Gradle 9.4.1 и Maven.

```bash
cd plugins/minecraft/minecraft-plugin-folia/TaskForgeFoliaPlugin
gradle --no-daemon clean build

cd ../CustomMobTweaksPlugin
gradle --no-daemon clean build

cd ../../../default-group-assigner
mvn -B -ntp clean package

cd ../world-loader-folia
mvn -B -ntp clean package
```
