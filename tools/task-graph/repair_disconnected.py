#!/usr/bin/env python3
"""Create a previewable, non-destructive task-graph link repair from an export.

The operator explicitly supplies task/course key pairs, never inferred titles.
No production API calls are made and no task content is changed.
"""
import argparse
import csv
import json
import pathlib
import uuid


def build_repair(export, pairs):
    if export.get("format") != "taskforge-task-graph" or export.get("schemaVersion") != 5:
        raise ValueError("Expected schemaVersion=5 taskforge-task-graph export")
    tasks = {item["key"]: item for item in export.get("tasks", [])}
    courses = {item["key"]: item for item in export.get("courses", [])}
    existing = {(edge["from"], edge["to"]) for edge in export.get("connections", [])}
    requested = {}
    for task_key, course_key in pairs:
        if task_key in requested and requested[task_key] != course_key:
            raise ValueError(f"Conflicting target course for task: {task_key}")
        requested[task_key] = course_key
    unknown_tasks = set(requested) - set(tasks)
    unknown_courses = set(requested.values()) - set(courses)
    if unknown_tasks or unknown_courses:
        raise ValueError(f"Unknown keys: tasks={sorted(unknown_tasks)}, courses={sorted(unknown_courses)}")

    resolved_tasks = []
    resolved_courses = []
    connections = []
    for course_key in sorted(set(requested.values())):
        course = courses[course_key]
        uuid.UUID(course["id"])
        resolved_courses.append({"key": course_key, "id": course["id"], "title": course["title"]})
        # Include root links even if already present, so the import result is
        # complete for the affected courses and works with either merge or replace.
        connections.append({"from": "$course", "to": course_key})
    for task_key, course_key in sorted(requested.items()):
        task = tasks[task_key]
        uuid.UUID(task["id"])
        resolved_tasks.append({"key": task_key, "id": task["id"]})
        if (course_key, task_key) not in existing:
            connections.append({"from": course_key, "to": task_key})
    if len(connections) == len(resolved_courses):
        raise ValueError("All requested course/task links already exist; nothing to repair")
    return {"schemaVersion": 5, "format": "taskforge-task-graph",
        "scopes": ["ids", "connections"], "courses": resolved_courses,
        "tasks": resolved_tasks, "deleteTasks": [], "datasets": [], "connections": connections}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map", required=True, type=pathlib.Path, help="Full TaskForge task graph export (with UUIDs)")
    parser.add_argument("--links", required=True, type=pathlib.Path, help="CSV: taskKey,courseKey")
    parser.add_argument("--out", required=True, type=pathlib.Path)
    args = parser.parse_args()
    with args.map.open(encoding="utf-8") as f:
        export = json.load(f)
    with args.links.open(encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        if not reader.fieldnames or not {"taskKey", "courseKey"}.issubset(reader.fieldnames):
            raise ValueError("CSV requires taskKey,courseKey headers")
        pairs = [(row["taskKey"], row["courseKey"]) for row in reader]
    document = build_repair(export, pairs)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8") as f:
        json.dump(document, f, ensure_ascii=False, separators=(",", ":"))
        f.write("\n")
    print(f"Repair ready: {len(document['tasks'])} existing tasks, {len(document['courses'])} courses, {len(document['connections'])} specified edges. Preview with MERGE mode; do not touch task contents.")


if __name__ == "__main__":
    main()
