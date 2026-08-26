# Migration v39 -> v40 r2

v40 adds Node Agent telemetry and hot application standby without rebuilding
PostgreSQL or MinIO.

## Rollout

Update `C -> B -> A`:

```bash
bash ./cluster.sh upgrade-v39 NODE_ID --from /path/to/v39-folder
bash ./cluster.sh status
bash ./cluster.sh doctor
```

No basebackup, promotion or data-volume recreation is performed. The migration also carries forward node-local Origin CA TLS material and shared ASP.NET DataProtection state from the v39 runtime tree when present, so a promoted standby does not discover missing certificates/keys at the worst possible moment.

Immediately after upgrade (still replica mode):

- A keeps the existing full application running;
- B creates/prepares the full application stack in STOPPED state;
- C creates/prepares only its lite assigned stack; `browser-api` and
  `image-analyzer` are not created and therefore are not auto-downloaded;
- Watchtower runs on all three nodes every ~5 minutes;
- on B/C Watchtower updates stopped containers but never revives them;
- Node Agent exposes telemetry on the WireGuard health port;
- automatic PostgreSQL promotion is still disabled until explicit quorum setup.

Wait for B/C `HOT READY` before enabling quorum.

## Enable automatic failover later

```bash
# A, B, C
bash ./cluster.sh quorum-prepare

# current A primary
bash ./cluster.sh quorum-enable-primary

# B, then C
bash ./cluster.sh quorum-join
```

After that Patroni/etcd own PostgreSQL leadership. C remains a voter with
`nofailover=true`.
