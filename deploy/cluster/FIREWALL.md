# Automatic UFW policy

v39 treats host firewall as part of node convergence rather than an optional
manual step.

## Order of operations

During `apply`, `adopt`, `join`, `repair` and version upgrades:

1. host dependencies ensure `ufw` exists;
2. WireGuard is rendered/started;
3. current/effective sshd port(s) are detected and allowed;
4. public web rules are added;
5. peer WireGuard/data/control rules are added;
6. only then, if UFW was inactive, default incoming policy becomes deny and
   `ufw --force enable` is executed;
7. the active state is verified.

No `ufw reset` is used, so unrelated administrator rules survive upgrades.

## Public web source

Default:

```text
TASKFORGE_FIREWALL_WEB_SOURCE=cloudflare
```

HTTP/HTTPS are then allowed from the bundled official Cloudflare proxy CIDRs.
For a deliberately direct origin:

```bash
TASKFORGE_FIREWALL_WEB_SOURCE=any bash ./cluster.sh repair NODE_ID
```

`none` is also supported for a data-only origin, but normal TaskForge servers
should use `cloudflare`.

## Emergency override

```bash
TASKFORGE_FIREWALL_AUTO_ENABLE=0 bash ./cluster.sh repair NODE_ID
```

This stages rules but leaves an inactive UFW inactive. `doctor` will report it
as a failure so the exception cannot be forgotten silently.
