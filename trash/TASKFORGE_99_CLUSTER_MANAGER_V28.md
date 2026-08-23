# TaskForge Cluster Manager v28

## Цель

Убрать ручную настройку двух- и N-node инфраструктуры. Пользователь работает через `deploy/prod/cluster.sh`; PostgreSQL/MinIO/WireGuard details остаются внутри менеджера.

## Основные изменения

- отдельный публичный inventory вместо HA-переменных в `.env`;
- `replica` mode для A/B без ложного двухузлового quorum;
- переход к Patroni/etcd `quorum` mode после появления трёх независимых voters;
- one-command bootstrap/adopt/join/status/doctor/repair;
- автоматический WireGuard full mesh из inventory;
- автоматические UFW rules только между peers;
- systemd/socat proxies позволяют разным локальным PostgreSQL/MinIO ports использовать стабильные WireGuard ports;
- автоматический `pg_basebackup`, physical slot и проверка `streaming / async`;
- MinIO N-way replication с versioning, delete markers и двусторонним probe;
- автоматический выбор MinIO `-cpuv1` на старом CPU;
- RabbitMQ и Redis остаются локальными, без WAN cluster;
- импорт секретов больше не оставляет `.runtime` root-owned;
- экспортированный encrypted secrets bundle принадлежит пользователю, а не root;
- normal updates вызывают safe repair, чтобы восстановить proxy/CPU profile после container recreate.

## Безопасность данных

Менеджер не содержит команд `down -v`, `volume prune` или безусловного удаления named volumes. Непустой PostgreSQL volume нельзя перезаписать без явного `--reset-data`, и этот флаг предназначен только для недоделанной replica без уникальных данных.

## Deployment-specific topology

The source repository keeps placeholders instead of publishing static IPs or WireGuard public keys. The generated server bundle can be customized for the operator's already-existing A/B topology without committing those values to Git.
