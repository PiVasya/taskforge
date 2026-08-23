# Recovery

Сначала:

```bash
bash ./cluster.sh status
bash ./cluster.sh doctor
```

Безопасное восстановление служебной конфигурации:

```bash
bash ./cluster.sh repair
```

`repair` не удаляет volumes.

Для неудачного первого `join` непустой PostgreSQL volume автоматически не перезаписывается. `--reset-data` допускается только если на этом узле нет уникальных данных и он точно является недоделанной replica.

При разошедшихся timelines не копируйте PGDATA вручную. В replica-mode заново присоедините узел через контролируемый `join --reset-data`; в quorum-mode используйте Patroni rejoin/rewind или новый basebackup.

## Ручной failover в двухузловом режиме

Только после физического выключения/изоляции старого primary:

```bash
bash ./cluster.sh promote --old-primary A --confirm-old-primary-off
```

После этого старый A нельзя просто включить writable. Его присоединяют к B заново:

```bash
bash ./cluster.sh join A --primary B --yes --reset-data
```

`--reset-data` здесь означает сознательную замену разошедшейся старой копии A новым basebackup с B. Перед командой убедитесь, что уникальные записи уже находятся на B.
