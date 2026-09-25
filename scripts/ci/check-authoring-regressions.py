#!/usr/bin/env python3
"""Legacy compatibility entrypoint for the former source-grep authoring checker.

Active CI uses executable frontend tests and a production build through
scripts/tests/frontend.sh. This wrapper exists only for old local commands.
"""
from __future__ import annotations

import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
print("Legacy authoring source checker redirected to executable frontend tests.")
raise SystemExit(subprocess.call(["bash", str(ROOT / "scripts" / "tests" / "frontend.sh")], cwd=ROOT))
