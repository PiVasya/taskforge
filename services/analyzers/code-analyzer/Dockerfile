# Используем актуальный stable toolchain, т.к. Cargo.lock может иметь новый формат.
# (Например lockfile version 4 не поддерживается старыми Cargo из rust:1.77)
FROM rust:slim-bookworm AS build

WORKDIR /app

# ВАЖНО: собираем РЕАЛЬНЫЙ код.
# Раньше использовался трюк с "пустышкой" main.rs для кэша зависимостей.
# В CI он приводил к тому, что в итоговый образ попадал бинарник, который сразу завершался (exit=0).
# Здесь делаем простой и надёжный билд.

COPY Cargo.toml Cargo.toml
COPY Cargo.lock Cargo.lock
COPY src ./src

RUN cargo build --release


FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates openssl clang \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --uid 1000 --create-home appuser
WORKDIR /app

COPY --from=build /app/target/release/taskforge-code-analyzer /app/taskforge-code-analyzer
COPY include /opt/taskforge/include

RUN chown -R appuser:appuser /app \
    && chmod -R a-w /opt/taskforge/include
USER appuser

EXPOSE 8080

ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=code-analyzer\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=code-analyzer\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/taskforge-code-analyzer"]
