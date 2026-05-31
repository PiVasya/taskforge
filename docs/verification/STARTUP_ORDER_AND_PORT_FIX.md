# Startup order and dev port fix

The real dev run showed that all images built, including `image-pascal-runner`, but API services attempted to apply EF migrations while PostgreSQL was still bootstrapping. The logs showed repeated `NpgsqlException: Failed to connect ... Connection refused` for DB-owning APIs.

Fixes:

- PostgreSQL healthcheck now has a longer startup window.
- RabbitMQ healthcheck now has a longer startup window.
- Compose `depends_on` is now condition-based:
  - `postgres` -> `service_healthy`
  - `rabbitmq` -> `service_healthy`
  - other services -> `service_started`
- Dev gateway host port is configurable and defaults to `18080` instead of hardcoded `8080`.

No migrations were generated.
