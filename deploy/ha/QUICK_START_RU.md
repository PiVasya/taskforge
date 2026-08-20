# TaskForge HA A/B — короткая инструкция

Схема:

```text
Cloudflare -> A (обычно)
             B (резерв)

A <-> WireGuard <-> B

PostgreSQL: текущий PRIMARY --async WAL--> STANDBY
MinIO:      A <----async bucket replication----> B
Redis:      локальный на каждом сервере
RabbitMQ:   локальный на каждом сервере
```

A — предпочтительный узел. B автоматически становится активным после безопасного fencing A. Когда A снова включён, он пересобирается как replica от B, догоняет данные и затем автоматически возвращается в PRIMARY через controlled failback.

## Что нужно подготовить

- два Linux-сервера A и B;
- публичные IP A и B;
- доступ root/sudo;
- Cloudflare Load Balancing;
- API хостера или другой независимый способ гарантированно выключить зависший/отрезанный сервер;
- для полного авто-возврата — API хостера для включения fenced-сервера обратно.

Без реального fencing автоматическое переключение двух серверов небезопасно при сетевом разделении. `HA_ALLOW_UNFENCED_FAILOVER=true` существует, но это сознательное принятие риска split-brain.

## 1. A: резервная копия

В каталоге production bundle:

```bash
./generate-secrets.sh --yes
./check.sh
./backup.sh --metadata
./backup.sh --database
```

Сохраните текущий `.env` A. Не создавайте на B отдельный набор общих TaskForge-секретов.

## 2. WireGuard

На A и B:

```bash
sudo ./ha/wireguard-generate.sh
```

Запишите публичные WireGuard-ключи.

На A:

```bash
sudo ./ha/wireguard-apply.sh \
  10.80.0.1 \
  B_WIREGUARD_PUBLIC_KEY \
  B_PUBLIC_IP:51820 \
  10.80.0.2
```

На B:

```bash
sudo ./ha/wireguard-apply.sh \
  10.80.0.2 \
  A_WIREGUARD_PUBLIC_KEY \
  A_PUBLIC_IP:51820 \
  10.80.0.1
```

Проверка:

```bash
# A
ping -c 3 10.80.0.2
sudo wg show wg-taskforge

# B
ping -c 3 10.80.0.1
sudo wg show wg-taskforge
```

Firewall:

- UDP 51820 — только A <-> B;
- TCP 5432 PostgreSQL — не открывать в Интернет;
- TCP 9000 MinIO — не открывать в Интернет;
- TCP 9187 HA health — разрешить Cloudflare health-check сетям и публичному IP второго сервера;
- обычные 80/443 — как и раньше для сайта.

## 3. Сделать текущий A primary

На A:

```bash
sudo ./ha/setup-primary.sh A 10.80.0.1 10.80.0.2 A_PUBLIC_IP B_PUBLIC_IP
```

Скрипт сохраняет существующую БД A, включает PostgreSQL WAL replication и пока оставляет `HA_AUTO_FAILOVER=false`.

## 4. Передать конфигурацию на B

На A:

```bash
./ha/make-peer-env.sh B 10.80.0.2 10.80.0.1 /tmp/taskforge-B.env
scp /tmp/taskforge-B.env USER@B_PUBLIC_IP:/tmp/taskforge.env
rm -f /tmp/taskforge-B.env
```

На B:

```bash
sudo install -m 600 /tmp/taskforge.env .env
sudo ./generate-secrets.sh --yes
./check.sh
```

## 5. Создать PostgreSQL standby B

На B:

```bash
sudo ./ha/setup-standby.sh --yes B 10.80.0.2 10.80.0.1 A_PUBLIC_IP B_PUBLIC_IP
```

Внимание: это специально заменяет локальный PostgreSQL volume B свежим `pg_basebackup` от A.

Проверить на обоих:

```bash
sudo ./ha/status.sh
```

Нормально:

```text
A: postgres_role=primary, traffic_ready=true
B: postgres_role=standby, standby_streaming=true, traffic_ready=false
```

## 6. MinIO A <-> B

Один раз на A после запуска MinIO B:

```bash
sudo ./ha/setup-minio-replication.sh --yes
```

Скрипт:

- откажется автоматически смешивать уже заполненный bucket B;
- сначала скопирует существующие файлы A -> B;
- включит versioning;
- создаст async replication A -> B и B -> A.

Проверить в тихий момент на каждом узле:

```bash
sudo ./ha/minio-wait-replication.sh 60
```

Должно закончиться `MinIO replication backlog is empty.`

При planned failover/failback HA agent сам останавливает writers и ждёт пустой backlog перед переключением. При внезапной смерти сервера это, естественно, невозможно: самые свежие ещё не реплицированные файлы могут быть потеряны.

## 7. TLS/DataProtection state

На A и B:

```bash
sudo ./ha/ssh-generate.sh
```

Публичный ключ A добавить на B:

```bash
sudo ./ha/ssh-authorize-peer.sh 'A_PUBLIC_KEY_LINE' 10.80.0.1
```

Публичный ключ B добавить на A:

```bash
sudo ./ha/ssh-authorize-peer.sh 'B_PUBLIC_KEY_LINE' 10.80.0.2
```

На A:

```bash
sudo ./ha/sync-shared-volumes.sh --force
```

Дальше это повторяет systemd timer текущего active node.

## 8. Fencing — обязательный кусок для безопасного AUTO failover

Скопируйте шаблон:

```bash
sudo cp ha/fence-provider.example.sh /root/taskforge-fence.sh
sudo chmod 700 /root/taskforge-fence.sh
```

Внутри вместо `exit 1` должен быть вызов API вашего VPS/hoster, который **выключает HA_TARGET_NODE_ID и подтверждает, что он больше не может писать**.

Для автоматического включения fenced-узла обратно:

```bash
sudo cp ha/recover-provider.example.sh /root/taskforge-recover.sh
sudo chmod 700 /root/taskforge-recover.sh
```

В recovery hook должен быть provider API power-on.

На обоих `.env`:

```env
HA_FENCE_SCRIPT=/root/taskforge-fence.sh
HA_RECOVER_SCRIPT=/root/taskforge-recover.sh
HA_ALLOW_UNFENCED_FAILOVER=false
```

Если `HA_RECOVER_SCRIPT` не настроен, failover всё равно автоматический, но выключенный старый сервер нужно включить вручную. После его включения rejoin/failback уже автоматические.

## 9. Cloudflare

Создать два origin/pool в порядке:

```text
A_PUBLIC_IP
B_PUBLIC_IP
```

Health monitor:

```text
protocol: HTTP
port: 9187
path: /ha/traffic-ready
expected status: 200
```

Приложение как и раньше обслуживается по HTTPS/443. Порт 9187 используется только для HA health.

До failover:

```text
A /ha/traffic-ready -> 200
B /ha/traffic-ready -> 503
```

После failover — наоборот. После успешного авто-failback A снова 200, B снова 503.

## 10. Финальная проверка и включение AUTO

На A и B:

```bash
sudo ./ha/preflight.sh
```

Потом на A и B:

```bash
sudo ./ha/enable-auto-failover.sh
```

Проверка:

```bash
sudo ./ha/status.sh
sudo systemctl status taskforge-ha
sudo journalctl -u taskforge-ha -f
```

## 11. Обязательный реальный тест

В maintenance window:

1. Убедиться, что A active, B streaming standby.
2. Сделать тестовые записи через сайт.
3. Убедиться, что provider fencing A реально работает.
4. Выключить/уронить A.
5. Проверить: B promoted, `/ha/traffic-ready` на B = 200, Cloudflare перевёл трафик.
6. Сделать новые записи и загрузить файл, пока B primary.
7. Дать recovery hook включить A либо включить A вручную.
8. Проверить: A сначала становится standby B и догоняет PostgreSQL.
9. Проверить лог MinIO drain во время failback.
10. Проверить: A снова primary/200, B standby/503.
11. Проверить новые записи и файл после возврата на A.

## Полезные команды

```bash
sudo ./ha/status.sh
sudo ./ha/preflight.sh
sudo ./ha/minio-wait-replication.sh 300
sudo systemctl restart taskforge-ha
sudo systemctl status taskforge-ha
sudo journalctl -u taskforge-ha -f
sudo wg show wg-taskforge
./ps.sh
./logs.sh
```

## Что происходит с транзакцией

```text
пользователь -> PRIMARY PostgreSQL -> локальный WAL flush -> SUCCESS
                                      |
                                      +---- async WAL ----> standby
```

Удалённую БД обычный `COMMIT` не ждёт. Поэтому сайт не получает WAN latency на каждую транзакцию. Цена — при мгновенной физической смерти primary последние WAL-записи, которые ещё не дошли до standby, могут потеряться. Это выбранная схема TaskForge HA.
