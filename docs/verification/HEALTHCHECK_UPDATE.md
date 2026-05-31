# Healthcheck update

Добавлены healthcheck'и для всех сервисов в `deploy/dev` и `deploy/prod`.

Проверено:

- YAML compose-файлы парсятся.
- Все dev/prod compose services имеют healthcheck.
- Dockerfile paths существуют.
- Production compose не содержит `build`.
- Go runner tests проходят.
- Python вне `services/analyzers/image-analyzer` отсутствует.
- EF `Migrations/` директории отсутствуют.

Миграции не генерировались.
