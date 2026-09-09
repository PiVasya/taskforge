#!/usr/bin/env python3
"""Regression tests for the dependency guard. Fixtures are not real NuGet restores."""
from __future__ import annotations

import importlib.util
import json
import shutil
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('sql_ef_dependencies', HERE / 'check-sql-ef-dependencies.py')
assert SPEC and SPEC.loader
CHECK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECK)


class DependencyGuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix='taskforge-ef-guard-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in (*CHECK.APIS, CHECK.CHECK_PROJECT, '.config/dotnet-tools.json'):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(CHECK.ROOT / name, path)
        self.version = '10.0.8'

    def edit_reference(self, package: str, change, api: str | None = None) -> None:
        path = self.root / (api or CHECK.APIS[0])
        tree = ET.parse(path)
        item = next(x for x in tree.findall('.//PackageReference') if x.get('Include') == package)
        change(tree, item)
        tree.write(path, encoding='utf-8')

    def remove_reference(self, package: str) -> None:
        def remove(tree, item) -> None:
            parent = next(x for x in tree.iter() if item in list(x))
            parent.remove(item)
        self.edit_reference(package, remove)

    def assets(self, project: str = CHECK.CHECK_PROJECT, version: str | None = None) -> Path:
        path = (self.root / project).parent / 'obj/project.assets.json'
        path.parent.mkdir(parents=True, exist_ok=True)
        libraries = {f'{name}/{version or self.version}': {'type': 'package'} for name in CHECK.RESOLVED_PACKAGES}
        path.write_text(json.dumps({'targets': {'net10.0': libraries}}), encoding='utf-8')
        return path

    def all_assets(self) -> None:
        for name in (*CHECK.APIS, CHECK.CHECK_PROJECT):
            self.assets(name)

    def test_current_source_pins_are_public_and_aligned(self) -> None:
        self.assertEqual(CHECK.check_sources(self.root), self.version)

    def test_no_core_pin_is_rejected(self) -> None:
        self.remove_reference('Microsoft.EntityFrameworkCore')
        with self.assertRaisesRegex(ValueError, 'missing explicit'):
            CHECK.check_sources(self.root)

    def test_no_relational_pin_is_rejected(self) -> None:
        self.remove_reference('Microsoft.EntityFrameworkCore.Relational')
        with self.assertRaisesRegex(ValueError, 'missing explicit'):
            CHECK.check_sources(self.root)

    def test_lower_runtime_version_is_rejected_in_every_api(self) -> None:
        for api in CHECK.APIS:
            with self.subTest(api=api):
                self.edit_reference('Microsoft.EntityFrameworkCore', lambda tree, x: x.set('Version', '10.0.4'), api)
                with self.assertRaisesRegex(ValueError, 'must be 10.0.8'):
                    CHECK.check_sources(self.root)
                self.edit_reference('Microsoft.EntityFrameworkCore', lambda tree, x: x.set('Version', self.version), api)

    def test_relational_drift_is_rejected(self) -> None:
        self.edit_reference('Microsoft.EntityFrameworkCore.Relational', lambda tree, x: x.set('Version', '10.0.4'))
        with self.assertRaisesRegex(ValueError, 'must be 10.0.8'):
            CHECK.check_sources(self.root)

    def test_private_runtime_pin_is_rejected(self) -> None:
        self.edit_reference('Microsoft.EntityFrameworkCore', lambda tree, x: x.set('PrivateAssets', 'all'))
        with self.assertRaisesRegex(ValueError, 'blocks transitive'):
            CHECK.check_sources(self.root)

    def test_excluded_runtime_assets_are_rejected(self) -> None:
        self.edit_reference('Microsoft.EntityFrameworkCore.Relational', lambda tree, x: x.set('ExcludeAssets', 'runtime'))
        with self.assertRaisesRegex(ValueError, 'blocks transitive'):
            CHECK.check_sources(self.root)

    def test_runtime_only_include_lacks_compile(self) -> None:
        self.edit_reference('Microsoft.EntityFrameworkCore', lambda tree, x: x.set('IncludeAssets', 'runtime'))
        with self.assertRaisesRegex(ValueError, 'must include compile'):
            CHECK.check_sources(self.root)

    def test_public_design_package_is_rejected(self) -> None:
        self.edit_reference(CHECK.DESIGN_PACKAGE, lambda tree, x: x.find('PrivateAssets').__setattr__('text', 'none'))
        with self.assertRaisesRegex(ValueError, 'Design must stay private'):
            CHECK.check_sources(self.root)

    def test_cli_must_match_api_design_and_runtime(self) -> None:
        path = self.root / '.config/dotnet-tools.json'
        data = json.loads(path.read_text())
        data['tools']['dotnet-ef']['version'] = '10.0.9'
        path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'must be 10.0.9'):
            CHECK.check_sources(self.root)

    def test_all_three_project_references_are_required(self) -> None:
        path = self.root / CHECK.CHECK_PROJECT
        tree = ET.parse(path)
        item = tree.find('.//ProjectReference')
        assert item is not None
        item.set('Include', '../wrong.csproj')
        tree.write(path)
        with self.assertRaisesRegex(ValueError, 'exactly the three'):
            CHECK.check_sources(self.root)

    def test_provider_versions_must_match(self) -> None:
        self.edit_reference(CHECK.PROVIDER_PACKAGE, lambda tree, x: x.set('Version', '10.0.1'))
        with self.assertRaisesRegex(ValueError, 'different Npgsql'):
            CHECK.check_sources(self.root)

    def test_aligned_restored_graph_fixture_passes(self) -> None:
        self.all_assets()
        CHECK.check_assets(self.root, self.version)

    def test_reported_10_0_4_consumer_regression_is_rejected(self) -> None:
        self.all_assets()
        self.assets(version='10.0.4')
        with self.assertRaisesRegex(ValueError, 'restored Microsoft.EntityFrameworkCore is'):
            CHECK.check_assets(self.root, self.version)

    def test_each_api_restored_graph_is_checked(self) -> None:
        self.all_assets()
        for api in CHECK.APIS:
            with self.subTest(api=api):
                self.assets(api, version='10.0.4')
                with self.assertRaises(ValueError):
                    CHECK.check_assets(self.root, self.version)
                self.assets(api)

    def test_missing_restore_is_not_success(self) -> None:
        with self.assertRaisesRegex(ValueError, 'restore output missing'):
            CHECK.check_assets(self.root, self.version)

    def test_restore_errors_are_not_success(self) -> None:
        self.all_assets()
        path = self.assets()
        data = json.loads(path.read_text())
        data['logs'] = [{'level': 'Error', 'code': 'NU1101'}]
        path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'restore output contains errors'):
            CHECK.check_assets(self.root, self.version)

    def test_missing_abstractions_is_not_success(self) -> None:
        self.all_assets()
        path = self.assets()
        data = json.loads(path.read_text())
        del data['targets']['net10.0'][f'Microsoft.EntityFrameworkCore.Abstractions/{self.version}']
        path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'Abstractions'):
            CHECK.check_assets(self.root, self.version)

    def test_rid_specific_graph_is_checked(self) -> None:
        self.all_assets()
        path = self.assets()
        data = json.loads(path.read_text())
        data['targets']['net10.0/linux-x64'] = dict(data['targets']['net10.0'])
        path.write_text(json.dumps(data))
        CHECK.check_assets(self.root, self.version)
        data['targets']['net10.0/linux-x64']['Microsoft.EntityFrameworkCore/10.0.4'] = {'type': 'package'}
        path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'linux-x64'):
            CHECK.check_assets(self.root, self.version)


if __name__ == '__main__':
    unittest.main(verbosity=2)
