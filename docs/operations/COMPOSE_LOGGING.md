# Compose logging helper

`deploy/dev/compose.sh` and `deploy/prod/compose.sh` wrap Docker Compose and keep normal Compose commands working.

## Recommended dev start with first startup logs saved

```bash
./deploy/dev/compose.sh down --remove-orphans
./deploy/dev/compose.sh up-logs --build
```

This starts the stack in detached mode and captures the first 30 seconds of logs from all services into:

```text
deploy/dev/logs/<utc-timestamp>/startup-30s.log
```

It also saves:

```text
deploy/dev/logs/<utc-timestamp>/ps.txt
```

## Change capture duration

```bash
TASKFORGE_STARTUP_LOG_SECONDS=60 ./deploy/dev/compose.sh up-logs --build
```

## Capture logs from already running stack

```bash
./deploy/dev/compose.sh logs-startup 30
```

## Dump last log lines without following

```bash
./deploy/dev/compose.sh logs-dump 1000
./deploy/dev/compose.sh logs-dump 300 ai-worker
```

## Follow logs normally, but with a limited tail

```bash
./deploy/dev/compose.sh logs -f --tail=200
```

## Production

Production has the same commands:

```bash
./deploy/prod/compose.sh up-logs
./deploy/prod/compose.sh logs-startup 30
./deploy/prod/compose.sh logs-dump 1000
```

Production logs are saved under:

```text
deploy/prod/logs/<utc-timestamp>/
```

## Why use `up-logs` instead of `up`

`docker compose up` attaches to every service and floods the terminal. `up-logs` starts services with `up -d`, captures the first startup window into a file, and then returns control to the terminal.
