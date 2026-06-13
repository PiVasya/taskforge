#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${TASKFORGE_PROD_ENV_FILE:-deploy/prod/.env}"
EXAMPLE="deploy/prod/.env.example"

random_secret() {
  local bytes="${1:-48}"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 "$bytes" | tr -d '\n' | tr '/+' '_-'
  else
    LC_ALL=C tr -dc 'A-Za-z0-9_-' < /dev/urandom | head -c $((bytes * 2))
  fi
}

set_env_value() {
  local key="$1"
  local value="$2"
  python3 - "$ENV_FILE" "$key" "$value" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
key = sys.argv[2]
value = sys.argv[3]
lines = path.read_text(encoding='utf-8').splitlines()
out = []
seen = False
for line in lines:
    if line.startswith(key + '='):
        out.append(f'{key}={value}')
        seen = True
    else:
        out.append(line)
if not seen:
    out.append(f'{key}={value}')
path.write_text('\n'.join(out) + '\n', encoding='utf-8')
PY
}

if [ ! -f "$ENV_FILE" ]; then
  mkdir -p "$(dirname "$ENV_FILE")"
  cp "$EXAMPLE" "$ENV_FILE"
  echo "Created $ENV_FILE from $EXAMPLE"
fi

# Allow one-command server bootstrap by passing the public values as environment variables.
for key in IMAGE_REPOSITORY IMAGE_TAG DOMAIN CT_DOMAIN LETSENCRYPT_EMAIL GATEWAY_MODE S3_PUBLIC_ENDPOINT BOOTSTRAP_ADMIN_EMAILS WATCHTOWER_SCOPE WATCHTOWER_POLL_INTERVAL; do
  if [ -n "${!key:-}" ]; then
    set_env_value "$key" "${!key}"
  fi
done

# Replace dangerous placeholders with strong random values. Existing real values are preserved.
python3 - "$ENV_FILE" <<'PY'
from pathlib import Path
import base64
import os
import sys

path = Path(sys.argv[1])
secret_keys = {
    'POSTGRES_PASSWORD': 36,
    'RABBITMQ_DEFAULT_PASS': 36,
    'MINIO_ROOT_PASSWORD': 36,
    'JWT_SIGNING_KEY': 72,
    'TASKFORGE_INTERNAL_KEY': 48,
    'TASKFORGE_AGENT_INTERNAL_KEY': 48,
}

def random_secret(n: int) -> str:
    return base64.urlsafe_b64encode(os.urandom(n)).decode('ascii').rstrip('=')

def unsafe(value: str) -> bool:
    v = (value or '').strip()
    return not v or 'CHANGE_ME' in v.upper() or 'DEV_CHANGE_ME' in v.upper() or v.lower() in {'taskforge', 'password', 'admin', 'secret'}

lines = path.read_text(encoding='utf-8').splitlines()
out = []
for line in lines:
    if not line or line.lstrip().startswith('#') or '=' not in line:
        out.append(line)
        continue
    key, value = line.split('=', 1)
    if key in secret_keys and unsafe(value):
        value = random_secret(secret_keys[key])
    out.append(f'{key}={value}')
path.write_text('\n'.join(out) + '\n', encoding='utf-8')
PY

echo "Prepared $ENV_FILE"
