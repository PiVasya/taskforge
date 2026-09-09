# TaskForge 108 - Primary switch local Patroni route

## Symptom

`POST /api/admin/cluster/primary` reached observability-api but returned after about 6 seconds with:

`PATRONI_CLUSTER_TIMEOUT` / `Patroni /cluster не ответил вовремя.`

The host and Node Agent could still read Patroni, and Patroni never logged a switchover request.

## Root cause

The admin API is only allowed to execute on the currently confirmed Primary, but it still tried to read and mutate that same Primary's Patroni through the host WireGuard-published address. From the Docker bridge this self-WireGuard published-port path can be filtered/hairpinned and time out.

## Fix

- The controlled website switchover now talks to the local Compose `postgres:8008` Patroni endpoint.
- The endpoint remains configurable through `ClusterControl:LocalPatroniHost` and `ClusterControl:LocalPatroniPort`.
- Remote target safety is still validated from Patroni `/cluster`; the mutation is still `leader -> candidate` with the existing internal Patroni authentication.
- The frontend no longer calls a structured backend 5xx a "lost response". Preflight errors such as `PATRONI_CLUSTER_TIMEOUT` are displayed directly.
- Truly ambiguous mutation/transport outcomes (`PATRONI_SWITCH_TIMEOUT`, `PATRONI_SWITCH_FAILED`, or generic response-less 5xx) remain non-retriable and state-observed.

No server-control bundle protocol change is required; r57 Node Agent telemetry is sufficient.
