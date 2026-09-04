# TaskForge v40 r2 — application HA and Node Agent

## Node roles

- **A / full / primary candidate** — active full TaskForge.
- **B / full / primary candidate** — hot full standby.
- **C / lite / nofailover / assist** — voter + data replica + reduced hot standby.

C excludes `browser-api` and `image-analyzer` by default. Because those
containers are not created on C, Watchtower does not pull/update their images.

## Normal update model

Watchtower runs on **A, B and C**.

- A: updates the running full stack.
- B: `WATCHTOWER_INCLUDE_STOPPED=true` updates the prepared stopped full stack.
- C: updates only the prepared stopped lite stack.
- `WATCHTOWER_REVIVE_STOPPED=false` is mandatory: updating a standby container
  must never start it.

Node Agent does not run a second five-minute updater. It only creates missing
prepared containers and repairs their shape. Once a stopped container exists,
Watchtower is its normal updater.

## Hot standby preparation

For B/C the agent prepares assigned services with `--no-start --no-deps`.
It first tries cached images with `--pull never`. If a first deployment is
missing images, the slow `compose pull` runs **outside** the application
transition lock. If failover happens during that pull, promotion wins and the
late prepare step is abandoned.

## Failover/update race

Watchtower is normally ON on standby. It is paused only for the short activation
or repair window:

1. Patroni/etcd grants B the primary role.
2. Node Agent stops Watchtower and waits until it is really stopped.
3. B rewrites local routing for the new writable primary.
4. Prepared containers are reconciled with `--pull never` (no GHCR dependency).
5. The prepared containers are started.
6. Health/traffic-ready passes.
7. Watchtower is started again.

The same local-only activation rule is used when C starts its lite assist profile.
A dead registry therefore cannot delay restoring the site.

## Steady-state behavior

The agent does **not** flap Watchtower every control tick. A healthy active or
assist stack keeps Watchtower running continuously. If a standby application is
accidentally running, the agent briefly fences Watchtower, stops the application,
and immediately enables Watchtower again.

## Migrations

Only a writable active application has `MIGRATE_ON_STARTUP=true`. PostgreSQL
physical replication carries schema and migration-history changes to B/C. On a
B promotion, already replicated migrations are skipped; truly pending ones are
applied against the new primary. C assist forces `MIGRATE_ON_STARTUP=false`.

## Telemetry

`GET /ha/live` is the lightweight liveness probe. Observability polls it frequently
and declares a node unavailable only after the configured grace period (60 seconds
by default), so a short Docker/network stall does not generate DOWN/UP spam.

`GET /ha/telemetry` reports host health, Docker state, PostgreSQL/MinIO,
Watchtower running state, app profile/readiness and internal image fingerprints.
It serves the last completed snapshot without waiting for the HA control lock.
Observability samples this heavier endpoint separately (30 seconds by default),
so liveness does not depend on telemetry collection speed. Fingerprints are used
only for exact comparison; admin UI exposes human states (`Одинаковая версия`,
`Версия отличается`, `Не назначен`) and never the IDs.

## Public traffic

`/ha/traffic-ready` is the authoritative public-origin health route. Patroni/Node Agent decide whether a node may serve traffic; Cloudflare should use that endpoint for origin failover/load-balancer health. The private Node Agent port `9187` stays WireGuard-only and must not be used as an Internet health target.
