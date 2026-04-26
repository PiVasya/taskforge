#!/usr/bin/env bash
set -euo pipefail

dotnet build taskforge/taskforge.csproj --no-restore
/usr/bin/python3 -m py_compile $(find taskforge-ai-worker-external -name '*.py' -print)
