# TaskForge Cluster Manager v31

v31 hardens the operational launcher after the first real A adoption.

Changes:

- Docker CLI calls made by root no longer inherit `/root/.docker/config.json`; the cluster manager always uses the bundle-local `.docker/config.json`, populated from the bundle `config.json` when present. This removes the repeated `Error parsing config file (/root/.docker/config.json)` warnings and isolates registry credentials per TaskForge bundle.
- User-facing cluster commands are documented as `bash ./cluster.sh ...`. This means a GUI extractor or filesystem may strip executable bits and the first command still works. The launcher immediately restores executable bits for every bundled `*.sh`. No manual `find ... chmod +x` step is required.
- Added regression invariants for the Docker config isolation and executable-bit self-healing entrypoint.

The A/B data path and adoption behavior are unchanged: existing PostgreSQL/MinIO containers and volumes are not recreated by `adopt`/`migrate-local`.
