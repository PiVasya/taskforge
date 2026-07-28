FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
COPY security/sandbox_guard.c ./
COPY security/sandbox_preload.c ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/image-runner .
RUN gcc -c -fPIC -O2 -Wall -Wextra -Werror -fvisibility=hidden -fno-stack-protector -fno-builtin \
      -o /out/taskforge_image_cpp_sandbox_guard.o sandbox_guard.c
RUN gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
      -Wl,-z,relro,-z,now,-z,noexecstack \
      -o /out/libtaskforge_sandbox.so sandbox_preload.c

FROM debian:bookworm-slim
WORKDIR /app
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl \
    ca-certificates \
    g++ make \
    freeglut3-dev libgl1-mesa-dev libglu1-mesa-dev mesa-utils \
    xvfb xauth xdotool x11-utils imagemagick openbox \
    fonts-dejavu-core \
  && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/image-runner /app/image-runner
COPY --from=build /out/taskforge_image_cpp_sandbox_guard.o /app/taskforge_image_cpp_sandbox_guard.o
COPY --from=build /out/libtaskforge_sandbox.so /app/libtaskforge_sandbox.so
COPY include /opt/taskforge/include
ENV HOME=/tmp
ENV IMAGE_RUNNER_KIND=cpp PORT=8000
EXPOSE 8000
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
USER 65534:65534
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=image-cpp-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=image-cpp-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/image-runner"]
