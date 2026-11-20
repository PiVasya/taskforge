#!/usr/bin/env python3
"""Read-only validation of physical EF database ownership (current deployment).

Never auto-move tables based on the mapping; schema changes require reviewed EF
migrations and an explicit, verified cutover. Keep this inventory in sync with
one DbContext ModelSnapshot per service, not obsolete monolith table names.
"""
from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
MAPPING = ROOT / "tools/database-splitter/table-ownership-map.json"
SOURCES = {'identity': 'services/identity/api/Migrations/IdentityDbContextModelSnapshot.cs', 'education': 'services/education/api/Migrations/EducationDbContextModelSnapshot.cs', 'content': 'services/content/api/Migrations/LearningDbContextModelSnapshot.cs', 'tasks': 'services/tasks/assignment-api/Migrations/TasksDbContextModelSnapshot.cs', 'solutions': 'services/solutions/api/Migrations/SolutionsDbContextModelSnapshot.cs', 'execution': 'services/execution/api/Migrations/ExecutionDbContextModelSnapshot.cs', 'ai': 'services/ai/api/Migrations/AiDbContextModelSnapshot.cs', 'support': 'services/support/api/Migrations/SupportDbContextModelSnapshot.cs', 'minecraft': 'services/minecraft/api/Migrations/MinecraftDbContextModelSnapshot.cs', 'files': 'services/files/api/Migrations/FilesDbContextModelSnapshot.cs', 'notifications': 'services/notifications/api/Migrations/NotificationsDbContextModelSnapshot.cs', 'observability': 'services/observability/api/Migrations/ObservabilityDbContextModelSnapshot.cs', 'telegram-quiz': 'services/bots/telegram-quiz-bot/Data/Migrations/TelegramQuizDbContextModelSnapshot.cs'}

def main():
    mapping = json.loads(MAPPING.read_text(encoding="utf-8"))
    problems = []
    if set(mapping) != set(SOURCES):
        problems.append("missing/extra service owners: " + str(sorted(set(mapping) ^ set(SOURCES))))
    for service, path in SOURCES.items():
        source = ROOT / path
        if not source.exists():
            candidates = list(source.parent.glob("*ModelSnapshot.cs"))
            if len(candidates) == 1:
                source = candidates[0]
            else:
                problems.append(f"{service}: missing EF snapshot {path}")
                continue
        actual = set(re.findall(r'\.ToTable\("([^"]+)"', source.read_text(encoding="utf-8-sig")))
        listed = mapping.get(service)
        if not isinstance(listed, list) or any(not isinstance(x, str) for x in listed):
            problems.append(f"{service}: expected array of table names")
            continue
        if len(set(listed)) != len(listed):
            problems.append(f"{service}: duplicate table in ownership map")
        if actual != set(listed):
            problems.append(f"{service}: missing={sorted(actual - set(listed))}, stale={sorted(set(listed) - actual)}")
    for problem in problems:
        print(f"::error::{problem}",file=sys.stderr)
    if problems:
        print('The target inventory and EF snapshots differ. This is not a passing schema check.', file=sys.stderr)
        print('For the fresh K3s lab, scaffold the new migrations with:', file=sys.stderr)
        print('  bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions', file=sys.stderr)
        print('Then rerun this check; do not hand-edit the target table map.', file=sys.stderr)
        return 1
    print("PASS: ownership inventory matches current EF model snapshots (not a database migration)")
    print("EF snapshots reflect the target single-owner design: Tasks definitions, Solutions attempts.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
