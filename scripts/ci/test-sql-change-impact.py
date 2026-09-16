#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
import unittest

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("sql_change_impact", HERE / "sql-change-impact.py")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)
classify = MODULE.classify


class SqlChangeImpactTests(unittest.TestCase):
    def assertImpact(self, paths, contract=False, engines=False):
        impact = classify(paths)
        self.assertEqual(contract, impact.contract, impact)
        self.assertEqual(engines, impact.engines, impact)

    def test_unrelated_frontend_does_not_start_sql(self):
        self.assertImpact([
            "apps/web/src/features/admin-solutions/AdminSolutionsFeature.jsx",
            "apps/web/src/features/landing/landing.css",
        ])

    def test_sql_frontend_uses_normal_frontend_suite_only(self):
        self.assertImpact(["apps/web/src/features/sql-task/SqlTaskSolve.jsx"])

    def test_generic_education_ai_and_global_dotnet_changes_do_not_start_sql(self):
        self.assertImpact([
            "services/education/api/Endpoints/CourseEndpoints.cs",
            "services/ai/api/Endpoints/AiEndpoints.cs",
            "Directory.Build.props",
            "global.json",
        ])

    def test_sql_domain_change_runs_contract_gate_without_real_engines(self):
        self.assertImpact(
            ["services/tasks/assignment-api/Domain/Sql/SqlDataset.cs"],
            contract=True,
            engines=False,
        )

    def test_shared_wire_change_runs_contract_gate_without_provider_containers(self):
        self.assertImpact(
            ["services/shared/Sql/SqlContracts.cs"],
            contract=True,
            engines=False,
        )

    def test_sql_worker_change_runs_real_engine_gate(self):
        self.assertImpact(
            ["services/execution/sql-worker/internal/sqlworker/runner.go"],
            contract=False,
            engines=True,
        )

    def test_provider_init_change_runs_real_engine_gate(self):
        self.assertImpact(
            ["infrastructure/sql/postgres-init/10-sandbox-guard.sh"],
            contract=False,
            engines=True,
        )

    def test_sql_compose_change_runs_real_engine_gate(self):
        self.assertImpact(
            ["deploy/prod/compose/35-sql.yaml"],
            contract=False,
            engines=True,
        )

    def test_protected_sql_migration_change_runs_contract_gate(self):
        self.assertImpact(
            ["services/tasks/assignment-api/Migrations/20260909054604_AddSqlDomain.cs"],
            contract=True,
            engines=False,
        )

    def test_unrelated_migration_does_not_get_misclassified(self):
        self.assertImpact(
            ["services/tasks/assignment-api/Migrations/20990101000000_AddSomethingElse.cs"]
        )


if __name__ == "__main__":
    unittest.main()
