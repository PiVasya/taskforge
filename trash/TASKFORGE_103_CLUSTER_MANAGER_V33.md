# TaskForge Cluster Manager v33

- Fixed false `WireGuard peers expected=N actual=0` in `doctor` when invoked as a normal user.
- `cluster.sh status` and `cluster.sh doctor` now self-elevate before reading WireGuard kernel peer/handshake metadata.
- No manual `sudo wg` or chmod step is required.
- PostgreSQL/MinIO/container state is unchanged by this diagnostics-only fix.
