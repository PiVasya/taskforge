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
USER appuser
EXPOSE 8080
ENV RUST_LOG=info
CMD ["/app/taskforge-code-analyzer"]
