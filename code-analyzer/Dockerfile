# ---------- build ----------
FROM rust:1.77-slim AS build
WORKDIR /app

# speed up builds: cache deps
COPY Cargo.toml Cargo.toml
RUN mkdir -p src && echo "fn main(){}" > src/main.rs
RUN cargo build --release

# actual source
COPY src ./src
RUN cargo build --release

# ---------- runtime ----------
FROM debian:bookworm-slim
RUN useradd -m appuser
WORKDIR /app
COPY --from=build /app/target/release/taskforge-code-analyzer /app/taskforge-code-analyzer

# Small wrapper so we always see "started" and also see the exit code
# if the process terminates immediately (helps debug restart loops).
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/taskforge-code-analyzer /app/entrypoint.sh
USER appuser
EXPOSE 8080
# Default to very verbose logs for MVP debugging in production.
ENV RUST_LOG=debug \
    RUST_BACKTRACE=1

ENTRYPOINT ["/app/entrypoint.sh"]
