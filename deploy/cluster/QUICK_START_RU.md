# Короткий старт v32

> `chmod +x` вручную не нужен. Все команды ниже запускаются через `bash ./cluster.sh`; launcher сам восстанавливает executable bits у всех `*.sh`.


## Уже настроенные A/B

Не копируйте `.env`, ключи и зависимости вручную:

```bash
bash ./cluster.sh migrate-local A --from /path/to/old-server-folder
bash ./cluster.sh migrate-local B --from /path/to/old-server-folder
```

Команда сама устанавливает отсутствующие зависимости и подхватывает существующее состояние.

Проверка:

```bash
bash ./cluster.sh status
bash ./cluster.sh doctor
```

## Новый C/D

На новом узле:

```bash
bash ./cluster.sh prepare-node --node C --public-ip IP --wg-ip 10.80.0.3 --priority 80 --quorum-voter
```

Host dependencies и Docker при необходимости устанавливаются автоматически.

Descriptor передаётся на A:

```bash
bash ./cluster.sh add-node taskforge-node-C.json
```

Общий topology-файл передаётся на все узлы:

```bash
bash ./cluster.sh import-topology taskforge-cluster-topology.json
bash ./cluster.sh apply NODE_ID
```

На C:

```bash
bash ./cluster.sh join C --yes
```
