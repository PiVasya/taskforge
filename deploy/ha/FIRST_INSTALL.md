# First two-server HA installation

The examples use:

```text
A public IP: A_PUBLIC_IP
B public IP: B_PUBLIC_IP
A WireGuard: 10.80.0.1
B WireGuard: 10.80.0.2
WireGuard UDP: 51820
HA health TCP: 9187
```

Do this during a maintenance window. `setup-primary.sh` recreates the PostgreSQL container with WAL/HA settings and the B bootstrap replaces B's local PostgreSQL volume.

## 1. Put the same server bundle on both machines

Keep A's existing `.env`. On B do not independently invent a second set of TaskForge secrets.

On A, from the server bundle directory:

```bash
./generate-secrets.sh --yes
./check.sh
./backup.sh --metadata
./backup.sh --database
```

## 2. Create WireGuard keys

On A and B:

```bash
sudo ./ha/wireguard-generate.sh
```

Record each printed public key.

## 3. Configure WireGuard

On A:

```bash
sudo ./ha/wireguard-apply.sh \
  10.80.0.1 \
  B_WIREGUARD_PUBLIC_KEY \
  B_PUBLIC_IP:51820 \
  10.80.0.2
```

On B:

```bash
sudo ./ha/wireguard-apply.sh \
  10.80.0.2 \
  A_WIREGUARD_PUBLIC_KEY \
  A_PUBLIC_IP:51820 \
  10.80.0.1
```

Verify both ways:

```bash
ping -c 3 10.80.0.2   # from A
ping -c 3 10.80.0.1   # from B
sudo wg show wg-taskforge
```

Open UDP 51820 between the two public server addresses. PostgreSQL 5432 and MinIO 9000 should not be opened publicly.

## 4. Convert the current live server A to HA primary

On A:

```bash
sudo ./ha/setup-primary.sh A 10.80.0.1 10.80.0.2 A_PUBLIC_IP B_PUBLIC_IP
```

This keeps the existing A PostgreSQL data, creates a dedicated replication login, and marks A as the preferred node.

## 5. Create B's environment from A

On A:

```bash
./ha/make-peer-env.sh B 10.80.0.2 10.80.0.1 /tmp/taskforge-B.env
scp /tmp/taskforge-B.env YOUR_B_SSH_USER@B_PUBLIC_IP:/tmp/taskforge.env
rm -f /tmp/taskforge-B.env
```

On B:

```bash
sudo install -m 600 /tmp/taskforge.env .env
sudo ./generate-secrets.sh --yes
./check.sh
```

This intentionally shares JWT/internal/storage/HA credentials with A while B can generate its own local code-analyzer key pair if needed.

## 6. Bootstrap B from A

On B:

```bash
sudo ./ha/setup-standby.sh --yes B 10.80.0.2 10.80.0.1 A_PUBLIC_IP B_PUBLIC_IP
```

This destroys B's local PostgreSQL data directory and recreates it with `pg_basebackup` from A. It does not touch A.

Check:

```bash
sudo ./ha/status.sh
```

Expected:

```text
A: postgres_role=primary, traffic_ready=true
B: postgres_role=standby, standby_streaming=true, traffic_ready=false
```

## 7. Configure MinIO two-way replication

Run once on A after B MinIO is reachable:

```bash
sudo ./ha/setup-minio-replication.sh --yes
```

The script refuses the initial setup if B's bucket already contains objects, seeds existing objects A -> B, enables versioning on both buckets and then creates asynchronous replication in both directions. During a later **cooperative** failover/failback the current primary stops application writers and waits up to `HA_MINIO_DRAIN_TIMEOUT_SECONDS` for this replication backlog to become empty before database handoff. A hard crash cannot perform that drain, so the usual asynchronous RPO still applies to the newest files.

## 8. Sync TLS/DataProtection state

This is recommended so an existing certificate/key ring is already present on whichever machine becomes active.

On both A and B:

```bash
sudo ./ha/ssh-generate.sh
```

Take A's printed public key and on B run:

```bash
sudo ./ha/ssh-authorize-peer.sh 'A_PUBLIC_KEY_LINE' 10.80.0.1
```

Take B's key and on A run:

```bash
sudo ./ha/ssh-authorize-peer.sh 'B_PUBLIC_KEY_LINE' 10.80.0.2
```

Then from A:

```bash
sudo ./ha/sync-shared-volumes.sh --force
```

The systemd timer repeats this only from the currently active node.

## 9. Configure fencing before enabling unattended failover

Read `FENCING.md`. Put the provider-specific script path in both `.env` files:

```env
HA_FENCE_SCRIPT=/absolute/path/to/fence-provider.sh
HA_RECOVER_SCRIPT=/absolute/path/to/recover-provider.sh
HA_ALLOW_UNFENCED_FAILOVER=false
```

Then restart the agents:

```bash
sudo systemctl restart taskforge-ha
```

Do not treat automatic failover as safe until the fence script has been tested against a disposable/maintenance-window A shutdown. If you want A to come back without you opening the provider panel, implement and test `HA_RECOVER_SCRIPT` too. It is called only after the replacement node has promoted successfully; if it is absent, failover still works but the fenced server must be powered on manually before automatic failback can happen.

## 10. Configure Cloudflare

Read `CLOUDFLARE.md`.

## 11. Final checks and enable automatic promotion

On both nodes:

```bash
sudo ./ha/preflight.sh
```

During bootstrap `setup-primary.sh` / `setup-standby.sh` deliberately force `HA_AUTO_FAILOVER=false`, so an unfinished standby cannot take production. After fencing, MinIO, shared-state sync and Cloudflare are ready, enable it on **both** nodes:

```bash
sudo ./ha/enable-auto-failover.sh
```

From an external machine test:

```bash
curl -i http://A_PUBLIC_IP:9187/ha/traffic-ready
curl -i http://B_PUBLIC_IP:9187/ha/traffic-ready
```

Initially A should return 200 and B 503. Also verify MinIO replication from both nodes:

```bash
sudo ./ha/minio-wait-replication.sh 60
```

Run that command on A and B after a quiet moment; it should report an empty backlog.

## 12. Test real failover/failback

Do not pull A's network cable as the first test. First verify the fencing hook and provider API.

Then, during a maintenance window:

1. continuously write/read a harmless test record through TaskForge;
2. power A off in a way the fencing hook can confirm;
3. observe B promotion in `journalctl -u taskforge-ha -f`;
4. verify Cloudflare moves traffic to B;
5. make several writes while B is primary;
6. boot A;
7. verify A performs a fresh rejoin from B and catches up;
8. when automatic failback starts, verify B logs a successful MinIO backlog drain before PostgreSQL yield;
9. verify A returns to primary and B ends in streaming standby mode.
