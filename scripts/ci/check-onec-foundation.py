#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def read(rel: str) -> str:
    path = ROOT / rel
    if not path.is_file():
        raise SystemExit(f"1C foundation check failed: missing {rel}")
    return path.read_text(encoding="utf-8")

def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("1C foundation check failed: " + message)

frontend_edit = read("apps/web/src/features/assignment-edit/assignmentEditModel.js")
frontend_solve = read("apps/web/src/features/assignment-solve/assignmentSolveSupport.js")
frontend_map = read("apps/web/src/features/course-assignments/courseMapNodeMeta.js")
editor = read("apps/web/src/components/CodeEditor.jsx")
require("{ value: 'onec', label: '1С' }" in frontend_edit, "authoring language list misses 1C")
require("{ value: 'onec'" in frontend_solve and "return 'onec'" in frontend_solve, "solve language normalization misses 1C")
require("onec: '1С'" in frontend_map, "course-map label misses 1C")
require('return "onec";' in editor and "ensureOneCMonacoLanguage" in editor, "Monaco 1C language support missing")

for rel in (
    "services/tasks/assignment-api/Services/Serialization/AssignmentApiSerializationService.cs",
    "services/solutions/api/Services/Serialization/SolutionsApiSerializationService.cs",
    "services/execution/api/Services/Serialization/ExecutionApiSerializationService.cs",
    "services/execution/worker/Worker.cs",
):
    text = read(rel)
    require('=> "onec"' in text, f"{rel} does not normalize 1C to onec")

common = read("services/tasks/assignment-api/Services/Common/AssignmentApiCommonService.cs")
require('"onec"' in common.split("SupportedCodeLanguages", 1)[-1], "tasks-api does not advertise 1C")
worker = read("services/execution/worker/Worker.cs")
require('Runners:OneC' in worker and 'http://onec-runner:8080' in worker, "execution worker does not route 1C")
execution_results = read("services/execution/api/Services/Results/ExecutionApiResultsService.cs")
require('("onec", false) => "onec-runner"' in execution_results, "execution API does not route 1C")

policy = read("services/analyzers/code-analyzer/src/security_policy.rs")
analyzer = read("services/analyzers/code-analyzer/src/main.rs")
require('Some("onec")' in policy and '"onec" => analyze_onec' in policy, "code analyzer does not recognize 1C")
for rule in ("onec.dynamic_execute", "onec.process", "onec.network", "onec.files", "onec.external_component"):
    require(rule in policy, f"1C security policy misses {rule}")
require('if lang == "onec"' in analyzer, "1C executable Cyrillic exception missing")

runner = ROOT / "services/execution/runners/onec-runner"
for name in ("Dockerfile", "bootstrap.sh", "main.go", "policy_attestation.go", "README.md"):
    require((runner / name).is_file(), f"onec-runner missing {name}")
main = (runner / "main.go").read_text(encoding="utf-8")
require('verifyPolicyAttestation("onec", "standard"' in main, "onec-runner does not enforce signed policy")
require('ONEC_DRIVER' in main and '/ready' in main, "onec-runner readiness/driver contract missing")

for environment in ("dev", "prod"):
    compose = read(f"deploy/{environment}/compose/30-execution.yaml")
    require("  onec-runner:\n" in compose, f"{environment} compose misses onec-runner")
    require("profiles:\n    - onec" in compose, f"{environment} onec-runner must be optional")
    require("onec-runner-net" in compose and "Runners__OneC: http://onec-runner:8080" in compose,
            f"{environment} 1C network/routing missing")
    env = read(f"deploy/{environment}/.env.example")
    require("ONEC_ENABLED=false" in env, f"{environment} must keep 1C disabled by default")

workflow = read(".github/workflows/develop-build.yml")
full = read(".github/workflows/develop-full-rebuild.yml")
require("onec-runner|./services/execution/runners/onec-runner" in workflow, "normal CI does not build onec contract image")
require("- name: onec-runner" in full, "full rebuild does not build onec contract image")

print("1C foundation invariants OK")
