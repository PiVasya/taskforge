# Cloudflare origins

Generate the origin/failover description from the active inventory:

```bash
python3 ./cluster/manager.py --inventory ./cluster/inventory.json cloudflare
```

Important separation:

- public Cloudflare traffic/health probes use the node's public HTTP/HTTPS port;
- the dedicated cluster controller health port (default `9187`) binds to the
  WireGuard address and remains private between TaskForge nodes;
- v39 UFW therefore allows web traffic from Cloudflare CIDRs, while `9187` is
  allowed only over `wg-taskforge` from cluster peers.

For advanced quorum mode, `clusterctl.py cloudflare` emits the richer pool
layout with per-node priority, Host headers and active/passive ordering.

Before quorum mode is enabled, B/C must not be treated as writable just because
their network endpoint is reachable. `/ha/traffic-ready` is the authoritative
application-role readiness endpoint when the role controller is active.
