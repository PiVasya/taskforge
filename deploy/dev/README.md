# Dev compose

Обычный запуск через корневой helper:

```bash
./build.sh
```

Собрать один сервис:

```bash
./build.sh tasks-api
```

Посмотреть логи:

```bash
./build.sh logs
./build.sh logs judge
```

`deploy/dev/compose.sh` оставлен как тонкий wrapper над `docker compose`, если нужна ручная команда:

```bash
./deploy/dev/compose.sh ps
./deploy/dev/compose.sh logs -f --tail=200 tasks-api
./deploy/dev/compose.sh up -d --no-build tasks-api
```

## Redis cache

The compose stack includes Redis. Backend services receive `ConnectionStrings__Redis` and cache hot metadata such as user summaries, course metadata and assignment summaries. See `docs/operations/redis-cache.md`.
