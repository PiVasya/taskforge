# Python runtime boundary policy

Python не используется для AI/core backend runtime-сервисов. Разрешены только два изолированных случая:

```text
services/analyzers/image-analyzer
services/execution/runners/python-runner
```

`python-runner` — это не application backend, а sandbox runner для шестого языка решений. Он вызывается только через `execution-worker` и получает уже проверенный job payload.

Не разрешены как application runtime:

```text
taskforge-ai-worker-external
legacy python AI worker
python backend монолита
image-python-runner
```

Миграционные снапшоты старого монолита не хранятся в активном дереве сервисов, поэтому Python-runtime исключения проверяются только по реальным сервисам и runners.
