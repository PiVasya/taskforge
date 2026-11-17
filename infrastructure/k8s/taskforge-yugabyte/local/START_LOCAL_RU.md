# TaskForge K3s LAB — отдельная ветка, новый CI/CD, YugabyteDB RF1

Актуальная инструкция **`README_K3S_BRANCH.md` в корне новой ветки**.

Доступ: `http://127.0.0.1:18080` через `port-forward.sh`.
Namespace приложения: `taskforge-k3s-lab`; namespace БД: `taskforge-k3s-lab-db`.
Секреты: `~/.config/taskforge-k3s-lab/credentials.env`.
Нужны `KUBECONFIG` отдельного локального K3s и `TASKFORGE_LOCAL_K3S_ACK=local-development-only`.

```bash
bash infrastructure/k8s/taskforge-yugabyte/local/build-images.sh "$PWD" core
bash infrastructure/k8s/taskforge-yugabyte/local/run-local.sh "$PWD" core
bash infrastructure/k8s/taskforge-yugabyte/local/port-forward.sh
```

**Старый PostgreSQL/действующий сервер не участвует.** K3s локальный, RF1; кодовые раннеры пока не включены.
