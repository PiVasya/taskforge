# taskforge-python-runner

Regular code runner for Python submissions.

The service itself is written in Go. Python is present only as the language toolchain used to run submitted Python code (`python3 -I -B`). This keeps the backend runtime aligned with the rest of the lightweight runner services while preserving Python as a supported site language.

Endpoints:

- `GET /health`
- `POST /run`
- `POST /run/tests`
- `POST /run-tests` legacy alias
