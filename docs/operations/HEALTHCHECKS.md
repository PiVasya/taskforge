# K3s: проверки работоспособности

Для локальной Kubernetes-ветки используется namespace `taskforge-k3s-lab`.
Проверить фактические статусы, readiness и одноразовые миграционные Job:

```bash
kubectl -n taskforge-k3s-lab get deploy,pods,svc,jobs
kubectl -n taskforge-k3s-lab-db get pods,pvc
kubectl -n taskforge-k3s-lab logs job/migrate-tasks-api-lab1 --tail=200
curl -i http://127.0.0.1:18080/health/gateway
```

Эти проверки требуют предварительного запуска локального K3s и port-forward.
Манифесты и probes смотри в `tools/k3s/manifest.mjs` и генераторе
`tools/k3s/local/generate.mjs`. Документация по запуску:
`docs/operations/README_K3S_BRANCH.md`.
