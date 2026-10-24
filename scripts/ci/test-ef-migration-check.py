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

# Regression: an incomplete ModelSnapshot must never be treated as a reason
# to scaffold an identical table migration (incident 2026-10-24).
import tempfile
import textwrap
import importlib.util

history_path = ROOT / "scripts/ci/check-ef-migration-history.py"
history_spec = importlib.util.spec_from_file_location("tf_ef_history_check", history_path)
history = importlib.util.module_from_spec(history_spec)
assert history_spec and history_spec.loader
history_spec.loader.exec_module(history)

model_new = '''#pragma warning disable 612, 618
modelBuilder.Entity("Test.Rating", b => { b.ToTable("RatingAcceptedActivities"); });
#pragma warning restore 612, 618
'''
model_old = '''#pragma warning disable 612, 618
modelBuilder.Entity("Test.Old", b => { b.ToTable("OldTable"); });
#pragma warning restore 612, 618
'''
with tempfile.TemporaryDirectory() as temp:
    folder = Path(temp)
    first = folder / "20261024153108_AddRatingAcceptedActivities.cs"
    first.write_text('''protected override void Up(MigrationBuilder b) {
migrationBuilder.CreateTable(name: "RatingAcceptedActivities");
}
protected override void Down(MigrationBuilder b) { migrationBuilder.DropTable(name: "RatingAcceptedActivities"); }
''')
    (folder / "20261024153108_AddRatingAcceptedActivities.Designer.cs").write_text(model_new)
    snapshot = folder / "SolutionsDbContextModelSnapshot.cs"
    snapshot.write_text(model_old)
    assert any("stale/inconsistent" in error for error in history.inspect_migrations(folder))
    snapshot.write_text(model_new)
    assert not history.inspect_migrations(folder)
    second = folder / "20261024160137_AddRatingAcceptedActivities2.cs"
    second.write_text(first.read_text())
    (folder / "20261024160137_AddRatingAcceptedActivities2.Designer.cs").write_text(model_new)
    assert any("duplicate CREATE TABLE" in error for error in history.inspect_migrations(folder))
    second.write_text('''protected override void Up(MigrationBuilder b) {
migrationBuilder.DropTable(name: "RatingAcceptedActivities");
migrationBuilder.CreateTable(name: "RatingAcceptedActivities");
}
''')
    assert not history.inspect_migrations(folder)

print("ef migration history guard regression tests ok")

for generator_name in ("generate-migrations.sh", "generate-migrations-force.sh"):
    generator_source = (ROOT / "scripts" / generator_name).read_text()
    assert generator_source.count("check-ef-migration-history.py") == 2, generator_name
