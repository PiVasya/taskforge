# TaskForge SQL worker (Go)

Coordinator and isolated helper are the same native executable. Go owns orchestration
and validation; CGO binds libpq, MariaDB Connector/C and SQLite. This component does
not run Python and does not start a database server per attempt.

Read docs/sql/GO_RUNTIME.md at the repository root.
The full project entrypoint is 00_AI_READ_THIS_FIRST.md. Keep C# wire contracts and
user-owned migrations unchanged.

From the repository root: bash ./scripts/check-sql-go.sh
Real production-image/server gate: bash ./scripts/sql/test-engines.sh

Commands: sql-worker (serve), sql-worker child (private stdin contract),
sql-worker health, sql-worker version, sql-worker self-test-isolation.
Docker builds the release target by default; the integration target is test-only.
No .NET migration or production database is touched by these test commands.
