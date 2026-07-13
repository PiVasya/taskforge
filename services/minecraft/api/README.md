# taskforge-minecraft-api

Микросервис Minecraft-интеграции TaskForge.

Сервис отвечает за:

- привязку Minecraft-аккаунтов;
- Minecraft-чат и SignalR-доступ для ролей `Minecraft`/`Admin`;
- отдельный Minecraft-баланс;
- идемпотентные расходы `death-chest` и `death-teleport`;
- долговечное состояние смертей, сундуков и оплаченных возвратов;
- компенсацию неоднозначных либо невыполненных платных операций.

База сервиса: `taskforge_minecraft`.

Схема создаётся и обновляется только EF Core migrations. В runtime-коде Minecraft-сервиса не используются `EnsureCreated`, ручные `CREATE TABLE` и `MinecraftSchemaCompatibility`.

## Миграции

Новые миграции не следует писать вручную. После изменения сущностей запускай из корня репозитория:

```bash
./scripts/generate-migrations.sh MinecraftDeathRecoveryAndEconomyCleanup
```

Перед отправкой в GitHub проверь созданный `Up()` и убедись, что он не удаляет действующие таблицы `MinecraftLinks` и `MinecraftChatMessages`.
