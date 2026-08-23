# Добавление узлов

Новый узел не получает private key другого сервера. Он создаёт собственный key и публичный descriptor через `prepare-node`.

Порядок:

1. `bootstrap` и `prepare-node` на новом сервере.
2. `add-node` на A.
3. Передать один публичный `taskforge-cluster-topology.json` на A/B/C/D.
4. `apply NODE_ID` на существующих узлах — обновляет WireGuard peers, UFW и доступ PostgreSQL/MinIO.
5. `join NODE_ID --yes` на новом узле.

Inventory можно расширять до D/E. Только A/B/C обычно отмечаются `quorum_voter`; D/E остаются полноценными failover candidates без изменения размера etcd quorum.
