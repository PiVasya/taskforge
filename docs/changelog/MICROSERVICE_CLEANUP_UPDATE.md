# Microservice cleanup update

Что сделано после первичного разбиения `Program.cs`:

- удалены миграционные папки `extracted/` со старыми монолитными исходниками;
- удалены устаревшие `docs/original/`, `scripts/original/` и `docs/source-map/`;
- из `.csproj` убраны правила исключения `extracted/**`, потому что этих папок больше нет;
- endpoint-support/helper-файлы вынесены из `Endpoints/` в `Services/<Area>/`;
- `TaskForgeCache.cs` перенесён из корня сервисов в `Infrastructure/Caching/`;
- корневые update-notes перенесены в `docs/changelog/`;
- обновлён `docs/architecture/microservice-source-layout.md`.

Итог: в `Endpoints/` остаются в основном файлы маршрутов, а вспомогательная логика и инфраструктура больше не лежат вперемешку рядом с контроллерными группами.
