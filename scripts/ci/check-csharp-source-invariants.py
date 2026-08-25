#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SERVICES = ROOT / "services" / "tasks" / "assignment-api" / "Services"

UNQUALIFIED_SYSTEM_MATH = re.compile(
    r"(?<![\w.:])Math\.(?:Abs|Acos|Asin|Atan|Atan2|Ceiling|Clamp|Cos|Exp|Floor|Log|Max|Min|Pow|Round|Sign|Sin|Sqrt|Tan|Truncate)\b"
)


def main() -> int:
    errors: list[str] = []
    for path in sorted(SERVICES.rglob("*.cs")):
        text = path.read_text(encoding="utf-8")
        for match in UNQUALIFIED_SYSTEM_MATH.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            errors.append(
                f"{path.relative_to(ROOT)}:{line}: use System.Math.* or global::System.Math.*; "
                "TaskForge.Tasks.Api.Services.Math shadows the BCL Math type inside Services.* namespaces"
            )
    projection_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Access" / "CourseMapProjectionService.cs"
    endpoints_path = ROOT / "services" / "tasks" / "assignment-api" / "Endpoints" / "Assignments" / "AssignmentsEndpoints.cs"
    tasks_security_path = ROOT / "services" / "tasks" / "assignment-api" / "Security" / "TaskForgeRequestSecurity.cs"
    assignment_access_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Access" / "AssignmentApiAccessService.cs"
    progression_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Access" / "CourseMapProgressionService.cs"
    education_internal_path = ROOT / "services" / "education" / "api" / "Endpoints" / "Internal" / "InternalEndpoints.cs"
    education_map_path = ROOT / "services" / "education" / "api" / "Endpoints" / "CourseMaps" / "CourseMapEndpoints.cs"
    task_graph_json_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Serialization" / "AssignmentTaskGraphJsonService.cs"
    assignment_endpoints_path = ROOT / "services" / "tasks" / "assignment-api" / "Endpoints" / "Assignments" / "AssignmentsEndpoints.cs"

    if projection_path.exists():
        projection = projection_path.read_text(encoding="utf-8")
        if "IEnumerable<SegmentPayload> Segments" not in projection or "yield return new SegmentPayload" not in projection:
            errors.append("course-map projection must emit course segments lazily instead of materializing the streamed response")
        if 'Guid.NewGuid().ToString("N")' not in projection or 'ProjectionToken = replayState?.ProjectionToken ??' not in projection:
            errors.append("course-map deltas must rotate immutable projection tokens while allowing replay of an already-created newer state")
        if "SnapshotBuilds.GetOrAdd" not in projection or "IDistributedCache" not in projection:
            errors.append("course-map projection lost cache-miss coalescing or Redis-backed distributed caching")
        if "IServiceScopeFactory" not in projection or "snapshotDb" not in projection:
            errors.append("shared course-map snapshot builds must use an independent DI scope instead of the request DbContext")
        if 'tasks:course-map-projection-current:v2' not in projection or 'state.RequestedCourseId' not in projection:
            errors.append("course-map current projection pointers must be scoped by requested course, not only by root")
        if "LoadNewerCurrentProjectionAsync" not in projection or "replayState" not in projection:
            errors.append("quiet learner-map deltas must replay a newer immutable projection after fast solve/back navigation")
        if "forceFreshMeta: true" not in projection or "if (forceFresh)" not in projection or "GetMapMetaAsync(requestedCourseId, ct, forceFresh || forceFreshMeta)" not in projection:
            errors.append("learner map cache revalidation must bypass stale map metadata/snapshot caches when freshness is required")
        if not re.search(r"\bpublic\s+CourseMapProjectionService\s*\(", projection):
            errors.append("CourseMapProjectionService must expose a public constructor so ASP.NET DI can activate it")

    program_path = ROOT / "services" / "tasks" / "assignment-api" / "Program.cs"
    readiness_path = ROOT / "services" / "tasks" / "assignment-api" / "Endpoints" / "ServiceInfo" / "ServiceInfoEndpoints.cs"
    diagnostics_path = ROOT / "services" / "tasks" / "assignment-api" / "Diagnostics" / "TaskForgeDebugDiagnostics.cs"

    if program_path.exists():
        program_source = program_path.read_text(encoding="utf-8")
        if "GetRequiredService<TaskForge.Tasks.Api.Services.Access.CourseMapProjectionService>()" not in program_source:
            errors.append("tasks-api startup must resolve CourseMapProjectionService so broken DI fails before serving traffic")

    if readiness_path.exists():
        readiness_source = readiness_path.read_text(encoding="utf-8")
        ready_match = re.search(r'MapGet\("/health/ready"[\s\S]{0,700}', readiness_source)
        if ready_match is None or "CourseMapProjectionService" not in ready_match.group(0):
            errors.append("tasks-api readiness must resolve CourseMapProjectionService, not only check PostgreSQL")

    if diagnostics_path.exists():
        diagnostics_source = diagnostics_path.read_text(encoding="utf-8")
        if "pipelineException" not in diagnostics_source or "StatusCodes.Status500InternalServerError" not in diagnostics_source:
            errors.append("tasks-api debug request logging must report unhandled pipeline exceptions as server failures")

    if endpoints_path.exists():
        endpoints = endpoints_path.read_text(encoding="utf-8")
        if "/learning-map/stream" not in endpoints or "/learning-map/delta" not in endpoints:
            errors.append("learner course-map stream/delta endpoints are missing")
        if 'X-Accel-Buffering' not in endpoints:
            errors.append("learner course-map stream no longer disables reverse-proxy buffering")
        if "ReadFreshMapRequest(http)" not in endpoints or "bool? fresh" in endpoints:
            errors.append("learner course-map stream must parse fresh revalidation explicitly instead of relying on bool model binding")
        for marker in ('raw.Equals("1"', 'raw.Equals("true"', 'raw.Equals("yes"', 'raw.Equals("on"'):
            if marker not in endpoints:
                errors.append(f"learner course-map fresh parser lost rolling-compatible value: {marker}")

    if tasks_security_path.exists():
        tasks_security = tasks_security_path.read_text(encoding="utf-8")
        for marker in ('path.EndsWith("/learning-map/delta"', 'HttpMethods.IsPost(method)', 'return Requirement.Authenticated;'):
            if marker not in tasks_security:
                errors.append(f"tasks-api learner learning-map delta security exception missing: {marker}")
        delta_rule_at = tasks_security.find('path.EndsWith("/learning-map/delta"')
        generic_course_rule_at = tasks_security.find('if (path == "/api/courses" || path.StartsWith("/api/courses/"))')
        if delta_rule_at < 0 or generic_course_rule_at < 0 or delta_rule_at > generic_course_rule_at:
            errors.append("learner learning-map delta must be classified Authenticated before generic course writes become Editor-only")

    if assignment_access_path.exists():
        access_source = assignment_access_path.read_text(encoding="utf-8")
        hard_access_at = access_source.find("var courseAccess = await LoadCourseAccessRowsAsync")
        projection_at = access_source.find("TryGetCachedAssignmentAccessAsync")
        if hard_access_at < 0 or projection_at < 0 or hard_access_at > projection_at:
            errors.append("direct assignment access must validate current hard course visibility before using cached progression")

    if progression_path.exists():
        progression_source = progression_path.read_text(encoding="utf-8")
        if "incoming[edge.Target].All(source => throughComplete.GetValueOrDefault(source))" not in progression_source:
            errors.append("hidden-start merge gates must wait for every incoming prerequisite of the target, not only the current source branch")
        hidden_gate_at = progression_source.find("if (state.Hidden && !hiddenPrerequisitesComplete)")
        visible_add_at = progression_source.find("visibleNodeIds.Add(state.NodeId);")
        if hidden_gate_at < 0 or visible_add_at < 0 or hidden_gate_at > visible_add_at:
            errors.append("hidden learner nodes must pass prerequisite gating before being added to visibleNodeIds")

    if education_internal_path.exists():
        education_source = education_internal_path.read_text(encoding="utf-8")
        if "request.BypassStudentVisibility" not in education_source or "request.IncludeProgressionRules" not in education_source:
            errors.append("education batch access lost admin visibility bypass or lightweight progression-rule opt-out")

    if education_map_path.exists():
        education_map_source = education_map_path.read_text(encoding="utf-8")
        if "COURSE_MAP_SYNTHETIC_FORBIDDEN" not in education_map_source or 'TryGetProperty("synthetic"' not in education_map_source:
            errors.append("education course-map save must reject learner synthetic nodes/edges")
        if "var editorDocument = ParseDocumentElement(map.DocumentJson);" in education_map_source and "editorDocument.HasValue" not in education_map_source:
            errors.append("nullable course-map editor JsonElement must be checked with HasValue before ValueKind/TryGetProperty")

    if task_graph_json_path.exists():
        task_graph_source = task_graph_json_path.read_text(encoding="utf-8")
        if "internal const int SchemaVersion = 4" not in task_graph_source or '"courses"' not in task_graph_source or '"course"' not in task_graph_source:
            errors.append("canonical task-graph JSON v4 must retain nested course references")
        if "internal const int MaxTasks = 5000" not in task_graph_source or "GraphExportOptions" not in task_graph_source or "GraphImportOptions" not in task_graph_source:
            errors.append("task-graph JSON lost the expanded export limit or selective import/export scopes")
        if "IReadOnlyList<CourseTreeCourseDto>" not in task_graph_source or "BuildCourseKeys" not in task_graph_source:
            errors.append("task-graph export must include the whole course subtree, not only direct assignments")

    if assignment_endpoints_path.exists():
        assignment_endpoints_source = assignment_endpoints_path.read_text(encoding="utf-8")
        if "/assignments/export-json" not in assignment_endpoints_source or "ReadGraphImportOptions" not in assignment_endpoints_source:
            errors.append("selective task-graph export/import endpoints are missing")
        if "/api/internal/courses/{courseId:D}/tree" not in assignment_endpoints_source:
            errors.append("task-graph export/import must resolve nested course ownership from education tree")

    # AI accounts have a resource policy, not an authorization shortcut. Keep the
    # account marker in the JWT, bypass only task energy, preserve rating energy,
    # and never leak the worker's reference solution into learner starter code.
    identity_access_path = ROOT / "services" / "identity" / "api" / "Services" / "Access" / "IdentityApiAccessService.cs"
    tasks_common_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Common" / "AssignmentApiCommonService.cs"
    task_test_service_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Testing" / "AssignmentApiTestingService.cs"
    math_service_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Math" / "AssignmentApiMathService.cs"
    solutions_access_path = ROOT / "services" / "solutions" / "api" / "Services" / "Access" / "SolutionsApiAccessService.cs"
    solutions_common_path = ROOT / "services" / "solutions" / "api" / "Services" / "Common" / "SolutionsApiCommonService.cs"
    identity_auth_path = ROOT / "services" / "identity" / "api" / "Endpoints" / "Auth" / "AuthEndpoints.cs"
    quota_endpoints_path = ROOT / "services" / "solutions" / "api" / "Endpoints" / "Quotas" / "QuotasEndpoints.cs"
    ai_mapping_path = ROOT / "services" / "ai" / "api" / "Services" / "Mapping" / "AiApiMappingService.cs"

    if identity_access_path.exists():
        identity_access = identity_access_path.read_text(encoding="utf-8")
        if 'new("account_type", NormalizeAccountType(user.AccountType))' not in identity_access:
            errors.append("identity JWT must carry normalized account_type so downstream AI resource policy is explicit")

    if tasks_common_path.exists():
        tasks_common = tasks_common_path.read_text(encoding="utf-8")
        for marker in ('AiAccounts:UnlimitedTaskEnergy', 'AiAccounts:UnlimitedTaskRateLimit', 'AiAccounts:UnlimitedTaskAttempts', 'AiAccounts:IgnoreTaskAttemptTimeLimits', 'AiAccounts:TaskRateLimitMultiplier', 'TaskForgeRequestSecurity.IsAiAccount(http.User)', 'X-TaskForge-AI-Task-Rate-Unlimited'):
            if marker not in tasks_common:
                errors.append(f"tasks-api AI resource policy marker missing: {marker}")
        if 'HasUnlimitedAiTaskEnergy(http, cfg)' not in tasks_common:
            errors.append("tasks-api attempt/image task energy consumption no longer bypasses task energy for configured AI accounts")

    if task_test_service_path.exists():
        task_test_service = task_test_service_path.read_text(encoding="utf-8")
        for marker in ('HasUnlimitedAiTaskAttempts(http, cfg)', 'IgnoreAiTaskAttemptTimeLimits(http, cfg)', 'ignoreTimeLimit ? null : TimeLimitFor'):
            if marker not in task_test_service:
                errors.append(f"tasks-api test AI no-wait policy marker missing: {marker}")
        if 'SelectedOptionKeys is { Count: > 0 }' not in task_test_service or 'SelectedOptionKey!' not in task_test_service:
            errors.append("single-choice API must fall back to selectedOptionKey when selectedOptionKeys is absent or empty")

    assignment_mapping_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Mapping" / "AssignmentApiMappingService.cs"
    if assignment_mapping_path.exists():
        assignment_mapping = assignment_mapping_path.read_text(encoding="utf-8")
        if 'taskConstraints = TaskConstraintsDto(x)' not in assignment_mapping or 'kind = "assignment"' not in assignment_mapping:
            errors.append("learner assignment DTOs must expose author-defined task constraints separately from platform security policy")

    execution_worker_contracts_path = ROOT / "services" / "execution" / "worker" / "Worker.Contracts.cs"
    execution_worker_policy_path = ROOT / "services" / "execution" / "worker" / "Worker.Policy.cs"
    execution_worker_path = ROOT / "services" / "execution" / "worker" / "Worker.cs"
    execution_worker_sanitization_path = ROOT / "services" / "execution" / "worker" / "Worker.Sanitization.cs"
    csharp_compiler_path = ROOT / "services" / "execution" / "runners" / "csharp-runner" / "Services" / "RoslynCompilationService.cs"
    csharp_execution_path = ROOT / "services" / "execution" / "runners" / "csharp-runner" / "Services" / "ExecutionService.cs"
    if execution_worker_contracts_path.exists():
        worker_contracts = execution_worker_contracts_path.read_text(encoding="utf-8")
        if 'string.IsNullOrWhiteSpace(Stderr)' not in worker_contracts or 'Код не соответствует правилам задания.' not in worker_contracts:
            errors.append("execution worker must preserve the sanitized task-policy reason in PolicyFailed messages")
    if execution_worker_policy_path.exists():
        worker_policy = execution_worker_policy_path.read_text(encoding="utf-8")
        if 'Код не соответствует правилам задания:' not in worker_policy:
            errors.append("execution worker task-policy message lost its explicit assignment-rule wording")

    if execution_worker_path.exists():
        worker_source = execution_worker_path.read_text(encoding="utf-8")
        for marker in ("Judge:RunnerAttempts", "IsJudgeUnavailableRoot", "IsJudgeUnavailableResult", "IsTransientRunnerStatus", "Judge:CompletionAttempts", "incompleteSuccessfulBatch"):
            if marker not in worker_source:
                errors.append(f"execution worker runner-recovery marker missing: {marker}")

    if execution_worker_sanitization_path.exists():
        worker_sanitization = execution_worker_sanitization_path.read_text(encoding="utf-8")
        for marker in ("Too many open files", "EMFILE", "ENFILE", "judge_unavailable"):
            if marker not in worker_sanitization:
                errors.append(f"execution worker infrastructure-failure classifier missing: {marker}")

    if csharp_compiler_path.exists():
        compiler_source = csharp_compiler_path.read_text(encoding="utf-8")
        if "_frameworkReferences = CreateFrameworkReferences()" not in compiler_source:
            errors.append("C# runner must cache trusted-platform metadata references across submissions")
        if "concurrentBuild: false" not in compiler_source:
            errors.append("C# runner Roslyn compilation must stay serial to match RunnerJobGate and bound peak memory")
        for marker in ("ImplicitUsingsSyntaxTree", "global using System;", "syntaxTrees: [ImplicitUsingsSyntaxTree, syntax]"):
            if marker not in compiler_source:
                errors.append(f"C# runner SDK-style implicit using support missing: {marker}")
        if compiler_source.count("encoding: Encoding.UTF8") < 2:
            errors.append("C# runner syntax trees must carry UTF-8 encoding so Portable PDB emit cannot fail with CS8055")
        if "usings:" in compiler_source:
            errors.append("C# runner must not rely on CSharpCompilationOptions.Usings for normal-compilation implicit usings")
        if "CompilationFailureKind.InfrastructureError" not in compiler_source or "catch (OutOfMemoryException" not in compiler_source:
            errors.append("C# Roslyn parent resource failures must not be reported as student CompileError")
        compile_method = compiler_source.split("public RoslynCompilationResult Compile", 1)[-1].split("private static MetadataReference[] CreateFrameworkReferences", 1)[0]
        if "MetadataReference.CreateFromFile" in compile_method:
            errors.append("C# runner must not rebuild trusted-platform MetadataReference objects inside every Compile call")

    if csharp_execution_path.exists():
        execution_source = csharp_execution_path.read_text(encoding="utf-8")
        for marker in ('TASKFORGE_LIMIT_NOFILE"] = "128"', '"judge_unavailable"', "IsRunnerInfrastructureFailure"):
            if marker not in execution_source:
                errors.append(f"C# runner child isolation/infrastructure marker missing: {marker}")

    if math_service_path.exists():
        math_service = math_service_path.read_text(encoding="utf-8")
        for marker in ('HasUnlimitedAiTaskAttempts(http, cfg)', 'IgnoreAiTaskAttemptTimeLimits(http, cfg)', 'ignoreTimeLimit ? null : TimeLimitFor'):
            if marker not in math_service:
                errors.append(f"tasks-api math AI no-wait policy marker missing: {marker}")

    if solutions_access_path.exists():
        solutions_access = solutions_access_path.read_text(encoding="utf-8")
        if 'AiAccounts:UnlimitedTaskEnergy' not in solutions_access or 'AiAccounts:UnlimitedTaskRateLimit' not in solutions_access or 'IsAiAccount(http, cfg)' not in solutions_access:
            errors.append("solutions-api lost configured AI task-energy/rate bypass")

    if solutions_common_path.exists():
        solutions_common = solutions_common_path.read_text(encoding="utf-8")
        if 'HasUnlimitedTaskRateLimit(http, cfg)' not in solutions_common or 'X-TaskForge-AI-Task-Rate-Unlimited' not in solutions_common:
            errors.append("solutions-api AI code-submit limiter bypass is missing")

    rating_results_path = ROOT / "services" / "solutions" / "api" / "Services" / "Results" / "SolutionsApiResultsService.cs"
    if rating_results_path.exists():
        rating_results = rating_results_path.read_text(encoding="utf-8")
        if rating_results.count('ON CONFLICT ("UserId") DO UPDATE SET') < 2:
            errors.append("rating dirty-user writes must remain atomic UPSERTs for both single and batch invalidation")
        if 'FROM unnest({ids}) AS input("UserId")' not in rating_results:
            errors.append("rating dirty-user batch invalidation must remain set-based instead of SELECT-then-INSERT")

    if identity_auth_path.exists():
        identity_auth = identity_auth_path.read_text(encoding="utf-8")
        for marker in ('AiAccounts:UnlimitedLoginRateLimit', 'passwordValid', 'unlimitedAiRefreshRate'):
            if marker not in identity_auth:
                errors.append(f"identity-api valid AI auth cooldown bypass marker missing: {marker}")

    if quota_endpoints_path.exists():
        quota_endpoints = quota_endpoints_path.read_text(encoding="utf-8")
        if 'GetQuotaStatus(db, uid.Value, cfg, HasUnlimitedTaskEnergy(http, cfg), isAdmin, IsAiAccount(http, cfg)' not in quota_endpoints:
            errors.append("quota status must report AI task energy/pacing while keeping top/rating unlimited only for admins")

    if ai_mapping_path.exists():
        ai_mapping = ai_mapping_path.read_text(encoding="utf-8")
        if 'starterCode = data["starterCode"]?.ToString() ?? string.Empty' not in ai_mapping:
            errors.append("AI assignment mapping must default learner starterCode to empty")
        if re.search(r'starterCode\s*=.*referenceSolution', ai_mapping):
            errors.append("AI referenceSolution must never fall back into learner starterCode")

    for environment in ("dev", "prod"):
        core_compose = ROOT / "deploy" / environment / "compose" / "20-core-services.yaml"
        env_example = ROOT / "deploy" / environment / ".env.example"
        if core_compose.exists():
            compose_text = core_compose.read_text(encoding="utf-8")
            required_compose_markers = (
                "AiAccounts__UnlimitedTaskEnergy:",
                "AiAccounts__UnlimitedTaskRateLimit:",
                "AiAccounts__UnlimitedTaskAttempts:",
                "AiAccounts__IgnoreTaskAttemptTimeLimits:",
                "AiAccounts__TaskRateLimitMultiplier:",
            )
            if any(compose_text.count(marker) < 2 for marker in required_compose_markers):
                errors.append(f"{environment} compose must pass AI task resource policy to both tasks-api and solutions-api")
            if compose_text.count("AiAccounts__UnlimitedLoginRateLimit:") < 1:
                errors.append(f"{environment} compose must pass AI login no-cooldown policy to identity-api")
        if env_example.exists():
            env_text = env_example.read_text(encoding="utf-8")
            for marker in (
                "AI_ACCOUNTS_UNLIMITED_TASK_ENERGY=true",
                "AI_ACCOUNTS_UNLIMITED_TASK_RATE_LIMIT=true",
                "AI_ACCOUNTS_UNLIMITED_TASK_ATTEMPTS=true",
                "AI_ACCOUNTS_IGNORE_TASK_ATTEMPT_TIME_LIMITS=true",
                "AI_ACCOUNTS_UNLIMITED_LOGIN_RATE_LIMIT=true",
                "AI_ACCOUNTS_TASK_RATE_LIMIT_MULTIPLIER=20",
            ):
                if marker not in env_text:
                    errors.append(f"{environment} .env.example missing AI resource policy knob: {marker}")

    for environment in ("dev", "prod"):
        execution_compose = ROOT / "deploy" / environment / "compose" / "30-execution.yaml"
        env_example = ROOT / "deploy" / environment / ".env.example"
        if execution_compose.exists():
            compose_text = execution_compose.read_text(encoding="utf-8")
            if "Judge__RunnerAttempts: ${JUDGE_RUNNER_ATTEMPTS:-3}" not in compose_text:
                errors.append(f"{environment} execution worker must expose bounded runner retries")
            csharp_block = compose_text.split("  csharp-runner:", 1)[-1].split("\n  cpp-runner:", 1)[0]
            for marker in ("soft: 4096", "hard: 4096", "${CSHARP_RUNNER_MEM_LIMIT:-1024m}"):
                if marker not in csharp_block:
                    errors.append(f"{environment} C# runner resource headroom marker missing: {marker}")
        if env_example.exists():
            env_text = env_example.read_text(encoding="utf-8")
            for marker in ("CSHARP_RUNNER_MEM_LIMIT=1024m", "JUDGE_RUNNER_ATTEMPTS=3"):
                if marker not in env_text:
                    errors.append(f"{environment} .env.example C# runner stability knob missing: {marker}")

    if errors:
        print("C# source invariants failed:\n" + "\n".join(errors), file=sys.stderr)
        return 1
    print("C# source invariants OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
