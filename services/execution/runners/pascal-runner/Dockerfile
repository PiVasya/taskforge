FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
COPY security/sandbox_guard.c ./
COPY security/sandbox_preload.c ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner . \
  && gcc -c -fPIC -O2 -Wall -Wextra -Werror -fvisibility=hidden -fno-stack-protector -fno-builtin \
      -o /out/taskforge_pascal_sandbox_guard.o sandbox_guard.c \
  && gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
      -Wl,-z,relro,-z,now,-z,noexecstack \
      -o /out/libtaskforge_sandbox.so sandbox_preload.c

FROM debian:bookworm-slim
ARG PABCNETC_URL=https://pascalabc.net/downloads/PABCNETC.zip
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    ca-certificates curl wget unzip p7zip-full \
    fpc util-linux \
    mono-complete libgdiplus \
    xvfb xauth xdotool x11-utils openbox imagemagick \
    fonts-dejavu-core libgtk2.0-0 \
  && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/pabcnetc \
  && (curl -fL --retry 8 --retry-delay 5 --retry-all-errors --connect-timeout 20 \
        -A "Mozilla/5.0" \
        -e "https://pascalabc.net/ssyilki-dlya-skachivaniya" \
        -o /tmp/PABCNETC.zip \
        "$PABCNETC_URL" \
      || curl -fL --retry 8 --retry-delay 5 --retry-all-errors --connect-timeout 20 \
        -A "Mozilla/5.0" \
        -e "https://pascalabc.net/ssyilki-dlya-skachivaniya" \
        -o /tmp/PABCNETC.zip \
        http://pascalabc.net/downloads/PABCNETC.zip \
      || wget --tries=8 --timeout=30 --waitretry=5 \
        --user-agent="Mozilla/5.0" \
        --referer="https://pascalabc.net/ssyilki-dlya-skachivaniya" \
        -O /tmp/PABCNETC.zip \
        "$PABCNETC_URL") \
  && (7z x -y -bd -o/opt/pabcnetc /tmp/PABCNETC.zip >/tmp/pabcnetc-extract.log \
      || unzip -q -O cp866 /tmp/PABCNETC.zip -d /opt/pabcnetc \
      || unzip -q /tmp/PABCNETC.zip -d /opt/pabcnetc) \
  && found="$(find /opt/pabcnetc -maxdepth 8 -iname 'pabcnetc.exe' -print -quit)" \
  && if [ -z "$found" ]; then echo "pabcnetc.exe was not found after extraction"; cat /tmp/pabcnetc-extract.log 2>/dev/null || true; find /opt/pabcnetc -maxdepth 4 -type f | sed -n '1,120p'; exit 1; fi \
  && target="/opt/pabcnetc/pabcnetc.exe" \
  && if [ "$found" != "$target" ]; then ln -sfn "$found" "$target"; fi \
  && test -f "$target" \
  && rm -f /tmp/PABCNETC.zip /tmp/pabcnetc-extract.log

WORKDIR /app
COPY --from=build /out/runner /app/runner
COPY --from=build /out/taskforge_pascal_sandbox_guard.o /app/taskforge_pascal_sandbox_guard.o
COPY --from=build /out/libtaskforge_sandbox.so /app/libtaskforge_sandbox.so
COPY security/TaskForgeSandboxGuard.pas /tmp/TaskForgeSandboxGuard.pas
RUN mkdir -p /opt/taskforge/pascal-guard /tmp/mono-registry/LocalMachine \
  && fpc -O2 -Fu/opt/taskforge/pascal-guard -FU/opt/taskforge/pascal-guard /tmp/TaskForgeSandboxGuard.pas \
  && chmod -R a-w /opt/taskforge/pascal-guard \
  && chmod 0444 /app/taskforge_pascal_sandbox_guard.o /app/libtaskforge_sandbox.so \
  && rm -f /tmp/TaskForgeSandboxGuard.pas \
  && test -x "$(command -v fpc)" \
  && test -x "$(command -v mono)" \
  && test -x "$(command -v Xvfb)" \
  && test -x "$(command -v xdotool)" \
  && test -x "$(command -v convert)" \
  && test -f /opt/pabcnetc/pabcnetc.exe

ENV RUNNER_KIND=pascal \
    PORT=8080 \
    HOME=/tmp \
    MONO_REGISTRY_PATH=/tmp/mono-registry \
    PABCNETC=/opt/pabcnetc/pabcnetc.exe
EXPOSE 8080 9090
USER 65534:65534
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=pascal-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=pascal-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; test -f \"${PABCNETC:-/opt/pabcnetc/pabcnetc.exe}\" || { echo \"PascalABC.NET compiler was not found\" >&2; exit 127; }; exec /app/runner"]
