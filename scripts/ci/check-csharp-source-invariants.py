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
    if errors:
        print("C# source invariants failed:\n" + "\n".join(errors), file=sys.stderr)
        return 1
    print("C# source invariants OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
