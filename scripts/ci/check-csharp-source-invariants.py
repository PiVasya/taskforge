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
    assignment_access_path = ROOT / "services" / "tasks" / "assignment-api" / "Services" / "Access" / "AssignmentApiAccessService.cs"
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

    if assignment_access_path.exists():
        access_source = assignment_access_path.read_text(encoding="utf-8")
        hard_access_at = access_source.find("var courseAccess = await LoadCourseAccessRowsAsync")
        projection_at = access_source.find("TryGetCachedAssignmentAccessAsync")
        if hard_access_at < 0 or projection_at < 0 or hard_access_at > projection_at:
            errors.append("direct assignment access must validate current hard course visibility before using cached progression")

    if education_internal_path.exists():
        education_source = education_internal_path.read_text(encoding="utf-8")
        if "request.BypassStudentVisibility" not in education_source or "request.IncludeProgressionRules" not in education_source:
            errors.append("education batch access lost admin visibility bypass or lightweight progression-rule opt-out")

    if education_map_path.exists():
        education_map_source = education_map_path.read_text(encoding="utf-8")
        if "COURSE_MAP_SYNTHETIC_FORBIDDEN" not in education_map_source or 'TryGetProperty("synthetic"' not in education_map_source:
            errors.append("education course-map save must reject learner synthetic nodes/edges")

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

    if errors:
        print("C# source invariants failed:\n" + "\n".join(errors), file=sys.stderr)
        return 1
    print("C# source invariants OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
