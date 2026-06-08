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

| Language | Frontend value | Runner service | Runner endpoint |
|---|---|---|---|
| C# | `csharp` | `csharp-runner` | `/run-tests` |
| C++ | `cpp` | `cpp-runner` | `/run-tests` |
| Java | `java` | `java-runner` | `/run-tests` |
| JavaScript | `javascript` | `javascript-runner` | `/run-tests` |
| Pascal | `pascal` | `pascal-runner` | `/run-tests` |
| Python | `python` | `python-runner` (Go service, runs `python3` toolchain) | `/run-tests` |

Image tasks intentionally use only image runners:

```text
image-cpp-runner
image-pascal-runner
```

## Analyzer policy

Before any runner is called, `execution-worker` calls `code-analyzer` when `CODE_ANALYZER_ENABLED=true`.

The analyzer checks:

- global dangerous patterns;
- language-specific forbidden calls;
- assignment-specific `codeForbiddenCalls`;
- assignment-specific `codeRequiredCalls`;
- Cyrillic characters in executable code.

Cyrillic is allowed in strings and comments, but forbidden in executable code/identifiers for all regular languages. This prevents visually confusing identifiers such as Cyrillic `а`, `с`, `о`, `р`, `е`, `х` mixed with Latin code.

If analyzer is unavailable and `CODE_ANALYZER_FAIL_CLOSED=true`, the verdict is `JudgeUnavailable` instead of running unvalidated code.
