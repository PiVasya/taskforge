# TaskForge 1C runner

`onec-runner` is a normal TaskForge CI image and the execution boundary for
`language=onec`. Unlike C++/Python, the proprietary 1C platform is not baked into
the image: a one-shot server bootstrap installs it into a persistent Docker
volume. Rebuilding/updating the runner therefore does not re-download 1C.

## Runtime layout

```text
onec-runner image (GitHub CI)
  runner + trusted driver + Judge XML/BSL skeleton + bootstrap

persistent onec-platform volume
  /opt/1cv8/... + /opt/1cv8/taskforge/1cv8

persistent onec-runtime volume
  /opt/taskforge/onec/runtime/templates/code/1Cv8.1CD

host bind
  /var/lib/taskforge/onec-work -> /work (ephemeral submission copies)
  /etc/taskforge/onec/licenses -> /var/1C/licenses:ro
```

The bootstrap can obtain the official Linux `.run` installer from a mounted
local file or an arbitrary URL. The first installation requires an exact
`ONEC_PLATFORM_VERSION` and `ONEC_INSTALL_SHA256`. A marker inside the platform
volume makes later migrations idempotent; the cached installer lives in a third
persistent volume.

The bootstrap is allowed normal network access only while preparing the volumes.
The long-lived `onec-runner` remains attached exclusively to the internal
`onec-runner-net` and mounts the platform/runtime volumes read-only.

## `onec-code` execution contract

Initial code tasks implement one exported function:

```1c
Функция Решение(Вход) Экспорт
    Возврат Вход;
КонецФункции
```

For every submission the trusted driver:

1. copies the immutable file-infobase template to `/work/job-*`;
2. copies the trusted external-data-processor XML skeleton;
3. writes learner code as the processor ObjectModule;
4. generates a separate trusted FormModule containing the hidden assertions;
5. asks 1C Designer to build a job-local `TaskForgeJudge.epf`;
6. runs that EPF once in `1cv8 ENTERPRISE` for all tests;
7. validates bounded result JSON and removes the complete job tree.

No dynamic `Выполнить`/`Eval` is required for learner code. Compile diagnostics
become the normal TaskForge `compile_error`; learner runtime exceptions become
per-test `runtime_error` results.

See `docs/onec/RUNTIME_MVP.md` for bootstrap/deployment details.

## Cluster distribution

Production server bundle v41.4.0+ distributes the proprietary platform outside
this image. The current Patroni Primary seeds one verified installer into its
local MinIO; other nodes fetch verified chunks from multiple completed peer
MinIO caches over WireGuard and mirror the cache locally before invoking this
image's bootstrap. The runner image therefore remains reproducible in GitHub CI
and never embeds the 1C distribution or license.
