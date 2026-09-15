# TaskForge testing

TaskForge tests are split by what they prove. A test should run the production code of the layer it protects; static source checks are reserved for repository boundaries that cannot be expressed as runtime behavior.

For a machine with .NET, Node, Go and Docker available, the full local equivalent of the CI gates is:

```bash
bash scripts/tests/all.sh
```

The full runner attempts every major suite even when an earlier suite fails, then prints one consolidated failure summary. The .NET runner likewise builds every production service and executes every discovered test project before returning failure. This makes one run useful for diagnosis instead of revealing one project at a time. Nested `tests/` trees are excluded from production SDK default items so test `bin/obj` output can never be copied recursively into an API build.

Use the layer-specific commands below when iterating on one part of the project.

## 1. .NET production build and behavior tests

Command:

```bash
bash scripts/tests/dotnet.sh
```

The script first discovers every production `*.csproj` below `services/` (excluding `tests/`) and runs `dotnet build -c Release` for all of them. It then discovers every `*Tests.csproj` and runs every test project with `dotnet test -c Release`. Build and test failures are collected and reported together so one CI run exposes all .NET compile/test failures before any Docker image push starts.

Current service tests cover, among other things:

- Tasks API route authorization, AI resource policy, test-answer compatibility and course-map progression.
- Actual debug middleware streaming behavior for NDJSON.
- Solutions API result classification and actual SSE streaming through debug middleware.
- Identity JWT, bootstrap-role and password-hash behavior.
- AI assignment payload secrecy.
- Execution worker runner-outage classification, retry status classification and diagnostic sanitization.
- Existing AI-agent worker tests.

These tests replace the old practice of proving C# behavior with exact source-string matches.

Test projects that load production assemblies with EF Core keep the same explicit EF Core/Relational runtime version as those assemblies, so tests do not silently execute against an older transitive provider version.

Behavior tests should assert the contract that matters, not incidental presentation. For example, a security redaction test must prove that a private filesystem path cannot leak; it should not require one exact placeholder spelling or decide whether a harmless filename suffix is preserved unless that exact text is itself a documented user-facing contract.

## 2. Frontend behavior and build

Command:

```bash
bash scripts/tests/frontend.sh
```

It runs architecture checks, dependency-free model tests, Jest/jsdom tests and a production `CI=true` build. The logout privacy regression is covered here so a shared browser cannot retain another user's solution draft.

## 3. Security suites

OJ:

```bash
bash scripts/security/check-oj-security.sh
```

Browser API:

```bash
bash scripts/security/check-browser-api-security.sh
```

Security scripts may contain static architecture assertions where the property is itself structural, such as container hardening, secret mounting, CORS boundaries or forbidden runtime dependencies. Behavioral policies should live in executable tests.

## 4. SQL runtime

Fast Go/native checks:

```bash
bash scripts/check-sql-go.sh
```

SQL domain/model and immutable migration boundary:

```bash
bash scripts/check-sql-update.sh
```

Real Docker PostgreSQL/MySQL/RabbitMQ integration:

```bash
bash scripts/sql/test-engines.sh
```

The real-engine gate is authoritative for provider lifecycle, timeout/cancellation and recovery behavior. It must not be replaced with source greps or mocked-only tests. When `scripts/tests/all.sh` is used, the host-native Go/SQLite sub-gate inside `check-sql-update.sh` is intentionally not repeated: the immediately following Docker real-engine suite builds the same Go runtime with its pinned native development libraries and runs the stronger SQLite/PostgreSQL/MySQL/RabbitMQ gate. Running `check-sql-update.sh` by itself still requires the documented host native development packages.

## 5. Repository/release boundaries

Command:

```bash
bash scripts/tests/repository.sh
```

This layer checks things that are intentionally static: workflow/Docker matrix alignment, migration tooling safety and cluster release structure. It must not assert local variable names or implementation formatting inside business code.


## Regression ownership examples

| Regression | Canonical test | Why |
| --- | --- | --- |
| SSE/NDJSON accidentally buffered by debug logging | .NET middleware behavior test | Proves bytes reach the real response body before the request completes. |
| Another user sees a previous user's local editor draft after logout | Jest/jsdom browser-state test | Proves persisted and in-memory solution state is actually erased while UI preferences survive. |
| Admin live-solution feed filters/reconnect client | Node model tests + isolated Jest EventSource transport test | Tests filtering as pure logic and the browser transport without importing Axios/API clients into the transport test. |
| Runner outage classified as a learner error | Execution-worker xUnit tests + OJ security suite | Exercises production classification/sanitization instead of searching for marker strings. |
| SQL provider timeout/recovery/materialization | Go race tests + real Docker PostgreSQL/MySQL gate | Provider lifecycle behavior must be proven against real engines. |
| Workflow/image matrix, migration boundary, Compose hardening | repository/static guards | These are structural contracts where configuration text itself is the behavior. |

## CI ownership

The normal and full-rebuild workflows keep separate jobs for repository boundaries, .NET production build + behavior tests, frontend behavior/build, OJ security, Browser security, SQL runtime/integration and Compose validation. Docker image builds depend on all of them, so a C# compile failure is reported before image build/push jobs start.

`scripts/ci/check-csharp-source-invariants.py` and `scripts/ci/check-authoring-regressions.py` remain only as compatibility entrypoints for old local commands/source-retention. Active CI does not use either as a source-grep policy engine.
