# syntax=docker/dockerfile:1
ARG GO_IMAGE=golang:1.26.8-bookworm
FROM ${GO_IMAGE} AS build
ARG TASKFORGE_BUILD_DEBUG_LOGS=1
ENV CGO_ENABLED=1 GOTOOLCHAIN=local
RUN apt-get update && apt-get install -y --no-install-recommends libpq-dev libmariadb-dev libsqlite3-dev pkg-config \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY go.mod ./
COPY cmd ./cmd
COPY internal ./internal
RUN mkdir -p /out && test "${TASKFORGE_BUILD_DEBUG_LOGS}" = 1 \
    && go vet ./... \
    && go build -trimpath -buildvcs=false -ldflags='-s -w' -o /out/sql-worker ./cmd/sql-worker \
    && go test -c -race -trimpath -buildvcs=false -o /out/sqlworker.test ./internal/sqlworker \
    && go test -c -race -trimpath -buildvcs=false -o /out/wakeup.test ./internal/wakeup

FROM debian:bookworm-slim AS runtime
ARG TASKFORGE_BUILD_DEBUG_LOGS=1
ENV TASKFORGE_BUILD_DEBUG_LOGS=${TASKFORGE_BUILD_DEBUG_LOGS} \
    TASKFORGE_DEBUG_LOGS=1 \
    GOMEMLIMIT=128MiB \
    SQL_CACHE_DIR=/var/cache/taskforge-sql
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates libpq5 libmariadb3 libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app /var/cache/taskforge-sql \
    && chown 10001:10001 /var/cache/taskforge-sql
WORKDIR /app
COPY --from=build --chmod=0555 /out/sql-worker /app/sql-worker
USER 10001:10001
EXPOSE 8081
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 CMD ["/app/sql-worker", "health"]
ENTRYPOINT ["/app/sql-worker"]

# Only the explicit Docker integration gate builds this target. These test
# binaries, credentials and scripts are NOT included in the production image.
FROM runtime AS integration
COPY --from=build --chmod=0555 /out/sqlworker.test /out/wakeup.test /checks/
COPY run-integration.sh /checks/run-integration.sh
ENV SQL_TEST_HELPER=/app/sql-worker SQL_RUN_LOAD_TESTS=1
ENTRYPOINT ["/checks/run-integration.sh"]

FROM runtime AS release
