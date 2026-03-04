# TaskForge × Minecraft (Folia) plugin

Плагин для **Folia** (Paper/Folia API), который:

1) Принимает HTTP запрос от TaskForge и **отправляет игроку код привязки** в личные сообщения (игрок должен быть online).
2) (Опционально) на `join` и `death` запрашивает у TaskForge статус игрока (`debuffed`) и **вешает/снимает** эффекты.

## Как подключить

1. Собрать jar:

```bash
cd minecraft-plugin-folia/TaskForgeFoliaPlugin
./gradlew build
```

Готовый jar будет в `build/libs/`.

2. Положить jar в папку `plugins/` на Folia сервер.

3. Настроить `plugins/TaskForgeLink/config.yml`:

- `http.host`, `http.port`, `http.path` — где слушать webhook от TaskForge.
- `security.taskForgeKey` — общий секрет (TaskForge должен слать в `X-TaskForge-Key`).
- `security.allowedIps` — whitelist IP (не обязательно).
- `taskforge.apiBaseUrl` — URL TaskForge API (если хочешь включить дебафы).
- `taskforge.minecraftKey` — ключ для заголовка `X-Minecraft-Key` (плагин->TaskForge).

## Webhook (TaskForge -> plugin)

`POST http://<server>:<port>/taskforge/link/send`

Headers:
- `X-TaskForge-Key: <секрет>`
- `X-Request-Id: <uuid>`

Body:
```json
{ "nick":"Player", "code":"ABCD-EFGH", "ttlSeconds":600, "siteName":"TaskForge" }
```

Ответ:
- 200 `{ delivered:true, uuid:"..." }` если игрок online
- 404 `{ delivered:false, reason:"offline" }` если игрок offline

## Debuff pull (plugin -> TaskForge)

Плагин (если задан `taskforge.apiBaseUrl`) дергает:

- `POST /api/integrations/minecraft/events/join` с `{ nick, uuid }` (списание недели 1 раз в неделю)
- `GET /api/integrations/minecraft/player-status?nick=...&uuid=...` (проверка без списания)

Ожидаемый ответ JSON:
```json
{ "debuffed": true }
```

