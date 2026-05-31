#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

PROD_COMPOSE_FILES=(
  deploy/prod/compose/00-storage.yaml
  deploy/prod/compose/10-apps-gateway.yaml
  deploy/prod/compose/20-core-services.yaml
  deploy/prod/compose/30-execution.yaml
  deploy/prod/compose/40-ai-and-analyzers.yaml
  deploy/prod/compose/50-integrations.yaml
  deploy/prod/compose/90-certbot.yaml
)

MIGRATION_DIRS=(
  services/identity/api/Migrations
  services/education/api/Migrations
  services/content/api/Migrations
  services/tasks/assignment-api/Migrations
  services/tasks/quiz-api/Migrations
  services/solutions/api/Migrations
  services/execution/api/Migrations
  services/ai/api/Migrations
  services/support/api/Migrations
  services/minecraft/api/Migrations
  services/files/api/Migrations
  services/notifications/api/Migrations
  services/observability/api/Migrations
  services/bots/telegram-quiz-bot/Data/Migrations
)

echo "[1/15] YAML syntax check"
python3 - <<'PY'
from pathlib import Path
import yaml
files=[Path('.github/workflows/develop-build.yml'), *sorted(Path('deploy/dev/compose').glob('*.yaml')), *sorted(Path('deploy/prod/compose').glob('*.yaml'))]
for f in files:
    with f.open('r', encoding='utf-8') as h:
        yaml.safe_load(h)
    print(f'  ok: {f}')
PY

echo "[2/15] dev compose build Dockerfile path check"
python3 - <<'PY'
from pathlib import Path
import yaml
missing=[]
base=Path('deploy/dev/compose')
for f in sorted(base.glob('*.yaml')):
    cfg=yaml.safe_load(f.read_text()) or {}
    for name, svc in (cfg.get('services') or {}).items():
        b=svc.get('build') if isinstance(svc, dict) else None
        if not b:
            continue
        if isinstance(b, str):
            ctx=(f.parent / b).resolve()
            path=ctx/'Dockerfile'
        else:
            ctx=(f.parent / b.get('context', '.')).resolve()
            df=b.get('dockerfile', 'Dockerfile')
            path=ctx/df
        if not path.exists():
            missing.append((name, str(path)))
if missing:
    for item in missing:
        print('  missing:', item)
    raise SystemExit(1)
print('  all dev compose build Dockerfiles exist')
PY

echo "[3/15] production split compose uses images only"
python3 - <<'PY'
from pathlib import Path
import yaml
bad=[]
services=[]
for f in sorted(Path('deploy/prod/compose').glob('*.yaml')):
    cfg=yaml.safe_load(f.read_text()) or {}
    for name, svc in (cfg.get('services') or {}).items():
        services.append(name)
        if isinstance(svc, dict) and 'build' in svc:
            bad.append((str(f), name))
if bad:
    for item in bad:
        print('  build in production:', item)
    raise SystemExit(1)
if len(services) < 20:
    print('  too few production services detected:', services)
    raise SystemExit(1)
print('  ok:', len(services), 'services from split files')
PY

echo "[4/15] compose is split, no giant root/prod compose"
if [ -f deploy/prod/compose.prod.yaml ]; then
  echo "  deploy/prod/compose.prod.yaml still exists"
  exit 1
fi
if [ -f compose.yaml ]; then
  echo "  root compose.yaml still exists"
  exit 1
fi
for f in deploy/dev/compose/00-storage.yaml deploy/dev/compose/10-apps-gateway.yaml deploy/dev/compose/20-core-services.yaml deploy/dev/compose/30-execution.yaml deploy/dev/compose/40-ai-and-analyzers.yaml deploy/dev/compose/50-integrations.yaml; do
  [ -f "$f" ] || { echo "  missing $f"; exit 1; }
done
for f in "${PROD_COMPOSE_FILES[@]}"; do
  [ -f "$f" ] || { echo "  missing $f"; exit 1; }
done
printf '  ok\n'

echo "[5/15] no Python outside image analyzer"
bad=$(find . -name '*.py' ! -path './services/analyzers/image-analyzer/*' | sort)
if [ -n "$bad" ]; then
  echo "$bad"
  exit 1
fi
printf '  ok\n'

echo "[6/15] EF migrations exist for DB-owning services"
for d in "${MIGRATION_DIRS[@]}"; do
  [ -d "$d" ] || { echo "  missing migration dir: $d"; exit 1; }
  snapshot_count=$(find "$d" -maxdepth 1 -name '*ModelSnapshot.cs' | wc -l)
  migration_count=$(find "$d" -maxdepth 1 -name '*.cs' ! -name '*Designer.cs' ! -name '*ModelSnapshot.cs' | wc -l)
  [ "$snapshot_count" -eq 1 ] || { echo "  expected exactly one ModelSnapshot in $d, got $snapshot_count"; exit 1; }
  [ "$migration_count" -ge 1 ] || { echo "  expected at least one migration in $d"; exit 1; }
done
printf '  ok\n'

echo "[7/15] no empty EF migration Up() bodies"
python3 - <<'PY'
from pathlib import Path
import re
bad=[]
files=[]
for root in [Path('services')]:
    files.extend(root.glob('**/Migrations/*.cs'))
    files.extend(root.glob('**/Data/Migrations/*.cs'))
seen=set()
for p in sorted(files):
    if p in seen:
        continue
    seen.add(p)
    if p.name.endswith('Designer.cs') or p.name.endswith('ModelSnapshot.cs'):
        continue
    text=p.read_text(encoding='utf-8')
    m=re.search(r'protected override void Up\(MigrationBuilder migrationBuilder\)\s*\{(?P<body>.*?)\n\s*\}', text, re.S)
    body=m.group('body').strip() if m else ''
    if not body:
        bad.append(str(p))
if bad:
    print('  empty migrations:')
    for x in bad:
        print('   ', x)
    raise SystemExit(1)
print('  ok')
PY

echo "[8/15] no old MIGRATIONS_REQUIRED markers"
bad=$(find services -name MIGRATIONS_REQUIRED.md | sort)
if [ -n "$bad" ]; then
  echo "$bad"
  exit 1
fi
printf '  ok\n'

echo "[9/15] .dockerignore exists and ignores heavy local artifacts"
python3 - <<'PY'
from pathlib import Path
p=Path('.dockerignore')
if not p.exists():
    raise SystemExit('  missing .dockerignore')
text=p.read_text()
required=['**/bin','**/obj','**/node_modules','deploy/dev/logs','deploy/prod/logs','*.zip','.env','!**/Migrations/**']
missing=[x for x in required if x not in text]
if missing:
    print('  .dockerignore missing patterns:', missing)
    raise SystemExit(1)
print('  ok')
PY

echo "[10/15] extracted monolith sources are excluded from compiled microservices"
python3 - <<'PY'
from pathlib import Path
bad=[]
for extracted in Path('services').glob('**/extracted'):
    csproj=list(extracted.parent.glob('*.csproj'))
    if not csproj:
        bad.append((str(extracted), 'no csproj beside extracted'))
        continue
    text='\n'.join(p.read_text() for p in csproj)
    if 'Compile Remove="extracted/**/*.cs"' not in text:
        bad.append((str(extracted), 'missing Compile Remove'))
if bad:
    for item in bad:
        print('  bad:', item)
    raise SystemExit(1)
print('  ok')
PY

echo "[11/15] csproj XML syntax"
python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET
for p in Path('.').glob('**/*.csproj'):
    ET.parse(p)
print('  ok')
PY

echo "[12/15] Go runner tests"
for d in services/execution/runners/*; do
  if [ -f "$d/go.mod" ]; then
    echo "  go test $d"
    (cd "$d" && go test ./...)
  fi
done

echo "[13/15] every compose service has a healthcheck"
python3 - <<'PY'
from pathlib import Path
import yaml
for env in ['dev','prod']:
    missing=[]
    total=0
    for f in sorted(Path(f'deploy/{env}/compose').glob('*.yaml')):
        cfg=yaml.safe_load(f.read_text()) or {}
        for name, svc in (cfg.get('services') or {}).items():
            total += 1
            if not isinstance(svc, dict) or 'healthcheck' not in svc:
                missing.append((str(f), name))
    if missing:
        print(f'  {env} missing healthchecks:')
        for item in missing:
            print('   ', item)
        raise SystemExit(1)
    print(f'  {env}: {total} services have healthchecks')
PY

echo "[14/15] frontend does not expose technical transport errors"
python3 - <<'PY_FRONT'
from pathlib import Path
import re
bad=[]
patterns=[
    re.compile(r'Request failed with status code', re.I),
    re.compile(r'Network Error', re.I),
    re.compile(r'Адрес API не найден', re.I),
    re.compile(r'nginx route', re.I),
    re.compile(r'Открой логи backend', re.I),
]
for p in list(Path('apps/web/src').glob('**/*.[jt]s*')) + list(Path('apps/web-ct/src').glob('**/*.[jt]s*')):
    if p.as_posix().endswith('/api/http.js'):
        continue
    text=p.read_text(encoding='utf-8', errors='ignore')
    for pat in patterns:
        if pat.search(text):
            bad.append((str(p), pat.pattern))
if bad:
    print('  technical user-facing messages found:')
    for item in bad:
        print('   ', item)
    raise SystemExit(1)
print('  ok')
PY_FRONT

echo "[15/15] production image names match CI matrix"
python3 - <<'PY'
from pathlib import Path
import re, yaml
workflow = Path('.github/workflows/develop-build.yml').read_text()
ci = set(re.findall(r'add ([a-z0-9-]+) ', workflow))
prod = set()
for f in sorted(Path('deploy/prod/compose').glob('*.yaml')):
    cfg = yaml.safe_load(f.read_text()) or {}
    for name, svc in (cfg.get('services') or {}).items():
        if not isinstance(svc, dict):
            continue
        img = svc.get('image', '')
        m = re.search(r'}/([^:$]+):', img)
        if m:
            prod.add(m.group(1))
missing = sorted(prod - ci)
extra = sorted(x for x in ci - prod if x not in {'certbot'})
if missing or extra:
    print('missing in CI:', missing)
    print('CI not in prod:', extra)
    raise SystemExit(1)
print('  ok')
PY

echo "verification ok"
