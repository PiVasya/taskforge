# Split Docker Compose for TaskForge

Файл `docker-compose-develop.yaml` оставлен монолитным для обратной совместимости.

Новая разбитая версия:

```bash
docker compose -f docker-compose-develop-split.yaml config --no-interpolate
docker compose -f docker-compose-develop-split.yaml up -d
```

На проде безопаснее указывать текущий project name явно, например:

```bash
docker compose -p taskforge-prod-linux -f docker-compose-prod-split.yml up -d telegram-quiz-bot
```

Не используй `down -v` на живом проде.
Не используй `--remove-orphans`, пока полностью не убедился, что split-файл покрывает все старые контейнеры.

`watchtower` оставлен в модуле `compose/ops.yaml`; сервисы с label `com.centurylinklabs.watchtower.enable: "true"` будут обновляться автоматически.
