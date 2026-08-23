# Переход с уже настроенных A/B на v32

Новый архив распаковывается **в отдельную папку**. Старую папку не перезаписываем и не удаляем, пока новый менеджер не покажет здоровое состояние.

Рекомендуемый переход теперь выполняется одной командой. Менеджер сам:

- установит отсутствующие host dependencies (`socat`, WireGuard tools, `jq`, `curl` и т.д.);
- установит Docker, если его нет;
- выставит постоянное членство пользователя в группе `docker`;
- перенесёт `.env` и `config.json`;
- найдёт Code Analyzer keys даже если старый `.env` ссылается на ещё более старую папку;
- скопирует ключи внутрь новой `.runtime`;
- перепишет абсолютные пути в `.env`;
- исправит владельцев и права;
- подхватит существующие WireGuard/PostgreSQL/MinIO без пересоздания data volumes;
- определит роль PostgreSQL и выполнит `adopt`.

На A:

```bash
bash ./cluster.sh migrate-local A --from ~/Desktop/taskforge-prod-linux-v27-n-node-cluster
```

Если фактические ключи/`.env` живут в другой старой папке, укажите именно её. Команда также умеет следовать абсолютным путям `CODE_ANALYZER_*_KEY_PATH` из старого `.env`.

На B:

```bash
bash ./cluster.sh migrate-local B --from ~/taskforge-prod-linux-v27-n-node-cluster
```

Если `.env` и ключи уже были вручную перенесены в v32, достаточно:

```bash
bash ./cluster.sh adopt A   # A
bash ./cluster.sh adopt B   # B
```

После перехода:

```bash
bash ./cluster.sh status
bash ./cluster.sh doctor
```

`adopt`/`migrate-local` не выполняют `docker compose down -v`, не удаляют PostgreSQL/MinIO volumes и не запускают повторный `pg_basebackup` для уже работающей streaming standby.

> После первой установки Docker Linux применяет новую supplementary group `docker` к новым login-сессиям. Менеджер добавляет пользователя в группу автоматически и навсегда; если Docker был установлен только что, один раз переподключитесь по SSH/VS Code перед использованием `docker` **без sudo**. Для самих root cluster-команд это не требуется.
