# TaskForge Cluster Manager v30

v30 is the hardened A/B adoption release of the cluster manager.

Key changes over v29:

- fixes the Bash `set -u` crash in `install_proxy()` during `migrate-local` / `adopt`;
- prevents the exact dependent same-line `local` assignment pattern with a CI regression invariant;
- self-heals executable bits when the first invocation is made as `bash ./cluster.sh ...`;
- keeps automatic host dependency installation (`socat`, WireGuard tools, Docker, jq, curl, etc.);
- keeps safe adoption of existing A/B data without recreating PostgreSQL or MinIO volumes;
- remains ready for additional C/D replicas and later three-voter Patroni/etcd quorum mode.

Current operational documentation lives in `deploy/cluster/README.md` and the production entrypoint is `deploy/prod/cluster.sh`.
