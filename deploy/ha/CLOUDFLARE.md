# Cloudflare Load Balancing for TaskForge HA

Cloudflare is responsible only for public HTTP(S) routing. It does not decide which PostgreSQL is primary.

Create two origins/pools in priority order:

```text
1. A_PUBLIC_IP
2. B_PUBLIC_IP
```

Use a health monitor that requests:

```text
HTTP
port: 9187
path: /ha/traffic-ready
expected status: 200
```

The HA agent returns:

- 200 only on the node currently allowed to serve production traffic;
- 503 on the standby or while a promotion/failback is in progress.

Normal state:

```text
A -> 200
B -> 503
```

After failover:

```text
A -> timeout/503
B -> 200
```

After A has rejoined and the controlled automatic failback completes:

```text
A -> 200
B -> 503
```

Public application traffic still uses the normal TaskForge HTTPS origin/port. Port 9187 is only the monitor endpoint.

The unauthenticated 9187 endpoints expose only liveness/readiness and node ID. Control endpoints require the shared HA bearer token and additionally reject requests not originating from the configured peer WireGuard IP.

Prefer firewalling TCP 9187 to Cloudflare health-check source networks if practical. WireGuard UDP 51820 must be reachable between A and B. Keep PostgreSQL 5432 and MinIO 9000 reachable only through WireGuard.
