#!/usr/bin/env bash
set -euo pipefail

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$ROOT"

fail() {
  printf 'OJ security invariant failed: %s\n' "$*" >&2
  exit 1
}

printf '[oj-security] static architecture checks\n'
python3 - <<'PY'
from __future__ import annotations

import hashlib
import pathlib
import re
import sys
import yaml

root = pathlib.Path('.')
runners = [
    'cpp-runner', 'java-runner', 'javascript-runner', 'python-runner',
    'pascal-runner', 'image-cpp-runner', 'image-pascal-runner',
    'image-python-runner',
]
runner_services = runners + ['csharp-runner']


def die(message: str) -> None:
    raise SystemExit(f'OJ security invariant failed: {message}')

analyzer_attestation = (root / 'services/analyzers/code-analyzer/src/attestation.rs').read_text()
match = re.search(r'pub const POLICY_VERSION: &str = "([^"]+)";', analyzer_attestation)
if not match:
    die('cannot read analyzer policy version')
policy_version = match.group(1)

schema_match = re.search(r'pub const ATTESTATION_SCHEMA: &str = "([^"]+)";', analyzer_attestation)
if not schema_match:
    die('cannot read attestation schema')
schema = schema_match.group(1)

verifier_hashes: set[str] = set()
for runner in runners:
    directory = root / 'services/execution/runners' / runner
    verifier = directory / 'policy_attestation.go'
    if not verifier.is_file():
        die(f'{runner} has no attestation verifier')
    text = verifier.read_text()
    if schema not in text or policy_version not in text:
        die(f'{runner} verifier schema/version drift')
    verifier_hashes.add(hashlib.sha256(verifier.read_bytes()).hexdigest())

    dockerfile = (directory / 'Dockerfile').read_text()
    if 'COPY *.go ./' not in dockerfile:
        die(f'{runner} Dockerfile can omit security Go files')
    main = (directory / 'main.go').read_text()
    if 'verifyPolicyAttestation' not in main:
        die(f'{runner} does not enforce attestation')
    if 'policyAttestationReady' not in main:
        die(f'{runner} readiness does not validate the public key')

if len(verifier_hashes) != 1:
    die('Go attestation verifiers are not byte-identical')

csharp_verifier = (root / 'services/execution/runners/csharp-runner/Services/PolicyAttestationVerifier.cs').read_text()
if schema not in csharp_verifier or policy_version not in csharp_verifier:
    die('C# verifier schema/version drift')
if 'Verify("csharp", "standard"' not in (root / 'services/execution/runners/csharp-runner/Controllers/RunController.cs').read_text():
    die('C# endpoints do not enforce attestation')

csharp_compiler = (root / 'services/execution/runners/csharp-runner/Services/RoslynCompilationService.cs').read_text()
if '_frameworkReferences = CreateFrameworkReferences()' not in csharp_compiler or 'concurrentBuild: false' not in csharp_compiler:
    die('C# compiler resource reuse/serialization drift')
if 'CompilationFailureKind.InfrastructureError' not in csharp_compiler or 'catch (OutOfMemoryException' not in csharp_compiler:
    die('C# compiler parent resource failures can be misclassified as CompileError')
compile_body = csharp_compiler.split('public RoslynCompilationResult Compile', 1)[-1].split('private static MetadataReference[] CreateFrameworkReferences', 1)[0]
if 'MetadataReference.CreateFromFile' in compile_body:
    die('C# compiler recreates framework metadata references per submission')

csharp_execution = (root / 'services/execution/runners/csharp-runner/Services/ExecutionService.cs').read_text()
if 'TASKFORGE_LIMIT_NOFILE"] = "128"' not in csharp_execution:
    die('C# student child nofile limit drift')
if 'judge_unavailable' not in csharp_execution or 'Too many open files' not in csharp_execution:
    die('C# runner no longer distinguishes parent infrastructure exhaustion')

worker_source = (root / 'services/execution/worker/Worker.cs').read_text()
worker_sanitization = (root / 'services/execution/worker/Worker.Sanitization.cs').read_text()
if 'Judge:RunnerAttempts' not in worker_source or 'IsJudgeUnavailableResult' not in worker_source:
    die('execution worker lost bounded runner recovery')
if 'Too many open files' not in worker_sanitization or 'EMFILE' not in worker_sanitization:
    die('execution worker lost backward-compatible infrastructure classification')

python_a = root / 'services/execution/runners/python-runner/security/python_policy.py'
python_b = root / 'services/execution/runners/image-python-runner/security/python_policy.py'
if python_a.read_bytes() != python_b.read_bytes():
    die('Python execution policies drifted')
if (root / 'services/execution/runners/python-runner/server.py').exists():
    die('obsolete unprotected Python server returned')

js_docker = (root / 'services/execution/runners/javascript-runner/Dockerfile').read_text()
if 'FROM node:22-bookworm-slim' not in js_docker:
    die('JavaScript runner is not pinned to the hardened Node 22 runtime image')
if '--experimental-permission' not in (root / 'services/execution/runners/javascript-runner/main.go').read_text():
    die('JavaScript permission model is not enforced')

analyzer_dockerfile = (root / 'services/analyzers/code-analyzer/Dockerfile').read_text()
if 'FROM rust:slim-bookworm AS build' not in analyzer_dockerfile:
    die('code-analyzer Rust builder must be pinned to bookworm to avoid GLIBC drift')
if 'FROM debian:bookworm-slim' not in analyzer_dockerfile:
    die('code-analyzer runtime must remain on the same bookworm GLIBC generation')

required_analyzer_files = [
    'services/analyzers/code-analyzer/src/security_policy.rs',
    'services/analyzers/code-analyzer/src/attestation.rs',
]
for value in required_analyzer_files:
    if not (root / value).is_file():
        die(f'missing {value}')

for header in ('CTurtle.hpp', 'taskforge_turtle.h'):
    analyzer_header = root / 'services/analyzers/code-analyzer/include' / header
    runner_header = root / 'services/execution/runners/image-cpp-runner/include' / header
    if not analyzer_header.is_file() or analyzer_header.read_bytes() != runner_header.read_bytes():
        die(f'image C++ analyzer header drift: {header}')

for stale in root.glob('services/execution/**/taskforge-oj-security'):
    die(f'legacy compiler wrapper returned: {stale}')
if (root / 'services/execution/runners/csharp-runner/Infrastructure/Caching/TaskForgeCache.cs').exists():
    die('C# runner Redis cache returned')

callers = {
    'execution worker': ('services/execution/worker/Worker.cs', '/analyze'),
    'execution API': ('services/execution/api/Services/Results/ExecutionApiResultsService.cs', '/analyze'),
    'image assignment API': ('services/tasks/assignment-api/Services/Image/AssignmentApiImageService.cs', 'AnalyzeCodePolicyForAssignment'),
    'assignment analyzer client': ('services/tasks/assignment-api/Services/Common/AssignmentApiCommonService.cs', '/analyze'),
}
for name, (path, analyzer_marker) in callers.items():
    text = (root / path).read_text()
    if analyzer_marker not in text or 'attestation' not in text.lower():
        die(f'{name} can bypass mandatory analyzer attestation')

compose_paths = [
    root / 'deploy/dev/compose/20-core-services.yaml',
    root / 'deploy/dev/compose/30-execution.yaml',
    root / 'deploy/dev/compose/40-ai-and-analyzers.yaml',
    root / 'deploy/prod/compose/20-core-services.yaml',
    root / 'deploy/prod/compose/30-execution.yaml',
    root / 'deploy/prod/compose/40-ai-and-analyzers.yaml',
]
for path in compose_paths:
    yaml.safe_load(path.read_text())

for environment in ('dev', 'prod'):
    execution = yaml.safe_load((root / f'deploy/{environment}/compose/30-execution.yaml').read_text())
    analyzers = yaml.safe_load((root / f'deploy/{environment}/compose/40-ai-and-analyzers.yaml').read_text())
    core = yaml.safe_load((root / f'deploy/{environment}/compose/20-core-services.yaml').read_text())
    services = {}
    for document in (execution, analyzers, core):
        services.update(document.get('services') or {})

    for runner in runner_services:
        service = services.get(runner)
        if not service:
            die(f'{environment}: missing service {runner}')
        networks = service.get('networks') or []
        if isinstance(networks, dict):
            networks = list(networks)
        expected = f'{runner}-net'
        if networks != [expected]:
            die(f'{environment}: {runner} must only join {expected}, got {networks}')
        secrets = service.get('secrets') or []
        rendered = repr(secrets)
        if 'code_analyzer_public_key' not in rendered or 'code_analyzer_private_key' in rendered:
            die(f'{environment}: {runner} public/private key mounts are invalid')
        environment_values = service.get('environment') or {}
        if str(environment_values.get('CODE_ANALYZER_POLICY_VERSION')) != policy_version:
            die(f'{environment}: {runner} policy version drift')
        if str(service.get('user')) != '65534:65534':
            die(f'{environment}: {runner} must run as uid/gid 65534')
        if service.get('read_only') is not True:
            die(f'{environment}: {runner} root filesystem is writable')
        if service.get('cap_drop') != ['ALL']:
            die(f'{environment}: {runner} capabilities are not fully dropped')
        if 'no-new-privileges:true' not in (service.get('security_opt') or []):
            die(f'{environment}: {runner} no-new-privileges is missing')

    csharp = services['csharp-runner']
    csharp_nofile = (csharp.get('ulimits') or {}).get('nofile') or {}
    if int(csharp_nofile.get('soft', 0)) < 4096 or int(csharp_nofile.get('hard', 0)) < 4096:
        die(f'{environment}: C# parent nofile must be at least 4096')
    if 'CSHARP_RUNNER_MEM_LIMIT:-1024m' not in str(csharp.get('mem_limit')):
        die(f'{environment}: C# parent default memory must be 1024m')

    analyzer = services.get('code-analyzer')
    if not analyzer:
        die(f'{environment}: code-analyzer missing')
    analyzer_secret_entries = analyzer.get('secrets') or []
    analyzer_secrets = repr(analyzer_secret_entries)
    if 'code_analyzer_private_key' not in analyzer_secrets or 'code_analyzer_public_key' in analyzer_secrets:
        die(f'{environment}: analyzer key mounts are invalid')
    private_entries = [entry for entry in analyzer_secret_entries if isinstance(entry, dict) and entry.get('source') == 'code_analyzer_private_key']
    if len(private_entries) != 1 or str(private_entries[0].get('mode')) != '0400':
        die(f'{environment}: analyzer private key target mode must be 0400')
    if str(analyzer.get('user')) != '1000:1000':
        die(f'{environment}: analyzer must run as uid/gid 1000')
    if analyzer.get('read_only') is not True or analyzer.get('cap_drop') != ['ALL']:
        die(f'{environment}: analyzer container hardening drift')
    if 'no-new-privileges:true' not in (analyzer.get('security_opt') or []):
        die(f'{environment}: analyzer no-new-privileges is missing')
    analyzer_networks = analyzer.get('networks') or []
    if isinstance(analyzer_networks, dict):
        analyzer_networks = list(analyzer_networks)
    if analyzer_networks != ['code-analyzer-net']:
        die(f'{environment}: analyzer must only join code-analyzer-net')

    networks = {}
    for document in (execution, analyzers, core):
        networks.update(document.get('networks') or {})
    analyzer_network = networks.get('code-analyzer-net')
    if not isinstance(analyzer_network, dict) or analyzer_network.get('internal') is not True:
        die(f'{environment}: code-analyzer-net must be internal')

print(f'[oj-security] schema={schema} policy={policy_version}')
PY

printf '[oj-security] Python policy smoke tests\n'
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT HUP INT TERM
cat > "$tmp/safe.py" <<'PY'
import math
print(math.factorial(6))
PY
python3 services/execution/runners/python-runner/security/python_policy.py "$tmp/safe.py" standard

for payload in \
  'open("/etc/passwd").read()' \
  'getattr(object, "__subclasses__")' \
  'import os' \
  'import numpy as np; print(np.fromfile("/etc/passwd"))'; do
  printf '%s\n' "$payload" > "$tmp/bad.py"
  profile=standard
  case "$payload" in *numpy*) profile=image ;; esac
  if python3 services/execution/runners/python-runner/security/python_policy.py "$tmp/bad.py" "$profile" >/dev/null 2>&1; then
    fail "Python policy accepted a forbidden payload"
  fi
done

printf '[oj-security] JavaScript bootstrap smoke test\n'
if command -v node >/dev/null 2>&1; then
  mkdir -p "$tmp/js"
  cp services/execution/runners/javascript-runner/security/js_bootstrap.mjs "$tmp/js/bootstrap.mjs"
  cat > "$tmp/js/main.mjs" <<'JS'
console.log([1, 2, 3].map(x => x * 2).join(','));
JS
  (
    cd "$tmp/js"
    node \
      --experimental-permission \
      --no-warnings \
      --no-addons \
      --no-expose-wasm \
      --disallow-code-generation-from-strings \
      --disable-proto=throw \
      --allow-fs-read="$tmp/js/bootstrap.mjs" \
      --allow-fs-read="$tmp/js" \
      "$tmp/js/bootstrap.mjs" | grep -qx '2,4,6'
  )
else
  printf '[oj-security] node unavailable; bootstrap runtime smoke test skipped\n'
fi

printf '[oj-security] C sandbox sources\n'
if command -v gcc >/dev/null 2>&1; then
  while IFS= read -r file; do
    case "$file" in
      *sandbox_preload.c)
        gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
          -Wl,-z,relro,-z,now,-z,noexecstack -o "$tmp/$(basename "$(dirname "$file")")-preload.so" "$file"
        ;;
      *sandbox_guard.c)
        gcc -c -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
          -o "$tmp/$(printf '%s' "$file" | sha256sum | cut -c1-16).o" "$file"
        ;;
    esac
  done < <(find services/execution/runners -path '*/security/*.c' -type f | sort)
else
  printf '[oj-security] gcc unavailable; C sandbox compilation skipped\n'
fi

printf '[oj-security] Go runner tests and vet\n'
if command -v go >/dev/null 2>&1; then
  for runner in \
    cpp-runner java-runner javascript-runner python-runner pascal-runner \
    image-cpp-runner image-pascal-runner image-python-runner; do
    (
      cd "services/execution/runners/$runner"
      go test ./...
      go vet ./...
    )
  done
else
  printf '[oj-security] go unavailable; Go tests skipped\n'
fi

printf '[oj-security] Rust analyzer tests\n'
if command -v cargo >/dev/null 2>&1; then
  (cd services/analyzers/code-analyzer && cargo test --locked)
else
  printf '[oj-security] cargo unavailable; analyzer compilation is verified by its Docker image build\n'
fi

printf '[oj-security] all available checks passed\n'
