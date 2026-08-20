# Fencing

With only A and B, a timeout cannot prove that A is powered off. B may simply have lost the network path to A. Promoting B while A is still writable can create two PostgreSQL primaries.

For unreachable-peer automatic failover, `agent.py` therefore calls `HA_FENCE_SCRIPT` before promotion.

The script receives:

```text
HA_TARGET_NODE_ID
HA_TARGET_WG_IP
HA_LOCAL_NODE_ID
```

It must exit **0 only after an independent control plane has confirmed that the target cannot write**. The usual implementation calls the hosting provider's power/API and powers the target off.

A ping failure, SSH failure or Cloudflare health failure is not sufficient fencing.

Start from `fence-provider.example.sh` and replace its body with the API call for your provider. Keep API credentials in a root-readable file or provider-specific credential store, not in the repository.

For **fully automatic recovery/failback**, also configure `HA_RECOVER_SCRIPT` from `recover-provider.example.sh`. The order is deliberately fixed: the old primary is fenced first, the standby promotes and becomes ready, and only then the recovery hook may power the fenced machine back on. This avoids the race where the old writable timeline reboots before the replacement primary exists.

Recommended permissions:

```bash
sudo chown root:root /path/to/fence-provider.sh /path/to/provider-credentials
sudo chmod 700 /path/to/fence-provider.sh
sudo chmod 600 /path/to/provider-credentials
```

Then set on both nodes:

```env
HA_FENCE_SCRIPT=/path/to/fence-provider.sh
HA_RECOVER_SCRIPT=/path/to/recover-provider.sh
HA_ALLOW_UNFENCED_FAILOVER=false
```

The unsafe override exists for emergencies:

```env
HA_ALLOW_UNFENCED_FAILOVER=true
```

With it, B can promote after a timeout without proving A is off. This can cause split brain. If the agents ever see two reachable primaries, they deliberately remove traffic readiness and **do not choose a winner automatically**, because node preference cannot prove which PostgreSQL timeline contains all writes.


If `HA_RECOVER_SCRIPT` is empty or fails, the successful failover is **not rolled back**. The new primary stays active. You then power the old node on manually; once its HA agent can reach the current primary, it rebuilds/rejoins as a standby and the normal automatic preferred-node failback can continue.
