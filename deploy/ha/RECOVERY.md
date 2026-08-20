# HA recovery notes

## Split brain

If both agents can see two writable PostgreSQL primaries, both nodes deliberately remove traffic readiness. The software does not automatically choose A, because B may contain writes made after a previous failover.

1. Stop external traffic if Cloudflare has not already done so.
2. Back up both PostgreSQL volumes/hosts before destructive recovery if possible.
3. Decide which node is authoritative based on the actual failover history and latest user writes.
4. Keep the authoritative node primary.
5. On the losing node stop `taskforge-ha`, ensure its `.env` points at the authoritative peer, then run:

```bash
sudo python3 ha/agent.py --env-file .env rejoin --yes
sudo systemctl restart taskforge-ha
```

Repository layout equivalent:

```bash
sudo python3 deploy/ha/agent.py --env-file deploy/prod/.env rejoin --yes
```

`rejoin --yes` replaces the losing PostgreSQL data directory with a fresh base backup. PostgreSQL divergent timelines are not merged automatically.

## Standby lost WAL streaming

If the peer primary is reachable but a standby cannot stream for `HA_REJOIN_AFTER_SECONDS`, the agent automatically performs a fresh base backup from the current primary. No permanent replication slot is used, so an offline standby cannot consume the primary disk indefinitely.

## Fencing hook failed

The standby stays non-writable and returns 503 readiness. Fix the provider control path or perform a verified manual power-off of the old primary before forcing promotion. Do not treat a failed ping as proof that the primary is off.


## Cooperative handoff blocked by MinIO backlog

A planned/service failover or automatic failback stops TaskForge application writers before checking MinIO. If `minio-wait-replication.sh` cannot reach an empty outgoing backlog within `HA_MINIO_DRAIN_TIMEOUT_SECONDS`, the HA agent aborts the handoff and attempts to restore the existing primary stack.

Inspect from the current primary:

```bash
sudo ./ha/minio-wait-replication.sh 300
sudo ./.runtime/ha/bin/mc --config-dir .runtime/ha/mc-config replicate status tf-local/$(grep '^S3_BUCKET=' .env | cut -d= -f2-)
```

Do not force a planned failback while the active node still has unreplicated file operations unless you deliberately accept missing/stale objects on the destination. Hard failure of the active host is different: the source is unavailable, so the async-storage RPO is accepted and the fenced standby may still promote.
