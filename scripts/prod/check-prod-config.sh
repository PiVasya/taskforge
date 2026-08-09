#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"
ENV_FILE="${TASKFORGE_PROD_ENV_FILE:-deploy/prod/.env}"
EXAMPLE="${TASKFORGE_PROD_ENV_EXAMPLE:-deploy/prod/.env.example}"

[ -f "$ENV_FILE" ] || { echo "error: missing $ENV_FILE" >&2; exit 2; }
[ -f "$EXAMPLE" ] || { echo "error: missing $EXAMPLE" >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "error: python3 is required" >&2; exit 2; }

python3 - "$ENV_FILE" "$EXAMPLE" <<'PY'
from __future__ import annotations
from pathlib import Path
import os, re, sys

env_path=Path(sys.argv[1]); example_path=Path(sys.argv[2])
key_re=re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')

def parse(path:Path):
    values={}; dup=[]
    for raw in path.read_text(encoding='utf-8-sig').splitlines():
        if not raw or raw.lstrip().startswith('#') or '=' not in raw: continue
        k,v=raw.split('=',1); k=k.strip()
        if k.startswith('export '): k=k[7:].strip()
        if not key_re.fullmatch(k): continue
        if k in values: dup.append(k)
        values[k]=v.strip()
    return values,sorted(set(dup))

env,dup=parse(env_path); example,template_dup=parse(example_path)
errors=[]
if template_dup: errors.append('duplicate keys in template: '+', '.join(template_dup))
if dup: errors.append('duplicate keys in env: '+', '.join(dup))
missing=sorted(set(example)-set(env))
if missing: errors.append('missing env keys: '+', '.join(missing))

def required(key):
    value=env.get(key,'')
    if not value: errors.append(f'{key} is empty'); return ''
    if value.endswith('@example.com'): errors.append(f'{key} still contains an example address')
    upper=value.upper()
    if 'CHANGE_ME' in upper or 'DEV_CHANGE_ME' in upper:
        errors.append(f'{key} still contains a placeholder')
    return value

def minlen(key,n):
    value=required(key)
    if value and len(value)<n: errors.append(f'{key} must be at least {n} characters')

def positive_int(key):
    value=env.get(key,'')
    try:
        if int(value)<=0: raise ValueError
    except ValueError: errors.append(f'{key} must be a positive integer')

def positive_number(key):
    value=env.get(key,'')
    try:
        if float(value)<=0: raise ValueError
    except ValueError: errors.append(f'{key} must be a positive number')

for key in ('IMAGE_REPOSITORY','IMAGE_TAG','DOMAIN','CT_DOMAIN','LETSENCRYPT_EMAIL',
            'S3_PUBLIC_ENDPOINT','BOOTSTRAP_ADMIN_EMAILS','BROWSER_MAIN_ORIGIN',
            'BROWSER_CT_ORIGIN','BROWSER_MEM_LIMIT','BROWSER_SHM_SIZE','BROWSER_TMPFS_SIZE'):
    required(key)
for key,n in {
    'JWT_SIGNING_KEY':64, 'TASKFORGE_INTERNAL_KEY':40, 'TASKFORGE_AGENT_INTERNAL_KEY':40,
    'POSTGRES_PASSWORD':24, 'RABBITMQ_DEFAULT_PASS':24, 'REDIS_PASSWORD':24,
    'MINIO_ROOT_PASSWORD':24, 'ANALYTICS_IP_HASH_SALT':32,
    'MINECRAFT_PLUGIN_KEY':32, 'MINECRAFT_WEBHOOK_KEY':32,
}.items(): minlen(key,n)
for key in ('MINECRAFT_DEATH_COORDINATES_COST','MINECRAFT_DEATH_CHEST_COST',
            'MINECRAFT_DEATH_TELEPORT_COST','MINECRAFT_DEATH_INVENTORY_COST',
            'RUNNER_PIDS_LIMIT','CODE_ANALYZER_PIDS_LIMIT','WATCHTOWER_POLL_INTERVAL',
            'BROWSER_NAVIGATION_TIMEOUT_SECONDS','BROWSER_ACTION_TIMEOUT_SECONDS',
            'BROWSER_DEFAULT_WAIT_MILLISECONDS','BROWSER_AGENT_CAPTURE_WAIT_MILLISECONDS','BROWSER_MAX_WAIT_MILLISECONDS',
            'BROWSER_MIN_VIEWPORT_WIDTH','BROWSER_MAX_VIEWPORT_WIDTH',
            'BROWSER_MIN_VIEWPORT_HEIGHT','BROWSER_MAX_VIEWPORT_HEIGHT',
            'BROWSER_MAX_FULL_PAGE_HEIGHT','BROWSER_MAX_SCREENSHOT_PIXELS',
            'BROWSER_MAX_SNAPSHOT_ELEMENTS','BROWSER_MAX_SNAPSHOT_TEXT_CHARACTERS',
            'BROWSER_MAX_ARIA_SNAPSHOT_CHARACTERS','BROWSER_ARIA_SNAPSHOT_DEPTH',
            'BROWSER_MAX_EVENT_ENTRIES','BROWSER_MAX_CACHED_ARTIFACT_BYTES',
            'BROWSER_MAX_ARTIFACT_RESPONSE_BYTES',
            'BROWSER_METADATA_LIMIT','BROWSER_METADATA_WINDOW_SECONDS',
            'BROWSER_SNAPSHOT_LIMIT','BROWSER_SNAPSHOT_WINDOW_SECONDS',
            'BROWSER_RENDER_LIMIT','BROWSER_RENDER_WINDOW_SECONDS',
            'BROWSER_SESSION_CREATE_LIMIT','BROWSER_SESSION_CREATE_WINDOW_SECONDS',
            'BROWSER_SESSION_ACTION_LIMIT','BROWSER_SESSION_ACTION_WINDOW_SECONDS',
            'BROWSER_RATE_NETWORK_MULTIPLIER','BROWSER_MAX_CONCURRENT_OPERATIONS',
            'BROWSER_MAX_ACTIVE_SESSIONS','BROWSER_MAX_ANONYMOUS_SESSIONS_PER_OWNER',
            'BROWSER_MAX_AUTHENTICATED_SESSIONS_PER_OWNER','BROWSER_SESSION_IDLE_MINUTES',
            'BROWSER_SESSION_ABSOLUTE_MINUTES','BROWSER_PUBLIC_CACHE_SECONDS',
            'BROWSER_PIDS_LIMIT'):
    positive_int(key)
for key in ('RUNNER_CPUS','CODE_ANALYZER_CPUS','BROWSER_CPUS'):
    positive_number(key)

if env.get('ASPNETCORE_ENVIRONMENT')!='Production': errors.append('ASPNETCORE_ENVIRONMENT must be Production')
if env.get('ENSURE_CREATED')!='false': errors.append('ENSURE_CREATED must be false')
if env.get('BOOTSTRAP_FIRST_USER_IS_ADMIN')!='false': errors.append('BOOTSTRAP_FIRST_USER_IS_ADMIN must be false')
if env.get('TASKFORGE_DEBUG_LOGS')!='1': errors.append('TASKFORGE_DEBUG_LOGS must remain 1 while the project logging policy is development diagnostics')
if env.get('DOMAIN')==env.get('CT_DOMAIN'): errors.append('DOMAIN and CT_DOMAIN must be different')
if not env.get('MINECRAFT_WEBHOOK_SEND_CODE_PATH','').startswith('/'):
    errors.append('MINECRAFT_WEBHOOK_SEND_CODE_PATH must start with /')
if env.get('TASKFORGE_NODE_ROLE','primary') not in {'primary','standby'}:
    errors.append('TASKFORGE_NODE_ROLE must be primary or standby')
if env.get('BROWSER_RATE_LIMITS_ENABLED')!='true': errors.append('BROWSER_RATE_LIMITS_ENABLED must be true')
if env.get('BROWSER_IGNORE_HTTPS_ERRORS')!='false': errors.append('BROWSER_IGNORE_HTTPS_ERRORS must be false in production')
if env.get('BROWSER_REDUCE_MOTION') not in {'true','false'}: errors.append('BROWSER_REDUCE_MOTION must be true or false')
for key in ('BROWSER_MAIN_ORIGIN','BROWSER_CT_ORIGIN'):
    value=env.get(key,'')
    if value and not value.startswith('https://'): errors.append(f'{key} must use https:// in production')
for value in re.split(r'[,;\s]+',env.get('BROWSER_ALLOWED_EXTERNAL_ORIGINS','').strip()):
    if value and not value.startswith('https://'):
        errors.append('every BROWSER_ALLOWED_EXTERNAL_ORIGINS entry must use https://')
try:
    if int(env.get('BROWSER_MIN_VIEWPORT_WIDTH','0')) > int(env.get('BROWSER_MAX_VIEWPORT_WIDTH','0')):
        errors.append('BROWSER_MIN_VIEWPORT_WIDTH cannot exceed BROWSER_MAX_VIEWPORT_WIDTH')
    if int(env.get('BROWSER_MIN_VIEWPORT_HEIGHT','0')) > int(env.get('BROWSER_MAX_VIEWPORT_HEIGHT','0')):
        errors.append('BROWSER_MIN_VIEWPORT_HEIGHT cannot exceed BROWSER_MAX_VIEWPORT_HEIGHT')
    if int(env.get('BROWSER_SESSION_IDLE_MINUTES','0')) > int(env.get('BROWSER_SESSION_ABSOLUTE_MINUTES','0')):
        errors.append('BROWSER_SESSION_IDLE_MINUTES cannot exceed BROWSER_SESSION_ABSOLUTE_MINUTES')
    if int(env.get('BROWSER_MAX_ANONYMOUS_SESSIONS_PER_OWNER','0')) > int(env.get('BROWSER_MAX_ACTIVE_SESSIONS','0')):
        errors.append('BROWSER_MAX_ANONYMOUS_SESSIONS_PER_OWNER cannot exceed BROWSER_MAX_ACTIVE_SESSIONS')
    if int(env.get('BROWSER_MAX_AUTHENTICATED_SESSIONS_PER_OWNER','0')) > int(env.get('BROWSER_MAX_ACTIVE_SESSIONS','0')):
        errors.append('BROWSER_MAX_AUTHENTICATED_SESSIONS_PER_OWNER cannot exceed BROWSER_MAX_ACTIVE_SESSIONS')
    if int(env.get('BROWSER_MAX_CACHED_ARTIFACT_BYTES','0')) > int(env.get('BROWSER_MAX_ARTIFACT_RESPONSE_BYTES','0')):
        errors.append('BROWSER_MAX_CACHED_ARTIFACT_BYTES cannot exceed BROWSER_MAX_ARTIFACT_RESPONSE_BYTES')
except ValueError:
    pass
repo=env.get('IMAGE_REPOSITORY','')
if 'CHANGE_ME' in repo.upper() or not repo.startswith('ghcr.io/'):
    errors.append('IMAGE_REPOSITORY must point to ghcr.io')
for key in ('CODE_ANALYZER_PRIVATE_KEY_PATH','CODE_ANALYZER_PUBLIC_KEY_PATH'):
    value=required(key)
    if value and not Path(value).is_file(): errors.append(f'{key} does not exist: {value}')

bot=env.get('SUPPORT_BOT_TOKEN',''); group=env.get('SUPPORT_BOT_GROUP_ID','')
if bot and not group: errors.append('SUPPORT_BOT_GROUP_ID is required when SUPPORT_BOT_TOKEN is set')
if group and not re.fullmatch(r'-?\d+',group): errors.append('SUPPORT_BOT_GROUP_ID must be numeric')

if errors:
    for e in errors: print('error: '+e,file=sys.stderr)
    raise SystemExit(1)
print('environment values ok')
PY

private_key="$(python3 - "$ENV_FILE" <<'PY'
from pathlib import Path
import sys
v=''
for line in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if line.startswith('CODE_ANALYZER_PRIVATE_KEY_PATH='): v=line.split('=',1)[1]
print(v)
PY
)"
public_key="$(python3 - "$ENV_FILE" <<'PY'
from pathlib import Path
import sys
v=''
for line in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if line.startswith('CODE_ANALYZER_PUBLIC_KEY_PATH='): v=line.split('=',1)[1]
print(v)
PY
)"

if command -v openssl >/dev/null 2>&1; then
  tmp_public="$(mktemp)"
  trap 'rm -f "$tmp_public"' EXIT
  openssl pkey -in "$private_key" -check -noout >/dev/null
  openssl pkey -in "$private_key" -pubout -out "$tmp_public"
  openssl pkey -pubin -in "$public_key" -text -noout | grep -Eq 'Public-Key: \((3072|[4-9][0-9]{3,}) bit\)' \
    || { echo "error: code-analyzer public key must be at least 3072-bit RSA" >&2; exit 1; }
  cmp -s "$tmp_public" "$public_key" \
    || { echo "error: code-analyzer private/public keys do not match" >&2; exit 1; }
else
  echo "warning: openssl unavailable; key-pair consistency was not checked" >&2
fi

if grep -R "network_mode:[[:space:]]*host" deploy/prod/compose >/dev/null 2>&1; then
  echo "error: production compose must not use host networking" >&2
  exit 1
fi
if grep -R "^[[:space:]]*build:" deploy/prod/compose >/dev/null 2>&1; then
  echo "error: production compose must use published images, not local build contexts" >&2
  exit 1
fi

./scripts/verify-runtime-config.sh >/dev/null
python3 scripts/ci/check-workflow-integrity.py >/dev/null
python3 scripts/ci/check-migration-tooling-safety.py >/dev/null
bash scripts/security/check-browser-api-security.sh >/dev/null

if command -v docker >/dev/null 2>&1; then
  ./deploy/prod/compose.sh config >/dev/null
else
  echo "warning: docker is not installed; skipped docker compose config" >&2
fi

echo "production config ok"
