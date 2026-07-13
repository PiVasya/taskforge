# TaskForge Minecraft plugins

В каталоге находятся два плагина для Folia 26.1.2 и Java 25:

- `TaskForgeFoliaPlugin` — привязка аккаунта, чат TaskForge, отдельный Minecraft-баланс и система восстановления после смерти;
- `CustomMobTweaksPlugin` — настраиваемые усиления мобов.

## Автоматическая сборка

Workflow `.github/workflows/minecraft-plugins-build.yml` собирает оба плагина при push, pull request и ручном запуске.

Артефакт GitHub Actions содержит:

```text
TaskForgeLink.jar
CustomMobTweaks.jar
SHA256SUMS.txt
taskforge-minecraft-plugins.zip
```

## Локальная сборка

Нужны Java 25 и Gradle 9.4.1 либо совместимая новая версия.

```bash
cd plugins/minecraft/minecraft-plugin-folia/TaskForgeFoliaPlugin
./gradlew --no-daemon clean build

cd ../CustomMobTweaksPlugin
./gradlew --no-daemon clean build
```

Если Gradle Wrapper в рабочей копии отсутствует, используй системный `gradle` той же версии.

## Система смерти

Плагин перехватывает только итоговый список `PlayerDeathEvent#getDrops()`. Опыт выпадает по стандартным правилам Minecraft, а предметы с проклятием утраты не сохраняются.

После респавна игрок получает четыре кнопки:

```text
[Сундук — 50]
[Вернуться — 100]
[Сундук + возврат — 150]
[Обычный дроп]
```

Если время предложения истекло, вещи выпадают обычным способом в точке смерти. При недоступности Minecraft API либо рейтинговых backend-сервисов вместо платного действия создаётся бесплатный сундук, а игрок получает сообщение и точные координаты.

### Сундук

- создаётся обычный незащищённый двойной сундук;
- поиск выполняется в радиусе 16 блоков;
- можно заменить только воздух, воду и лаву;
- твёрдые блоки не разрушаются;
- количество сундуков не ограничено.

### Возврат

- 100 рейтинга списываются сразу после нажатия кнопки;
- обычный дроп появляется в исходной точке смерти;
- игрок получает 5 секунд spectator-режима;
- область выбора ограничена радиусом 12 блоков;
- после таймера выбирается безопасная точка и восстанавливается предыдущий игровой режим;
- оплаченная операция продолжается после переподключения или перезапуска.

### Мультимиры

Для каждой смерти сохраняются UUID мира, точный `NamespacedKey` и точное Bukkit-имя. Разрешение выполняется только в порядке:

```text
UUID -> NamespacedKey -> точное имя
```

Подстановки первого мира, хаба, `minecraft:overworld` либо соответствующего `terra:*` нет. Плагин не изменяет `plugins/Worlds/worlds.dat`, Portals, Terra и `bukkit.yml`.

## Настройка

Основные параметры находятся в `TaskForgeFoliaPlugin/src/main/resources/config.yml` в секции `deathRecovery`:

```yaml
deathRecovery:
  enabled: true
  offerSeconds: 300
  spectatorSeconds: 5
  maxDistance: 12
  chestSearchRadius: 16
  safeSearchRadius: 8
  chestCost: 50
  teleportCost: 100
  backendTimeoutMillis: 5000
```
