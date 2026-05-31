# Execution service

Запуск кода теперь выделен в отдельный домен.

- `api` владеет БД `taskforge_execution`, jobs/results/runner-heartbeats.
- `worker` должен читать очередь `ExecutionRequested` и вызывать нужный runner.
- `runners/*` stateless, без БД.

API задач/решений не должны ходить напрямую в `cpp-runner` / `java-runner` / `pascal-runner`.
Они создают execution job, а дальше работает queue + worker.
