#!/usr/bin/env python3
"""Fail CI when a backend silently loses container-visible runtime logging."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
errors: list[str] = []

programs = sorted((ROOT / "services").glob("**/Program.cs"))
if not programs:
    errors.append("no .NET backend Program.cs files found")

for program in programs:
    rel = program.relative_to(ROOT)
    text = program.read_text(encoding="utf-8", errors="replace")
    diag = program.parent / "Diagnostics" / "TaskForgeDebugDiagnostics.cs"
    if not diag.is_file():
        errors.append(f"{rel}: missing Diagnostics/TaskForgeDebugDiagnostics.cs")
        continue
    diag_text = diag.read_text(encoding="utf-8", errors="replace")
    if "AddTaskForgeDebugDiagnostics" not in text:
        errors.append(f"{rel}: TaskForge debug diagnostics are not registered")
    if "Logging.ClearProviders" in text and "Logging.AddConsole" not in text:
        errors.append(f"{rel}: clears default logging providers without restoring console logging")
    if "Console.WriteLine($\"[TFDBG BOOT]" not in diag_text:
        errors.append(f"{diag.relative_to(ROOT)}: no direct stdout TFDBG boot marker")

    # HTTP applications construct a WebApplication and must log every ordinary
    # inbound HTTP request. Background workers intentionally have no middleware.
    is_http = bool(re.search(r"\bWebApplication\b|builder\.Build\(\)", text)) and "Host.CreateApplicationBuilder" not in text
    if is_http and "app.Run" in text:
        if "UseTaskForgeDebugRequestLogging" not in text:
            errors.append(f"{rel}: HTTP request logging middleware is not enabled")
        elif "UseTaskForgeDebugRequestLogging(this IApplicationBuilder" not in diag_text:
            errors.append(f"{diag.relative_to(ROOT)}: Program enables inbound logging but the middleware extension is missing")

# Every .NET runtime image must explicitly wire the switch that controls verbose
# logs. Console ILogger output is stdout/stderr and therefore appears in docker logs.
for dockerfile in sorted((ROOT / "services").glob("**/Dockerfile")):
    rel = dockerfile.relative_to(ROOT)
    text = dockerfile.read_text(encoding="utf-8", errors="replace")
    if ("dotnet " in text or "TaskForge." in text) and "TASKFORGE_DEBUG_LOGS" not in text:
        errors.append(f"{rel}: .NET runtime image has no TASKFORGE_DEBUG_LOGS wiring")

# Production compose must keep debug logging on by default for every app service
# that declares the switch. This catches an accidental '-0' release regression.
prod_compose = ROOT / "deploy" / "prod" / "compose"
compose_text = "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in sorted(prod_compose.glob("*.yaml")))
if "TASKFORGE_DEBUG_LOGS: ${TASKFORGE_DEBUG_LOGS:-0}" in compose_text:
    errors.append("production compose defaults TASKFORGE_DEBUG_LOGS to 0")
if compose_text.count("TASKFORGE_DEBUG_LOGS: ${TASKFORGE_DEBUG_LOGS:-1}") < 20:
    errors.append("production compose has unexpectedly few TASKFORGE_DEBUG_LOGS=1 service bindings")

sql_worker = ROOT / "services" / "sql-worker" / "cmd" / "worker" / "main.go"
if sql_worker.exists():
    text = sql_worker.read_text(encoding="utf-8", errors="replace")
    if "os.Stdout" not in text or "slog" not in text:
        errors.append("Go SQL worker is not explicitly logging to stdout")
else:
    # Keep discovery resilient if package layout changes, but do not silently skip.
    candidates = list((ROOT / "services").glob("**/*.go"))
    if not any("slog.NewJSONHandler(os.Stdout" in p.read_text(encoding="utf-8", errors="replace") for p in candidates):
        errors.append("no Go backend with explicit slog stdout logging found")

image_docker = ROOT / "services" / "analyzers" / "image-analyzer" / "Dockerfile"
if not image_docker.exists() or "uvicorn" not in image_docker.read_text(encoding="utf-8", errors="replace"):
    errors.append("image-analyzer stdout logging entrypoint is missing")

code_docker = ROOT / "services" / "analyzers" / "code-analyzer" / "Dockerfile"
if not code_docker.exists() or "RUST_LOG" not in code_docker.read_text(encoding="utf-8", errors="replace"):
    errors.append("code-analyzer runtime log-level wiring is missing")

if errors:
    print("RUNTIME LOGGING AUDIT: FAIL", file=sys.stderr)
    for item in errors:
        print(f" - {item}", file=sys.stderr)
    raise SystemExit(1)

print(f"RUNTIME LOGGING AUDIT: PASS (.NET programs={len(programs)}, production debug bindings={compose_text.count('TASKFORGE_DEBUG_LOGS: ${TASKFORGE_DEBUG_LOGS:-1}')})")
print("Container visibility: .NET ILogger console provider + TFDBG direct stdout boot markers + analyzer/SQL stdout wiring present.")
