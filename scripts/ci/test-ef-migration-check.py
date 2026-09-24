#!/usr/bin/env python3
"""Dependency-free regression tests for the EF migration drift checker."""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "scripts/ci/check-ef-migrations.py"
spec = importlib.util.spec_from_file_location("ef_migration_check", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = module
spec.loader.exec_module(module)


targets = module.load_targets()
assert len(targets) == 14, len(targets)
assert targets[0].alias == "identity"
assert targets[0].context == "IdentityDbContext"
assert targets[-1].alias == "telegram-quiz"
assert targets[-1].context == "TelegramQuizDbContext"
assert len({target.alias for target in targets}) == len(targets)
assert len({target.context for target in targets}) == len(targets)

selected = module.select_targets(targets, "identity,TasksDbContext,identity")
assert [target.alias for target in selected] == ["identity", "assignment"]

positive = [
    "Changes have been made to the model since the last migration.",
    "The context has pending model changes.",
    "Model changes were detected.",
    "Обнаружены изменения модели.",
]
for sample in positive:
    assert module.pending_from_output(sample), sample

negative = [
    "Build failed.",
    "Unable to create a 'DbContext' of type 'IdentityDbContext'.",
    "No changes have been made to the model since the last migration.",
]
for sample in negative:
    assert not module.pending_from_output(sample), sample

source = MODULE_PATH.read_text(encoding="utf-8")
assert "migrations has-pending-model-changes" in source
assert "migrations add" not in source
assert "database update" not in source
assert 'GENERATOR = ROOT / "scripts/generate-migrations.sh"' in source

print("ef migration drift checker invariants ok")
