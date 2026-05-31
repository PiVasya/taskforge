# image-pascal-runner

Lightweight Go HTTP image runner.

No Python web server and no Node.js wrapper are used here. It keeps the legacy image-runner contract:

- GET /health
- GET /ready
- POST /render
- POST /render/debug
