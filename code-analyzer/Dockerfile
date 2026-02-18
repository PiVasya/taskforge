# Используем актуальный stable toolchain, т.к. Cargo.lock может иметь новый формат.
# (Например lockfile version 4 не поддерживается старыми Cargo из rust:1.77)
FROM rust:slim AS build

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
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m appuser
WORKDIR /app

COPY --from=build /app/target/release/taskforge-code-analyzer /app/taskforge-code-analyzer

RUN chown -R appuser:appuser /app
USER appuser

EXPOSE 8080

CMD ["/app/taskforge-code-analyzer"]
