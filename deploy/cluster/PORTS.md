# Порты

Публично нужны только:

- HTTP/HTTPS узла — индивидуальные порты разрешены;
- WireGuard UDP — индивидуальный порт разрешён;
- SSH по политике администратора.

Через `wg-taskforge` доступны:

- PostgreSQL cluster port 5432/tcp;
- MinIO cluster port 9000/tcp;
- health port 9187/tcp;
- Patroni/etcd только после quorum-mode.

Локальные порты могут отличаться. Менеджер создаёт proxy от WireGuard cluster port к loopback local port.
