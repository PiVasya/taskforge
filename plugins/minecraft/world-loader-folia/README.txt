WorldLoaderFolia 1.1.0
======================

Этот плагин больше не создаёт namespace-миры и не подменяет генераторы.
На TaskForge источником правды является plugins/Worlds/worlds.dat.

По умолчанию:
- lobby-world: minecraft:overworld
- survival-world: owp:overworld
- allow-world-creation: false
- autoload-survival-on-start: false

Команды:
- /loadworld <namespace:world> — проверить/получить уже загруженный мир;
- /tpsurv — телепорт в настроенный survival-world;
- /tplobby — телепорт в настроенный lobby-world.

Если Worlds установлен и мир не загружен, плагин не вызывает Bukkit.createWorld.
Сначала исправь/загрузи запись в plugins/Worlds/worlds.dat.

Сборка: Java 25 + Maven
  mvn -B -ntp clean package

JAR:
  target/WorldLoaderFolia.jar
