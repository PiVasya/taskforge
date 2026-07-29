FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
COPY security/sandbox_guard.c ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner .
RUN gcc -c -fPIC -O2 -Wall -Wextra -Werror -fvisibility=hidden -fno-stack-protector -fno-builtin \
      -o /out/taskforge_pascal_sandbox_guard.o sandbox_guard.c

FROM debian:bookworm-slim
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates fpc util-linux \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/runner /app/runner
COPY --from=build /out/taskforge_pascal_sandbox_guard.o /app/taskforge_pascal_sandbox_guard.o
COPY security/TaskForgeSandboxGuard.pas /tmp/TaskForgeSandboxGuard.pas
RUN mkdir -p /opt/taskforge/pascal-guard \
  && fpc -O2 -Fu/opt/taskforge/pascal-guard -FU/opt/taskforge/pascal-guard /tmp/TaskForgeSandboxGuard.pas \
  && chmod -R a-w /opt/taskforge/pascal-guard \
  && chmod 0444 /app/taskforge_pascal_sandbox_guard.o \
  && rm -f /tmp/TaskForgeSandboxGuard.pas
ENV RUNNER_KIND=pascal PORT=8080
EXPOSE 8080 9090
USER 65534
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=pascal-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=pascal-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/runner"]
