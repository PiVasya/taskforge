# No Python runtime policy

В runtime-сервисах Python запрещён, кроме:

```text
services/analyzers/image-analyzer
```

Удалены/не перенесены как runtime:

```text
python-runner
image-python-runner
taskforge-ai-worker-external
legacy python AI worker
```

В `extracted/` могут встречаться старые строки/комментарии из монолита. Это исходники для ручного переноса логики, а не активная runtime-схема.
