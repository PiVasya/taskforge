FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner .

FROM gcc:14-bookworm
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates util-linux \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/runner /app/runner
COPY security/sandbox_guard.c /tmp/taskforge_cpp_sandbox_guard.c
RUN gcc -O2 -fPIC -Wall -Wextra -Werror -fvisibility=hidden -fno-stack-protector -fno-builtin \
      -c /tmp/taskforge_cpp_sandbox_guard.c \
      -o /app/taskforge_cpp_sandbox_guard.o \
  && chmod 0444 /app/taskforge_cpp_sandbox_guard.o \
  && rm -f /tmp/taskforge_cpp_sandbox_guard.c
ENV RUNNER_KIND=cpp PORT=8080
EXPOSE 8080 9090
USER 65534
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=cpp-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=cpp-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/runner"]
