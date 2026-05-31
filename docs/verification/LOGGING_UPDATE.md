# Logging update

Added startup log capture commands to both compose wrappers:

- `./deploy/dev/compose.sh up-logs --build`
- `./deploy/prod/compose.sh up-logs`
- `logs-startup <seconds>`
- `logs-dump <tail>`

Logs are stored under:

- `deploy/dev/logs/<timestamp>/`
- `deploy/prod/logs/<timestamp>/`

Also fixed the dev AI worker environment so it uses `TaskForgeInternalApi__BaseUrl=http://ai-api:8080` instead of falling back to the old default `http://taskforge:8080`.
