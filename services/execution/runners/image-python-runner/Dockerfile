FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/image-runner .

FROM debian:bookworm-slim
WORKDIR /app
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates \
    python3 python3-tk python3-matplotlib python3-pil python3-numpy \
    xvfb xauth xdotool x11-utils imagemagick openbox \
    fonts-dejavu-core \
  && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/image-runner /app/image-runner
COPY python_image_entry.py /opt/taskforge/python_image_entry.py
ENV HOME=/tmp
ENV IMAGE_RUNNER_KIND=python PORT=8000 MPLBACKEND=Agg MPLCONFIGDIR=/tmp/matplotlib PYTHONPYCACHEPREFIX=/tmp/pycache
EXPOSE 8000
ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
USER 65534:65534
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=image-python-runner\"; export TF_LOG_LEVEL=debug; export RUST_LOG=\"${RUST_LOG:-debug}\"; else echo \"[taskforge-debug] logs=off service=image-python-runner\"; export TF_LOG_LEVEL=info; export RUST_LOG=\"${RUST_LOG:-info}\"; fi; exec /app/image-runner"]
