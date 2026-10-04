FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
COPY security/image_sandbox_guard.c ./
COPY security/sandbox_preload.c ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner . \
  && gcc -c -fPIC -O2 -Wall -Wextra -Werror -fvisibility=hidden -fno-stack-protector -fno-builtin \
      -o /out/taskforge_image_cpp_sandbox_guard.o image_sandbox_guard.c \
  && gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
      -Wl,-z,relro,-z,now,-z,noexecstack \
      -o /out/libtaskforge_sandbox.so sandbox_preload.c

FROM gcc:14-bookworm
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates util-linux \
    freeglut3-dev libgl1-mesa-dev libglu1-mesa-dev mesa-utils \
    xvfb xauth xdotool x11-utils imagemagick openbox fonts-dejavu-core \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/runner /app/runner
COPY --from=build /out/taskforge_image_cpp_sandbox_guard.o /app/taskforge_image_cpp_sandbox_guard.o
COPY --from=build /out/libtaskforge_sandbox.so /app/libtaskforge_sandbox.so
COPY include /opt/taskforge/include
COPY security/sandbox_guard.c /tmp/taskforge_cpp_sandbox_guard.c
RUN gcc -O2 -fPIC -Wall -Wextra -Werror -fvisibility=hidden -fno-stack-protector -fno-builtin \
      -c /tmp/taskforge_cpp_sandbox_guard.c \
      -o /app/taskforge_cpp_sandbox_guard.o \
  && chmod 0444 /app/taskforge_cpp_sandbox_guard.o /app/taskforge_image_cpp_sandbox_guard.o /app/libtaskforge_sandbox.so \
  && rm -f /tmp/taskforge_cpp_sandbox_guard.c
ENV HOME=/tmp RUNNER_KIND=cpp PORT=8080
EXPOSE 8080 9090
USER 65534:65534
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=cpp-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=cpp-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/runner"]
