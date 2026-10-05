FROM golang:1.23-bookworm AS build-runner
WORKDIR /src
COPY go.mod ./
COPY *.go ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner .

FROM golang:1.23-bookworm AS build-driver
WORKDIR /src
COPY driver/go.mod ./
COPY driver/*.go ./
RUN mkdir -p /out \
  && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/driver .

FROM debian:bookworm-slim
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
       bash ca-certificates curl fontconfig \
       libglib2.0-0 libgsf-1-114 libgtk-3-0 \
       libx11-6 libxext6 libxrender1 libxtst6 \
       unixodbc xauth xvfb \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build-runner /out/runner /app/runner
COPY --from=build-driver /out/driver /opt/taskforge/onec/driver
COPY bootstrap.sh /opt/taskforge/onec/bootstrap
COPY judge-src /opt/taskforge/onec/judge-src
RUN chmod 0755 /app/runner /opt/taskforge/onec/driver /opt/taskforge/onec/bootstrap \
  && find /opt/taskforge/onec/judge-src -type d -exec chmod 0755 {} + \
  && find /opt/taskforge/onec/judge-src -type f -exec chmod 0644 {} +
ENV HOME=/tmp PORT=8080 \
    ONEC_DRIVER=/opt/taskforge/onec/driver \
    ONEC_EXECUTABLE=/opt/1cv8/taskforge/1cv8 \
    ONEC_TEMPLATE_DIR=/opt/taskforge/onec/runtime/templates/code \
    ONEC_JUDGE_SOURCE_DIR=/opt/taskforge/onec/judge-src \
    ONEC_WORK_ROOT=/work \
    ONEC_USE_XVFB=true \
    ONEC_UI_LANGUAGE=ru
EXPOSE 8080
USER 65534:65534
ENTRYPOINT ["/app/runner"]
