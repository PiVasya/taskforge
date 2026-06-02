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

echo "[1/24] YAML syntax check"
python3 - <<'PY'
from pathlib import Path
import yaml
files=[Path('.github/workflows/develop-build.yml'), *sorted(Path('deploy/dev/compose').glob('*.yaml')), *sorted(Path('deploy/prod/compose').glob('*.yaml'))]
for f in files:
    with f.open('r', encoding='utf-8') as h:
        yaml.safe_load(h)
    print(f'  ok: {f}')
PY

echo "[2/24] dev compose build Dockerfile path check"
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

echo "[3/24] production split compose uses images only"
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

echo "[4/24] compose is split, no giant root/prod compose"
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

echo "[5/24] no Python outside image analyzer"
bad=$(find . -name '*.py' ! -path './services/analyzers/image-analyzer/*' | sort)
if [ -n "$bad" ]; then
  echo "$bad"
  exit 1
fi
printf '  ok\n'

echo "[6/24] EF migrations exist for DB-owning services"
for d in "${MIGRATION_DIRS[@]}"; do
  [ -d "$d" ] || { echo "  missing migration dir: $d"; exit 1; }
  snapshot_count=$(find "$d" -maxdepth 1 -name '*ModelSnapshot.cs' | wc -l)
  migration_count=$(find "$d" -maxdepth 1 -name '*.cs' ! -name '*Designer.cs' ! -name '*ModelSnapshot.cs' | wc -l)
  [ "$snapshot_count" -eq 1 ] || { echo "  expected exactly one ModelSnapshot in $d, got $snapshot_count"; exit 1; }
  [ "$migration_count" -ge 1 ] || { echo "  expected at least one migration in $d"; exit 1; }
done
printf '  ok\n'

echo "[7/24] no empty EF migration Up() bodies"
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

echo "[8/24] no old MIGRATIONS_REQUIRED markers"
bad=$(find services -name MIGRATIONS_REQUIRED.md | sort)
if [ -n "$bad" ]; then
  echo "$bad"
  exit 1
fi
printf '  ok\n'

echo "[9/24] .dockerignore exists and ignores heavy local artifacts"
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

echo "[10/24] extracted monolith sources are excluded from compiled microservices"
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

echo "[11/24] csproj XML syntax"
python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET
for p in Path('.').glob('**/*.csproj'):
    ET.parse(p)
print('  ok')
PY

echo "[12/24] Go runner module check"
if [ "${TASKFORGE_RUN_GO_TESTS:-}" = "1" ]; then
  command -v go >/dev/null 2>&1 || { echo "  go is required when TASKFORGE_RUN_GO_TESTS=1"; exit 1; }
  for d in services/execution/runners/*; do
    if [ -f "$d/go.mod" ]; then
      echo "  go test $d"
      (cd "$d" && go test ./...)
    fi
  done
else
  count=$(find services/execution/runners -mindepth 2 -maxdepth 2 -name go.mod | wc -l)
  [ "$count" -ge 5 ] || { echo "  expected Go runner modules, got $count"; exit 1; }
  echo "  ok: $count Go runner modules found"
  echo "  set TASKFORGE_RUN_GO_TESTS=1 to run go test locally/CI"
fi

echo "[13/24] every compose service has a healthcheck"
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

echo "[14/24] frontend does not expose technical transport errors"
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

echo "[15/24] production image names match CI image manifest"
python3 - <<'PY'
from pathlib import Path
import re, yaml
workflow = Path('.github/workflows/develop-build.yml').read_text()
ci = set(re.findall(r'^\s*"([a-z0-9-]+)\|', workflow, re.M))
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
if len(ci) != 30:
    print('expected 30 custom build images in CI manifest, got', len(ci))
    print(sorted(ci))
    raise SystemExit(1)
print('  ok:', len(ci), 'custom images')
PY


echo "[16/24] GitHub Actions selective build is image-level, not domain-level"
python3 - <<'PY'
from pathlib import Path
text = Path('.github/workflows/develop-build.yml').read_text()
for broad in ['services/tasks/**', 'services/execution/**', 'services/ai/**', 'services/bots/**', 'services/solutions/**']:
    if broad in text:
        raise SystemExit(f'  broad path filter still exists: {broad}')
if 'dorny/paths-filter' in text:
    raise SystemExit('  dorny/paths-filter is still used; use explicit image manifest selection instead')
if 'images=(' not in text:
    raise SystemExit('  workflow does not define the image manifest')
if 'workflow_dispatch:' not in text or 'build_all:' not in text or 'images:' not in text:
    raise SystemExit('  workflow must support manual full build and manual selected images')
print('  ok')
PY


echo "[17/24] GitHub Actions GHCR tags are lowercase-safe"
python3 - <<'PY'
from pathlib import Path
text = Path('.github/workflows/develop-build.yml').read_text()
if 'ghcr.io/${{ github.repository }}' in text:
    raise SystemExit('  workflow uses github.repository directly in GHCR tags; owner/repo may contain uppercase characters')
if '${GITHUB_REPOSITORY,,}' not in text:
    raise SystemExit('  workflow does not normalize GITHUB_REPOSITORY to lowercase before building GHCR tags')
if 'steps.image.outputs.prefix' not in text:
    raise SystemExit('  workflow tags do not use the prepared lowercase image prefix')
print('  ok')
PY


echo "[18/24] gateway preserves browser origin and has one source of routing truth"
python3 - <<'PY_GATEWAY'
from pathlib import Path
compose = Path('deploy/dev/compose/10-apps-gateway.yaml').read_text()
if 'GATEWAY_MODE: dev' not in compose:
    raise SystemExit('  dev gateway must use GATEWAY_MODE: dev to serve localhost correctly')
dockerfile = Path('apps/gateway/Dockerfile').read_text()
if 'COPY snippets/' not in dockerfile:
    raise SystemExit('  gateway Dockerfile must copy nginx snippets')
proxy_common = Path('apps/gateway/snippets/proxy-common.conf').read_text()
required = [
    'proxy_set_header Host $http_host;',
    'proxy_set_header X-Forwarded-Host $http_host;',
    'proxy_redirect ~^https?://[^/]+(/.*)$ $forwarded_proto://$http_host$1;',
]
for item in required:
    if item not in proxy_common:
        raise SystemExit(f'  proxy-common.conf missing: {item}')

api_routes = Path('apps/gateway/snippets/api-routes.conf').read_text()
if 'location ^~ /api/' in api_routes:
    raise SystemExit('  api fallback must not use ^~ because it preempts regex service routes')
if 'location /api/ {' not in api_routes:
    raise SystemExit('  api-routes.conf must contain a non-^~ /api/ fallback')
hub_routes = Path('apps/gateway/snippets/hub-routes.conf').read_text()
for hub in ['/hubs/agent', '/hubs/support', '/hubs/minecraft-chat']:
    marker = f'location {hub}'
    idx = hub_routes.find(marker)
    if idx < 0:
        raise SystemExit(f'  missing hub route: {hub}')
    block = hub_routes[idx:hub_routes.find('location ', idx + 1) if hub_routes.find('location ', idx + 1) >= 0 else len(hub_routes)]
    if 'include /etc/nginx/snippets/proxy-common.conf;' not in block:
        raise SystemExit(f'  hub route {hub} must include proxy-common.conf explicitly')
prod_compose = Path('deploy/prod/compose/10-apps-gateway.yaml').read_text()
if 'GATEWAY_MODE: ${GATEWAY_MODE:-auto}' not in prod_compose:
    raise SystemExit('  prod gateway compose default must be auto, matching production docs')
for p in Path('apps/gateway/templates').glob('*.conf'):
    text = p.read_text()
    if 'proxy_set_header Host $host;' in text:
        raise SystemExit(f'  {p} uses $host and can drop dev port; use proxy-common snippet')
    if p.name != 'bootstrap.conf' and 'include /etc/nginx/snippets/api-routes.conf;' not in text:
        raise SystemExit(f'  {p} must include api-routes.conf')
    if 'Legacy-compatible split routes' in text:
        raise SystemExit(f'  {p} still contains duplicated legacy route block')
for p in [Path('apps/web/Caddyfile'), Path('apps/web-ct/Caddyfile')]:
    text = p.read_text()
    if 'try_files {path} {path}/ /index.html' in text:
        raise SystemExit(f'  {p} can canonical-redirect SPA routes; remove {{path}}/')
print('  ok')
PY_GATEWAY



echo "[19/24] files-api uses MinIO/S3, not local disk storage"
python3 - <<'PY_FILES'
from pathlib import Path
import yaml
program = Path('services/files/api/Program.cs').read_text()
csproj = Path('services/files/api/TaskForge.Files.Api.csproj').read_text()
if 'AWSSDK.S3' not in csproj or 'IAmazonS3' not in program or 'PutObjectAsync' not in program or 'GetObjectAsync' not in program:
    raise SystemExit('  files-api must use AWSSDK.S3 / IAmazonS3 PutObject/GetObject')
if 'Storage__LocalPath' in ''.join(p.read_text() for p in Path('deploy').glob('**/*.yaml')):
    raise SystemExit('  compose still passes Storage__LocalPath; files-api must use MinIO/S3')
for f in [Path('deploy/dev/compose/50-integrations.yaml'), Path('deploy/prod/compose/50-integrations.yaml')]:
    cfg=yaml.safe_load(f.read_text()) or {}
    files=(cfg.get('services') or {}).get('files-api') or {}
    env=files.get('environment') or {}
    for key in ['S3__Endpoint','S3__AccessKey','S3__SecretKey','S3__Bucket','S3__Region','S3__UsePathStyle']:
        if key not in env:
            raise SystemExit(f'  {f} files-api missing {key}')
    if 'volumes' in files:
        raise SystemExit(f'  {f} files-api still mounts local file storage volume')
print('  ok')
PY_FILES


echo "[20/24] no user-facing 501 Not Implemented responses"
python3 - <<'PY_501'
from pathlib import Path
bad=[]
for p in Path('services').glob('**/*.cs'):
    text=p.read_text(encoding='utf-8', errors='ignore')
    if 'Status501NotImplemented' in text or 'status = 501' in text:
        bad.append(str(p))
if bad:
    print('  501 responses found:')
    for item in bad:
        print('   ', item)
    raise SystemExit(1)
print('  ok')
PY_501

echo "[21/24] production env does not fall back to localhost public URLs"
python3 - <<'PY_PROD_ORIGIN'
from pathlib import Path
prod_env = Path('deploy/prod/.env.example').read_text()
for item in ['DOMAIN=taskforge.by', 'CT_DOMAIN=ct.taskforge.by', 'GATEWAY_MODE=auto', 'S3_PUBLIC_ENDPOINT=https://s3.taskforge.by']:
    if item not in prod_env:
        raise SystemExit(f'  deploy/prod/.env.example missing or wrong: {item}')
for p in Path('deploy/prod/compose').glob('*.yaml'):
    text = p.read_text()
    if 'S3__PublicEndpoint: ${S3_PUBLIC_ENDPOINT:-http://localhost:9000}' in text:
        raise SystemExit(f'  {p} has unsafe localhost fallback for S3__PublicEndpoint')
print('  ok')
PY_PROD_ORIGIN

echo "[22/24] admin bootstrap is explicit"
python3 - <<'PY_ADMIN_BOOTSTRAP'
from pathlib import Path
identity = Path('services/identity/api/Program.cs').read_text()
if 'ResolveInitialRole' not in identity or 'Bootstrap:AdminEmails' not in identity or 'Bootstrap:FirstUserIsAdmin' not in identity:
    raise SystemExit('  identity registration must use explicit Bootstrap admin settings')
if 'Role = firstUser ? "Admin" : "User"' in identity:
    raise SystemExit('  hidden first-user-admin behavior is still present')
for p in [Path('deploy/dev/.env.example'), Path('deploy/prod/.env.example')]:
    text = p.read_text()
    if 'BOOTSTRAP_FIRST_USER_IS_ADMIN=false' not in text or 'BOOTSTRAP_ADMIN_EMAILS=' not in text:
        raise SystemExit(f'  {p} must document explicit admin bootstrap variables')
for p in [Path('deploy/dev/compose/20-core-services.yaml'), Path('deploy/prod/compose/20-core-services.yaml')]:
    text = p.read_text()
    if 'Bootstrap__FirstUserIsAdmin' not in text or 'Bootstrap__AdminEmails' not in text:
        raise SystemExit(f'  {p} must pass Bootstrap admin settings to identity-api')
print('  ok')
PY_ADMIN_BOOTSTRAP


echo "[23/24] no active backend stub responses in compiled services"
python3 - <<'PY_STUBS'
from pathlib import Path
patterns = [
    'FeatureUnavailable(',
    'OperationUnavailable(',
    'NOT_WIRED',
    'PENDING_PORT',
    'PIPELINE_NOT_READY',
    'NotImplemented',
    'Status501NotImplemented',
    'Results.Ok(Array.Empty<object>())',
    'passed = true, similarity = 1.0',
]
bad=[]
for p in Path('services').glob('**/*.cs'):
    if '/extracted/' in p.as_posix() or '/tests/' in p.as_posix():
        continue
    text=p.read_text(encoding='utf-8', errors='ignore')
    for pattern in patterns:
        if pattern in text:
            bad.append((str(p), pattern))
if bad:
    for file, pattern in bad:
        print(f'  active stub marker: {file}: {pattern}')
    raise SystemExit(1)
print('  ok')
PY_STUBS


echo "[24/24] migration scripts build projects before deciding/no-op"
python3 - <<'PY_MIGRATION_SCRIPTS'
from pathlib import Path
for script in [Path('scripts/generate-migrations.sh'), Path('scripts/generate-migrations-force.sh')]:
    text = script.read_text()
    if 'dotnet build "$PROJECT"' not in text:
        raise SystemExit(f'  {script} must build each project')
    if script.name == 'generate-migrations.sh' and 'has-pending-model-changes' in text and '--no-build' not in text:
        raise SystemExit(f'  {script} should use --no-build after explicit dotnet build')
print('  ok')
PY_MIGRATION_SCRIPTS

echo "verification ok"
