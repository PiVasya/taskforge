FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/runner .

FROM eclipse-temurin:21-jdk-jammy
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/runner /app/runner
ENV RUNNER_KIND=java PORT=8080
EXPOSE 8080
USER 65534
ENTRYPOINT ["/app/runner"]
