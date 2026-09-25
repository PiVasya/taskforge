#!/usr/bin/env python3
"""Legacy compatibility entrypoint.

TaskForge no longer validates C# behavior by grepping exact source strings. Active CI
runs the xUnit projects through scripts/tests/dotnet.sh. This filename is retained
because it is part of the frozen develop(212) source-retention manifest.
"""
from __future__ import annotations

import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
print("Legacy C# source checker redirected to executable .NET tests.")
raise SystemExit(subprocess.call(["bash", str(ROOT / "scripts" / "tests" / "dotnet.sh")], cwd=ROOT))
