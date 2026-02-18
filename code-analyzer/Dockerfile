# ---------- build ----------
FROM rust:1.81-slim AS build
WORKDIR /app

# speed up builds: cache deps
COPY Cargo.toml Cargo.toml
COPY Cargo.lock Cargo.lock
RUN mkdir -p src && echo "fn main(){}" > src/main.rs
RUN cargo build --release

# actual source
COPY src ./src
RUN cargo build --release

# ---------- runtime ----------
FROM debian:bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m appuser
WORKDIR /app
COPY --from=build /app/target/release/taskforge-code-analyzer /app/taskforge-code-analyzer
USER appuser
EXPOSE 8080
ENV RUST_LOG=debug
ENV RUST_BACKTRACE=1
CMD ["/app/taskforge-code-analyzer"]
