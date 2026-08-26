# TaskForge Cluster Manager v40

Use one bundle on every node:

```bash
bash ./cluster.sh help
bash ./cluster.sh status
bash ./cluster.sh doctor
```

v40 combines the proven v39 data cluster with an application control plane:

- WireGuard + UFW;
- PostgreSQL streaming replication in legacy replica mode;
- optional Patroni + 3-voter etcd quorum;
- N-way MinIO replication;
- host Node Agent;
- full/lite application profiles;
- warm stopped containers on standby nodes;
- host/Docker/update telemetry for TaskForge admin UI.

## v39 -> v40

```bash
bash ./cluster.sh upgrade-v39 NODE_ID --from /path/to/v39
```

This preserves existing database/object-storage data. Before explicit quorum
migration, Node Agent is intentionally unable to promote a node.

## Default A/B/C application profiles

If a v39 topology has no `app` section, v40 normalizes it automatically:

```text
highest priority       -> full, can primary
second highest         -> full, can primary
third / remaining      -> lite, cannot primary, assist enabled
```

Default lite exclusions:

```text
browser-api
image-analyzer
```

Default singleton exclusions while C is assisting:

```text
support-bot
telegram-quiz-bot
rating-worker
```

## Explicit quorum migration

After v40 is healthy on all nodes:

```bash
# A/B/C voters
bash ./cluster.sh quorum-prepare

# A
bash ./cluster.sh quorum-enable-primary

# B, then C
bash ./cluster.sh quorum-join
```

The standalone PostgreSQL volumes are preserved by the migration scripts; the
Patroni data path is separate. Do not run manual promotion in parallel with the
quorum flow.

## New node

For an additional node use the normal prepare/import/apply/join workflow. App
profile fields are part of topology and should be chosen deliberately before
turning the node into a production failover target.

See `APP_HA.md` for application behavior and `RECOVERY.md` for recovery paths.
