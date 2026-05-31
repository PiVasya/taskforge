# Production Compose split

Production compose intentionally uses several small files instead of one huge file:

- `00-storage.yaml` — PostgreSQL, RabbitMQ, MinIO.
- `10-apps-gateway.yaml` — gateway, main web app, CT web app.
- `20-core-services.yaml` — identity, education, content, task catalog, quiz, solutions, rating worker.
- `30-execution.yaml` — execution API, execution worker, code/image runners.
- `40-ai-and-analyzers.yaml` — AI API, AI worker, code analyzer, image analyzer.
- `50-integrations.yaml` — support, Minecraft, files, notifications, observability, bots.
- `90-certbot.yaml` — optional certbot profile.

Use the wrapper from the repository root:

```bash
cp deploy/prod/.env.example deploy/prod/.env
./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up -d
```

The wrapper passes all compose fragments in the correct order and uses `deploy/prod/.env` by default.
