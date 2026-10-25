#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(rel: str) -> str:
    path = ROOT / rel
    if not path.is_file():
        raise SystemExit(f"1C runtime check failed: missing {rel}")
    return path.read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("1C runtime check failed: " + message)


base = "services/execution/runners/onec-runner"
for rel in (
    f"{base}/driver/main.go",
    f"{base}/driver/main_test.go",
    f"{base}/driver/go.mod",
    f"{base}/bootstrap.sh",
    f"{base}/judge-src/TaskForgeJudge.xml",
    f"{base}/judge-src/TaskForgeJudge/Forms/Form.xml",
    f"{base}/judge-src/TaskForgeJudge/Forms/Form/Ext/Form.xml",
    f"{base}/judge-src/PROTOCOL.md",
    "docs/onec/RUNTIME_MVP.md",
):
    read(rel)

for obsolete in (
    f"{base}/Dockerfile.runtime",
    f"{base}/build-runtime.sh",
    f"{base}/platform-base/Dockerfile.private",
):
    require(not (ROOT / obsolete).exists(), f"obsolete private-image path still exists: {obsolete}")

driver = read(f"{base}/driver/main.go")
for needle in (
    'filepath.Join(jobDir, "infobase")',
    'copyTree(cfg.templateDir, infobase)',
    'defer os.RemoveAll(jobDir)',
    'ONEC_JUDGE_SOURCE_DIR',
    'prepareJudgeSource',
    'ObjectModule.bsl',
    'Module.bsl',
    '"DESIGNER"',
    '"/LoadExternalDataProcessorOrReportFromFiles"',
    '"ENTERPRISE"',
    '"/Execute", epfPath',
    'compileErrorJSON',
    'readResult(resultPath',
):
    require(needle in driver, f"trusted driver misses {needle}")
require('template symlinks are forbidden' in driver, "driver must reject template symlinks")
require('cleanupStaleJobs(cfg.workRoot, 10*time.Minute)' in driver, "stale 1C job cleanup is missing")
require('info.Mode().Perm() | 0o600' in driver, "ephemeral infobase copy must be writable")
require('ONEC_USE_XVFB' in driver and 'xvfb-run' in driver, "headless Linux execution wrapper missing")
require('"/L" + cfg.language' in driver, "1C interface language must use documented /L<code> syntax")
require('Выполнить' not in read(f"{base}/judge-src/PROTOCOL.md"), "protocol must not depend on dynamic Execute/Eval")

runner = read(f"{base}/main.go")
require('"--probe"' in runner and 'ONEC_RUNNER_SLOTS' in runner, "runner must probe runtime and bound concurrency")
require('"ONEC_JUDGE_SOURCE_DIR"' in runner, "runner must forward trusted Judge source path")

image = read(f"{base}/Dockerfile")
for needle in ('COPY bootstrap.sh /opt/taskforge/onec/bootstrap', 'COPY judge-src /opt/taskforge/onec/judge-src', 'xvfb', 'unixodbc'):
    require(needle in image, f"normal CI image misses auto-install runtime prerequisite {needle}")
require('ONEC_EXECUTABLE=/opt/1cv8/taskforge/1cv8' in image, "runner must target volume-backed stable 1C executable")

bootstrap = read(f"{base}/bootstrap.sh")
for needle in (
    'ONEC_INSTALL_SOURCE', 'ONEC_INSTALL_URL', 'ONEC_INSTALL_FILE', 'ONEC_INSTALL_SHA256',
    'sha256sum -c', '--mode unattended', '--enable-components', 'CREATEINFOBASE',
    '.taskforge-platform', '/installer-cache',
):
    require(needle in bootstrap, f"auto-install bootstrap misses {needle}")
require('ONEC_PLATFORM_VERSION is required' in bootstrap, "platform version must be explicit on first install")
require('A.B.C.D numeric form' in bootstrap, "platform version must be exact and validated")
require('find_exact_onec' in bootstrap and '$PLATFORM_ROOT/$arch/$VERSION/1cv8' in bootstrap, "bootstrap must select the requested exact 1C version")
require('find "$PLATFORM_ROOT" -mindepth 1 -maxdepth 1 -exec rm -rf' not in bootstrap, "platform upgrades must not destroy the previous installed version")

protocol = read(f"{base}/judge-src/PROTOCOL.md")
require('Функция Решение(Вход) Экспорт' in protocol and 'ObjectModule.bsl' in protocol,
        "compiled per-submission Judge contract is not documented")

for environment in ("dev", "prod"):
    compose = read(f"deploy/{environment}/compose/30-execution.yaml")
    require('ONEC_WORK_ROOT: /work' in compose, f"{environment} 1C jobs must use disk-backed /work")
    require('TASKFORGE_ONEC_WORK_DIR' in compose, f"{environment} 1C work bind is missing")
    require('ONEC_LICENSE_DIR' in compose, f"{environment} 1C license mount is missing")
    require('onec-platform:/opt/1cv8:ro' in compose, f"{environment} platform volume must be read-only in runner")
    require('onec-runtime:/opt/taskforge/onec/runtime:ro' in compose, f"{environment} runtime volume must be read-only in runner")
    require('ONEC_JUDGE_SOURCE_DIR' in compose, f"{environment} trusted Judge source path missing")
    require('ONEC_JUDGE_EPF' not in compose, f"{environment} still depends on a precompiled Judge EPF")
    env = read(f"deploy/{environment}/.env.example")
    for needle in ('ONEC_PLATFORM_VERSION=', 'ONEC_INSTALL_SOURCE=auto', 'ONEC_INSTALL_SHA256=', 'ONEC_INSTALL_COMPONENTS='):
        require(needle in env, f"{environment} env example misses {needle}")

print("1C auto-install runtime invariants OK")
