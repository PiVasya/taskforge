FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
COPY security/sandbox_preload.c ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner .
RUN gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
      -Wl,-z,relro,-z,now,-z,noexecstack \
      -o /out/libtaskforge_sandbox.so sandbox_preload.c

FROM debian:bookworm-slim
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates python3 \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/runner /app/runner
COPY --from=build /out/libtaskforge_sandbox.so /app/libtaskforge_sandbox.so
COPY security/python_policy.py /opt/taskforge/python_policy.py
ENV RUNNER_KIND=python PORT=8080 PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
EXPOSE 8080
USER 65534
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=python-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=python-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/runner"]
