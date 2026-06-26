# image-pascal-runner

Lightweight Go HTTP image runner for Pascal graphics tasks.

The regular `Dockerfile` intentionally does not download PascalABC.NET from `pascalabc.net` during every CI build. GitHub Actions sometimes cannot resolve that host, so normal rebuilds reuse the already published `ghcr.io/pivasya/taskforge/image-pascal-runner:develop` image as a compiler/runtime base and only replace the small Go runner binary.

Use `Dockerfile.bootstrap` only for a rare manual bootstrap when the PascalABC.NET runtime itself must be refreshed.

Endpoints:

- GET /health
- GET /ready
- POST /render
- POST /render/debug
