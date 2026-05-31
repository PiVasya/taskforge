# cpp-runner

Lightweight Go HTTP wrapper for the cpp-runner execution environment.

The service intentionally does not use Python or Node.js as the web server runtime.
It keeps the legacy TaskForge HTTP contract:

- GET /health
- GET /ready
- POST /run
- POST /run/tests
- POST /run-tests
