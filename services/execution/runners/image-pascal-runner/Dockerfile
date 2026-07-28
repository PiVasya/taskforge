# PASCAL_IMAGE_BASE must be declared before the first FROM. Docker only allows
# globally-scoped ARG values to be used in FROM instructions across stages.
ARG PASCAL_IMAGE_BASE=ghcr.io/pivasya/taskforge/image-pascal-runner:develop

FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
COPY security/sandbox_preload.c ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/image-runner .
RUN gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
      -Wl,-z,relro,-z,now,-z,noexecstack \
      -o /out/libtaskforge_sandbox.so sandbox_preload.c

# The PascalABC.NET compiler bundle is huge and pascalabc.net is not reliable from
# GitHub Actions runners. The normal image rebuild therefore reuses the already
# published Pascal image as a stable compiler/runtime base and only replaces the
# small Go HTTP runner binary.
#
# If the base image ever needs to be bootstrapped from PascalABC.NET again, use
# Dockerfile.bootstrap manually. Do not make the regular CI path depend on
# pascalabc.net DNS/download availability.
FROM ${PASCAL_IMAGE_BASE}

WORKDIR /app
COPY --from=build /out/image-runner /app/image-runner
COPY --from=build /out/libtaskforge_sandbox.so /app/libtaskforge_sandbox.so

ENV MONO_REGISTRY_PATH=/tmp/mono-registry \
    PABCNETC=/opt/pabcnetc/pabcnetc.exe \
    HOME=/tmp \
    IMAGE_RUNNER_KIND=pascal \
    PORT=8000

EXPOSE 8000
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
USER 65534:65534
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=image-pascal-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=image-pascal-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; test -f \"${PABCNETC:-/opt/pabcnetc/pabcnetc.exe}\" || { echo \"PascalABC.NET compiler was not found at ${PABCNETC:-/opt/pabcnetc/pabcnetc.exe}\" >&2; exit 127; }; exec /app/image-runner"]
