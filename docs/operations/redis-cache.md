# Redis cache in TaskForge

TaskForge uses Redis as a shared L2 cache between microservices.

Main goal:

```text
service -> in-process memory/IDistributedCache -> Redis -> internal API/Postgres
```

What is cached first:

- `identity-api` internal user summaries: `/api/internal/users/summaries`.
- `education-api` course metadata: `/api/internal/courses/metadata`.
- `education-api` paged course catalog: `/api/courses?page=&pageSize=&q=`.
- `tasks-api` assignment summaries: `/api/internal/assignments/summaries`.

The cache is intentionally TTL-based. It is safe if Redis is empty: services fall back to Postgres/internal APIs.

## Env variables

```env
CACHE_ENABLED=true
CACHE_DEFAULT_TTL_SECONDS=300
CACHE_METADATA_TTL_SECONDS=300
CACHE_USER_SUMMARIES_TTL_SECONDS=120
CACHE_LEADERBOARD_TTL_SECONDS=30
CACHE_ACTIVITY_TTL_SECONDS=30
REDIS_PASSWORD=change_me_in_prod
```

Dev uses Redis without a password inside Docker Compose:

```text
redis:6379
```

Prod uses a password-protected Redis container bound to localhost by default:

```text
redis:6379,password=...
```

## Server checks

```bash
docker ps --filter name=redis

docker logs --since 10m taskforge-prod-redis-1

docker exec -it taskforge-prod-redis-1 sh -lc 'redis-cli -a "$REDIS_PASSWORD" ping'
```

A healthy answer is:

```text
PONG
```

## Reset cache

```bash
docker exec -it taskforge-prod-redis-1 sh -lc 'redis-cli -a "$REDIS_PASSWORD" FLUSHDB'
```

Use reset only during debugging or after a large data migration.
