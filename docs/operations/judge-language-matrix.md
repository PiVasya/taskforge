# Judge language matrix

Обычные code-test задания проходят единый путь:

```text
frontend submit
  -> solutions-api
  -> tasks-api internal judge-spec
  -> execution-api durable job
  -> execution-worker
  -> code-analyzer
  -> language runner
  -> solutions-api verdict
  -> frontend polling
```

Поддерживаемые языки обычных задач:

| Language | Frontend value | Runtime/toolchain | Runner service | Runner endpoint |
|---|---|---|---|---|
| C# | `csharp` | .NET/C# | `csharp-runner` | `/run-tests` |
| C++ | `cpp` | GCC | `cpp-runner` | `/run-tests` |
| Java | `java` | Java | `java-runner` | `/run-tests` |
| JavaScript | `javascript` | JavaScript runtime | `javascript-runner` | `/run-tests` |
| Pascal | `pascal` | Free Pascal (`fpc`) | `pascal-runner` | `/run-tests` |
| PascalABC.NET | `pascalabc` | `pabcnetc.exe` + Mono | `pascal-runner` | `/run-tests` |
| Python | `python` | `python3` | `python-runner` | `/run-tests` |

Image tasks use the same language-family runners through `/render` rather than separate long-lived image-runner containers:

```text
cpp-runner     -> /render
python-runner  -> /render
pascal-runner  -> /render
```

For Pascal graphics, `pascal-runner` uses the PascalABC.NET toolchain and GraphABC/X11 helpers. The same Docker image also contains Free Pascal for `pascal` code tasks and PascalABC.NET for ordinary `pascalabc` code tasks.

## Analyzer policy

Before any runner is called, `execution-worker` calls `code-analyzer` when `CODE_ANALYZER_ENABLED=true`.

The analyzer checks:

- global dangerous patterns;
- language-specific forbidden calls;
- assignment-specific `codeForbiddenCalls`;
- assignment-specific `codeRequiredCalls`;
- Cyrillic characters in executable code where that language policy forbids them.

If analyzer is unavailable and `CODE_ANALYZER_FAIL_CLOSED=true`, the verdict is `JudgeUnavailable` instead of running unvalidated code.
