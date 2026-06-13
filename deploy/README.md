# Deploy environments

Dev и prod теперь лежат рядом и устроены одинаково:

```text
deploy/
  dev/
    .env.example
    README.md
    compose.sh
    compose/
      00-storage.yaml
      10-apps-gateway.yaml
      20-core-services.yaml
      30-execution.yaml
      40-ai-and-analyzers.yaml
      50-integrations.yaml

  prod/
    .env.example
    README.md
    compose.sh
    compose/
      00-storage.yaml
      10-apps-gateway.yaml
      20-core-services.yaml
      30-execution.yaml
      40-ai-and-analyzers.yaml
      50-integrations.yaml
      90-certbot.yaml
    docs/
      MIGRATIONS_POLICY.md
```

## Dev

```bash
cp deploy/dev/.env.example deploy/dev/.env
./deploy/dev/compose.sh up --build
```

Dev собирает контейнеры из локального исходного кода.

## Prod

```bash
cp deploy/prod/.env.example deploy/prod/.env
./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up -d
```

Prod использует готовые Docker images из registry и настраивается через `deploy/prod/.env`.

## Redis cache

The compose stack includes Redis. Backend services receive `ConnectionStrings__Redis` and cache hot metadata such as user summaries, course metadata and assignment summaries. See `docs/operations/redis-cache.md`.
