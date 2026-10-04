# Compiler and runner compatibility

TaskForge keeps one long-lived runner service per language family and exposes the legacy regular-code HTTP contract together with image/render endpoints where that language supports graphics.

`execution-worker` calls regular programming runners through:

```text
POST /run-tests
```

Every regular runner also exposes the legacy alias:

```text
POST /run/tests
```

Supported regular languages:

```text
cpp         -> cpp-runner
csharp      -> csharp-runner
java        -> java-runner
javascript  -> javascript-runner
pascal      -> pascal-runner -> Free Pascal
pascalabc   -> pascal-runner -> PascalABC.NET + Mono
python      -> python-runner
```

The execution path is:

```text
solutions-api -> execution-api -> execution-worker -> code-analyzer -> runner
```

Image/render execution is served by the same language-family containers instead of separate image-runner services:

```text
cpp-runner     -> /render
python-runner  -> /render
pascal-runner  -> /render (PascalABC.NET + GraphABC/X11)
```

For Pascal, one `pascal-runner` Docker image directly contains both complete toolchains and all graphics dependencies:

```text
Free Pascal
PascalABC.NET
Mono
GraphABC dependencies
Xvfb / Openbox / ImageMagick / xdotool
```

No separate `image-pascal-runner` image or running service is required by the active build/deployment path.

## Python submissions without Python backend service

Python remains a supported language for student submissions. The active `python-runner` service is written in Go and invokes `python3` as the student language toolchain. Python application runtime is intentionally limited to components that actually need it, such as `services/analyzers/image-analyzer`.
