# CustomMobTweaksPlugin

Folia-плагин с конфигурируемыми усилениями мобов.

## Что добавлено
- Blaze small fireball -> молния при попадании
- Bogged arrow -> иссушение
- Wither Skeleton melee -> короткий "стан"
- Snow Golem snowball -> наносит урон и может слегка замедлить
- Breeze wind charge -> наносит дополнительный урон и подбрасывает
- Skeleton / Stray sniper -> ускоренные и более точные стрелы на дистанции
- Drowned trident -> подсветка цели
- Pillager bolts -> дополнительный урон
- Stray arrow -> дополнительный chill-эффект

## Важно
"Самонаводка" у скелетов сделана в мягком, Folia-safe виде:
стрела доводится в сторону цели в момент выстрела с упреждением,
без опасных повторяющихся world/entity тиков через чужие регионы.

## Сборка на Fedora
```bash
sudo dnf install java-21-openjdk-devel unzip
sudo dnf install gradle --enablerepo=updates-testing
```

Если `gradle` снова не находится:
```bash
sudo dnf search gradle
```

Сборка:
```bash
cd minecraft-plugin-folia/CustomMobTweaksPlugin
gradle build
```

Готовый jar:
```bash
build/libs/CustomMobTweaksPlugin-1.0.0.jar
```

## Быстрая настройка
Все значения лежат в `src/main/resources/config.yml`.
После первого запуска сервера плагин создаст обычный `plugins/CustomMobTweaks/config.yml`,
и дальше меняешь уже его.


## v1.1.0 updates
- Pillager crossbow hits now strip vanilla armor reduction and protection-enchant reduction.
- Breeze wind charge can now be replaced with a configurable small fireball.
