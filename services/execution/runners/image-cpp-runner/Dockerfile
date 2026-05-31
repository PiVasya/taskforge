FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/image-runner .

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
ENV IMAGE_RUNNER_KIND=cpp PORT=8000
EXPOSE 8000
ENTRYPOINT ["/app/image-runner"]
