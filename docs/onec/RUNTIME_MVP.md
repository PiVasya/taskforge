# 1C auto-install runtime

TaskForge builds `onec-runner` in normal GitHub CI exactly like the other runner
images. The image contains the HTTP boundary, trusted driver, bootstrap utility,
1C Linux runtime dependencies and trusted Judge source skeleton. It does **not**
contain the proprietary 1C distribution or licenses.

## Server bootstrap

When `ONEC_ENABLED=true`, the server migration runs `/opt/taskforge/onec/bootstrap`
from the freshly pulled `onec-runner` image before it starts the long-lived
service. The bootstrap mounts three persistent volumes read-write:

- `onec-platform` -> `/opt/1cv8`;
- `onec-runtime` -> `/opt/taskforge/onec/runtime`;
- `onec-installer-cache` -> `/installer-cache`.

It obtains the official Linux `.run` installer using either:

```env
ONEC_INSTALL_SOURCE=file
ONEC_INSTALL_FILE=/secure/path/setup-full.run
```

or:

```env
ONEC_INSTALL_SOURCE=url
ONEC_INSTALL_URL=https://private-or-official-source/setup-full.run
```

`auto` prefers the configured local file and otherwise the URL. First install
requires `ONEC_PLATFORM_VERSION` in exact numeric `A.B.C.D` form (for example `8.3.27.1508`) and `ONEC_INSTALL_SHA256`. The cached installer
is reused and checked before any download.

Platform versions are installed side-by-side in the versioned `/opt/1cv8` tree; bootstrap never wipes an existing platform before installing another version. After unattended installation, the bootstrap requires the exact requested version and creates a stable executable symlink
`/opt/1cv8/taskforge/1cv8` and creates an empty file-infobase template in the
runtime volume with `CREATEINFOBASE`. A version/SHA marker makes re-running the
migration idempotent.

The bootstrap container may use normal outbound networking to fetch the installer.
The long-lived runner cannot: it remains only on the internal runner network and
mounts platform/runtime read-only.

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
