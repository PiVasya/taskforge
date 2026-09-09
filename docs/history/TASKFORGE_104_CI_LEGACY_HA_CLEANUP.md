# TaskForge 104 — CI legacy HA cleanup

The N-node cluster manager is the only supported HA implementation.

Removed from the repository:

- `deploy/ha/` — obsolete fixed two-node HA implementation.
- `scripts/ci/check-ha-two-node.sh` — obsolete CI for that implementation.

`deploy/cluster/cleanup-legacy-ha.sh` remains intentionally: it cleans old runtime/systemd state on servers upgraded from historical bundles. It does not require the old source directory to exist.

The N-node invariant now rejects both the legacy runtime source directory and its retired CI helper.
