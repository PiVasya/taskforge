#!/usr/bin/env python3
"""Small YAML compatibility layer for repository CI checks.

CI guards only need to read trusted repository YAML. Prefer PyYAML when it is
already available, but never require a network install just to run structural
checks. GitHub Ubuntu runners include Ruby/Psych, which is used as a zero-download
fallback and converted to JSON for Python.
"""
from __future__ import annotations

import json
import shutil
import subprocess
from typing import Any


class YamlLoadError(RuntimeError):
    pass


def _load_with_pyyaml(text: str) -> Any:
    try:
        import yaml  # type: ignore
    except ModuleNotFoundError:
        raise
    try:
        return yaml.safe_load(text)
    except Exception as exc:  # PyYAML exception types are optional here.
        raise YamlLoadError(str(exc)) from exc


def _load_with_ruby(text: str) -> Any:
    ruby = shutil.which("ruby")
    if not ruby:
        raise YamlLoadError(
            "no YAML parser is available: install PyYAML or provide Ruby/Psych"
        )

    program = r'''
require "json"
require "yaml"
begin
  value = YAML.safe_load(STDIN.read, permitted_classes: [], permitted_symbols: [], aliases: true)
  STDOUT.write(JSON.generate(value))
rescue StandardError => e
  STDERR.write("#{e.class}: #{e.message}")
  exit 2
end
'''
    result = subprocess.run(
        [ruby, "-e", program],
        input=text,
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or f"ruby exited with {result.returncode}"
        raise YamlLoadError(detail)
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise YamlLoadError(f"Ruby YAML bridge returned invalid JSON: {exc}") from exc


def safe_load(text: str) -> Any:
    try:
        return _load_with_pyyaml(text)
    except ModuleNotFoundError:
        return _load_with_ruby(text)
