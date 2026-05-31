# Dev Compose split

Dev compose is split by responsibility, same as production:

- `00-storage.yaml`
- `10-apps-gateway.yaml`
- `20-core-services.yaml`
- `30-execution.yaml`
- `40-ai-and-analyzers.yaml`
- `50-integrations.yaml`

Use from repo root:

```bash
cp deploy/dev/.env.example deploy/dev/.env
./deploy/dev/compose.sh up --build
```
