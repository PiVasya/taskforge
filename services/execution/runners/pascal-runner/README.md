# pascal-runner

Unified TaskForge Pascal execution service.

One Docker image and one long-lived Go process contain every Pascal toolchain used by TaskForge:

- Free Pascal (`fpc`) for regular `pascal` code tasks;
- PascalABC.NET (`pabcnetc.exe` + Mono) for regular `pascalabc` code tasks;
- PascalABC.NET + GraphABC/X11 helpers for image/render tasks.

HTTP contract:

- `GET /health`
- `GET /ready`
- `POST /run`
- `POST /run/tests`
- `POST /run-tests`
- `POST /render`
- `POST /render/debug`

The image installs FPC, PascalABC.NET, Mono, Xvfb, Openbox, ImageMagick and the remaining render helpers directly. It does not inherit from, start, or require an `image-pascal-runner` image/service.
