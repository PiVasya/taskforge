# 1C auto-install runtime

TaskForge builds `onec-runner` in normal GitHub CI exactly like the other runner
images. The image contains the HTTP boundary, trusted driver, bootstrap utility,
1C Linux runtime dependencies and trusted Judge source skeleton. It does **not**
contain the proprietary 1C distribution or licenses.

## Server bootstrap and cluster distribution

`onec-runner` itself still receives the platform through persistent volumes. In
the matching server bundle v41.4.0+, however, the official installer is no longer configured separately on every node. The current Patroni Primary seeds it once
into its private MinIO as a SHA-256-verified chunk manifest.

Every node then discovers the cluster desired version during normal `migrate`,
selects up to four already-complete peer MinIO seeds over WireGuard, downloads
different chunks in parallel, verifies each chunk and the full reconstructed
installer, mirrors that cache into its own MinIO, and only then invokes
`/opt/taskforge/onec/bootstrap` from this image.

The bootstrap still owns the platform-specific work:

- `onec-platform` -> `/opt/1cv8`;
- `onec-runtime` -> `/opt/taskforge/onec/runtime`;
- `onec-installer-cache` -> `/installer-cache`.

First installation requires an exact numeric `A.B.C.D` platform version and a
verified installer SHA-256. Platform versions are installed side-by-side in the
versioned `/opt/1cv8` tree; bootstrap never wipes an existing working version.
After unattended installation it selects the exact requested binary, creates the
stable `/opt/1cv8/taskforge/1cv8` symlink and creates an empty file-infobase
template with `CREATEINFOBASE`. The version/SHA marker keeps repeated migrations
idempotent.

Only the one-time Primary seed needs external download access. Normal peer
distribution stays inside the WireGuard cluster. The long-lived runner has no
external network and mounts platform/runtime read-only. See
`docs/onec/CLUSTER_DISTRIBUTION.md`.

## Licenses

Licenses are never put in the Docker image or platform volume by TaskForge. The
host directory `ONEC_LICENSE_DIR` is mounted read-only at `/var/1C/licenses` in
both bootstrap and runtime contexts. If the selected platform/license model needs
license material for `CREATEINFOBASE` or execution, place it there before the
migration.

## Submission lifecycle

For the initial `onec-code` mode, learner code implements exported
`Функция Решение(Вход)`. The driver copies the file-base template, creates a
job-specific external data processor from trusted XML plus learner ObjectModule,
compiles it through Designer, runs it once through Enterprise, reads the bounded
result and destroys the job directory.

The database copy lives under disk-backed `/work`, not tmpfs, to avoid turning
large 1C files into idle RAM pressure.

## What remains beyond this release

The auto-install/runtime path is complete enough to run the first algorithmic
1C code tasks once tested with an actual licensed platform. Query/object/document/
register/metadata/UI/project modes still need their own capability profiles and
purpose-built templates.
