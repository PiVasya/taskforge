# Compiler and runner compatibility

This microservice cut keeps the legacy runner HTTP contract for regular programming tasks.

`execution-worker` calls runners through:

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
pascal      -> pascal-runner
python      -> python-runner
```

The old monolith configured regular compiler services as runner base URLs such as `http://cpp-runner:8080`, `http://csharp-runner:8080`, `http://python-runner:8080` and then called `/run/tests`. The new execution path preserves this contract but puts it behind a durable queue:

```text
solutions-api -> execution-api -> execution-worker -> code-analyzer -> runner
```

Image tasks are separate from regular code-test tasks. The active microservice runtime supports:

```text
image-cpp-runner
image-pascal-runner
```

The legacy `image-python-runner` is not part of the active runtime, so the web solve page limits image-test language selection to C++ and Pascal.

## Python submissions without Python backend service

Python remains a supported language for student submissions. The active `python-runner` service is written in Go and only invokes `python3` as the language toolchain for submitted code. Python application runtime is intentionally limited to `services/analyzers/image-analyzer`.
