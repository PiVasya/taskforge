# Production hardening notes

v18 production pass focuses on safe single-host Docker Compose deployment.

## Secrets

- `deploy/prod/.env` is ignored by Git and Docker build contexts.
- `deploy/prod/.env.example` contains only placeholders.
- `scripts/prod/prepare-env.sh` creates `.env` and replaces placeholder secrets with random values.
- `scripts/prod/check-prod-config.sh` fails startup when required secrets are missing, too short or still contain placeholders.

Required production secrets:

- `POSTGRES_PASSWORD`
- `RABBITMQ_DEFAULT_PASS`
- `MINIO_ROOT_PASSWORD`
- `JWT_SIGNING_KEY`
- `TASKFORGE_INTERNAL_KEY`
- `TASKFORGE_AGENT_INTERNAL_KEY`

## Password storage

Identity now stores new and changed user passwords as `PBKDF2-SHA256$iterations$hash` with a per-user random salt. Legacy SHA256 hashes remain readable only for compatibility and are rehashed on successful login.

## Auth and internal API

- Internal endpoints are guarded by `X-Internal-Key` and return 404 when the key is absent or invalid.
- Weak/placeholder JWT and internal keys are rejected in `ASPNETCORE_ENVIRONMENT=Production`.
- Query-string access tokens are accepted only for `/hubs/*` WebSocket handshakes.
- Courses, groups, leaderboard, assignments, solutions and execution/compiler routes require authentication or editor/admin roles.

## Runner isolation

Language runners are attached only to an internal `runner-net`. They do not share the default service network with PostgreSQL, RabbitMQ, MinIO or business APIs. `execution-worker` is the bridge between the service network and `runner-net`.

Runner container restrictions:

- `no-new-privileges:true`
- `cap_drop: [ALL]`
- `read_only: true`
- `pids_limit`
- `mem_limit`
- tmpfs `/tmp` with `exec` only for compiled binaries

## Image workflow

The GitHub Actions workflow checks that every project Dockerfile is present in the image matrix and that every image used by prod compose is built. It runs on `develop`, `main`, tags `v*`, pull requests and manual dispatch.

## v19 deployment update policy

GitHub Actions no longer rebuilds every image on a regular push. The default behavior is diff-based:

- a change in `services/tasks/assignment-api/` builds only `tasks-api`;
- a change in `apps/web/` builds only `front`;
- a change in deployment scripts or docs builds no application images;
- a manual workflow run with `build_all=true` still rebuilds everything.

The production server should run Watchtower from `deploy/prod/compose/80-watchtower.yaml`. It updates only TaskForge services labeled with:

```text
com.centurylinklabs.watchtower.enable=true
com.centurylinklabs.watchtower.scope=taskforge-prod
```

Storage containers are intentionally not labeled, so PostgreSQL/RabbitMQ/MinIO are not auto-updated by Watchtower.
