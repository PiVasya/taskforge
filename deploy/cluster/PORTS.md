# Порты и firewall

v39 автоматически устанавливает/включает UFW при `apply/adopt/join/repair/upgrade`.
Правила добавляются до `ufw enable`, поэтому активный SSH port не должен быть
отрезан.

Публично нужны:

- SSH/tcp — обнаруженный effective/current sshd port;
- HTTP/HTTPS узла — по умолчанию только от Cloudflare proxy CIDR;
- WireGuard UDP — только от public IP других нод из inventory.

Через `wg-taskforge` между peers доступны:

- PostgreSQL cluster port (default 5432/tcp);
- MinIO cluster port (default 9000/tcp);
- cluster health port (default 9187/tcp);
- Patroni/etcd control ports после включения quorum mode.

MinIO console и data/control ports не должны слушать `0.0.0.0`; `doctor`
проверяет wildcard exposure для текущих inventory ports.

Для origin, который намеренно должен принимать web traffic напрямую:

```bash
TASKFORGE_FIREWALL_WEB_SOURCE=any bash ./cluster.sh repair NODE_ID
```
