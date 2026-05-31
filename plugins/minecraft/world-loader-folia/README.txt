WorldLoaderFolia (Folia-safe)
============================

Команды:
- /loadworld <world>  — загрузить (или создать) мир по имени папки.
- /tpsurv            — телепорт в мир survival (создаст/загрузит, если надо).
- /tplobby           — телепорт обратно в мир world.

Права:
- worldloader.load (default: op)
- worldloader.tp   (default: op)

Сборка:
1) Установи Java 21 и Maven.
2) В папке проекта:
   mvn -q package
3) Готовый файл будет:
   target/WorldLoaderFolia.jar  (или WorldLoaderFolia.jar рядом с target, зависит от Maven)

Установка:
- Положи WorldLoaderFolia.jar в plugins/
- Перезапусти сервер

Настройка:
plugins/WorldLoaderFolia/config.yml
