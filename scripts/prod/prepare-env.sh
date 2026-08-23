#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${TASKFORGE_PROD_ENV_FILE:-deploy/prod/.env}"
EXAMPLE="${TASKFORGE_PROD_ENV_EXAMPLE:-deploy/prod/.env.example}"
ASSUME_YES=0
CHECK_ONLY=0
ALLOW_PERSISTENT_INIT=0

usage() {
  cat <<'HELP'
Usage: scripts/prod/prepare-env.sh [options]

Synchronizes the production .env with .env.example without overwriting existing
real values. Duplicate assignments are normalized by keeping the last value,
matching Docker Compose behavior. Missing managed secrets are generated.

Options:
  -y, --yes                         Apply changes without an interactive prompt
  -c, --check                       Report required changes without modifying files
  --env-file FILE                   Use a different .env path
  --allow-persistent-secret-init    Allow generation of storage credentials in an
                                    already existing .env (unsafe on initialized volumes)
  -h, --help                        Show this help
HELP
}

while [ $# -gt 0 ]; do
  case "$1" in
    -y|--yes) ASSUME_YES=1; shift ;;
    -c|--check|--dry-run) CHECK_ONLY=1; shift ;;
    --allow-persistent-secret-init) ALLOW_PERSISTENT_INIT=1; shift ;;
    --env-file)
      [ $# -ge 2 ] || { echo "error: --env-file requires a path" >&2; exit 2; }
      ENV_FILE="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

command -v python3 >/dev/null 2>&1 || { echo "error: python3 is required" >&2; exit 2; }
[ -f "$EXAMPLE" ] || { echo "error: missing $EXAMPLE" >&2; exit 2; }

PLAN_FILE="$(mktemp)"
trap 'rm -f "$PLAN_FILE"' EXIT

python3 - "$ENV_FILE" "$EXAMPLE" "$PLAN_FILE" "$ROOT_DIR" <<'PY_PLAN'
from __future__ import annotations
from pathlib import Path
import json, os, re, sys

env_path = Path(sys.argv[1])
example_path = Path(sys.argv[2])
plan_path = Path(sys.argv[3])
root = Path(sys.argv[4]).resolve()

SECRET_SPECS = {
    "POSTGRES_PASSWORD": 36,
    "RABBITMQ_DEFAULT_PASS": 36,
    "REDIS_PASSWORD": 36,
    "MINIO_ROOT_PASSWORD": 36,
    "JWT_SIGNING_KEY": 72,
    "TASKFORGE_INTERNAL_KEY": 48,
    "TASKFORGE_AGENT_INTERNAL_KEY": 48,
    "ANALYTICS_IP_HASH_SALT": 48,
    "MINECRAFT_PLUGIN_KEY": 48,
    "MINECRAFT_WEBHOOK_KEY": 48,
}
PERSISTENT = {
    "POSTGRES_PASSWORD", "RABBITMQ_DEFAULT_PASS", "REDIS_PASSWORD", "MINIO_ROOT_PASSWORD"
}
OVERRIDES = {
    "IMAGE_REPOSITORY", "IMAGE_TAG", "DOMAIN", "CT_DOMAIN", "LETSENCRYPT_EMAIL",
    "GATEWAY_MODE", "TASKFORGE_NODE_ROLE", "S3_PUBLIC_ENDPOINT",
    "BOOTSTRAP_ADMIN_EMAILS", "WATCHTOWER_SCOPE", "WATCHTOWER_POLL_INTERVAL",
    "SUPPORT_BOT_TOKEN", "SUPPORT_BOT_GROUP_ID", "SUPPORT_BOT_USERNAME",
    "MINECRAFT_WEBHOOK_BASE_URL", "MINECRAFT_WEBHOOK_SEND_CODE_PATH",
    "MINECRAFT_WEBHOOK_KEY", "MINECRAFT_HEALTH_URL", "MINECRAFT_PLUGIN_KEY",
}
KEY_PATHS = {
    "CODE_ANALYZER_PRIVATE_KEY_PATH": str(root / ".runtime/code-analyzer-keys/code-analyzer-private.pem"),
    "CODE_ANALYZER_PUBLIC_KEY_PATH": str(root / ".runtime/code-analyzer-keys/code-analyzer-public.pem"),
}
key_re = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")

def parse(path: Path):
    values: dict[str, str] = {}
    order: list[str] = []
    dup: list[str] = []
    if not path.exists():
        return values, order, dup
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        if not raw or raw.lstrip().startswith("#") or "=" not in raw:
            continue
        left, value = raw.split("=", 1)
        key = left.strip()
        if key.startswith("export "):
            key = key[7:].strip()
        if not key_re.fullmatch(key):
            continue
        if key in values:
            dup.append(key)
        else:
            order.append(key)
        values[key] = value
    return values, order, sorted(set(dup))

def unsafe(value: str, key: str) -> bool:
    v = (value or "").strip().strip('"').strip("'")
    if not v or "CHANGE_ME" in v.upper() or "DEV_CHANGE_ME" in v.upper():
        return True
    if v.lower() in {"taskforge", "password", "admin", "secret", "changeme"}:
        return True
    minimum = 64 if key == "JWT_SIGNING_KEY" else 32
    return len(v) < minimum

example, example_order, example_dup = parse(example_path)
current, current_order, current_dup = parse(env_path)
missing = [k for k in example_order if k not in current]
unsafe_secrets = [k for k in SECRET_SPECS if k in current and unsafe(current[k], k)]
path_overrides: dict[str, str] = {}
for key, default in KEY_PATHS.items():
    value = current.get(key, "").strip()
    if key not in current or not value or "CHANGE_ME" in value.upper() or value.startswith("/srv/taskforge/secrets/"):
        path_overrides[key] = default
shell_overrides = {
    key: os.environ[key]
    for key in OVERRIDES
    if os.environ.get(key, "") != "" and current.get(key) != os.environ[key]
}
overrides = {**path_overrides, **shell_overrides}
plan = {
    "env_exists": env_path.exists(),
    "missing": missing,
    "unsafe_secrets": sorted(unsafe_secrets),
    "persistent_changes": sorted((set(missing) | set(unsafe_secrets)) & PERSISTENT),
    "duplicates": current_dup,
    "template_duplicates": example_dup,
    "extra": [k for k in current_order if k not in example],
    "overrides": overrides,
    "secret_specs": SECRET_SPECS,
}
plan_path.write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")
PY_PLAN

# shellcheck disable=SC2046
eval "$(python3 - "$PLAN_FILE" <<'PY_SHELL'
import json, shlex, sys
p=json.load(open(sys.argv[1], encoding='utf-8'))
def arr(name, values):
    print(f"declare -a {name}=(" + " ".join(shlex.quote(str(v)) for v in values) + ")")
arr('MISSING', p['missing'])
arr('UNSAFE_SECRETS', p['unsafe_secrets'])
arr('PERSISTENT_CHANGES', p['persistent_changes'])
arr('DUPLICATES', p['duplicates'])
arr('TEMPLATE_DUPLICATES', p['template_duplicates'])
arr('EXTRA_KEYS', p['extra'])
arr('OVERRIDES', [f"{k}={v}" for k,v in p['overrides'].items()])
print('ENV_EXISTS=' + shlex.quote('1' if p['env_exists'] else '0'))
PY_SHELL
)"

printf 'TaskForge production environment audit\n'
printf '  env file:        %s\n' "$ENV_FILE"
printf '  template:        %s\n' "$EXAMPLE"
printf '  missing keys:    %d\n' "${#MISSING[@]}"
printf '  unsafe secrets:  %d\n' "${#UNSAFE_SECRETS[@]}"
printf '  duplicate keys:  %d\n' "${#DUPLICATES[@]}"
printf '  explicit values: %d\n' "${#OVERRIDES[@]}"

if [ ${#TEMPLATE_DUPLICATES[@]} -gt 0 ]; then
  echo "error: duplicate keys in $EXAMPLE:" >&2
  printf '  - %s\n' "${TEMPLATE_DUPLICATES[@]}" >&2
  exit 2
fi
if [ ${#DUPLICATES[@]} -gt 0 ]; then
  echo "Duplicate variables will be normalized; the last value is kept:"
  printf '  - %s\n' "${DUPLICATES[@]}"
fi
if [ ${#MISSING[@]} -gt 0 ]; then
  echo "Missing variables will be added:"
  printf '  - %s\n' "${MISSING[@]}"
fi
if [ ${#UNSAFE_SECRETS[@]} -gt 0 ]; then
  echo "Empty, placeholder, or too-short managed secrets will be replaced:"
  printf '  - %s\n' "${UNSAFE_SECRETS[@]}"
fi
if [ ${#EXTRA_KEYS[@]} -gt 0 ]; then
  echo "Custom variables not present in the template will be preserved:"
  printf '  - %s\n' "${EXTRA_KEYS[@]}"
fi

if [ "$ENV_EXISTS" -eq 1 ] && [ ${#PERSISTENT_CHANGES[@]} -gt 0 ] && [ "$ALLOW_PERSISTENT_INIT" -ne 1 ]; then
  echo "error: persistent service credentials are missing or unsafe in an existing .env:" >&2
  printf '  - %s\n' "${PERSISTENT_CHANGES[@]}" >&2
  cat >&2 <<'WARN'
Changing only .env does not change credentials inside initialized Docker volumes.
Restore the existing values. Use --allow-persistent-secret-init only for a new or
explicitly reset storage stack.
WARN
  exit 5
fi

needs_change=0
[ ${#MISSING[@]} -gt 0 ] && needs_change=1
[ ${#UNSAFE_SECRETS[@]} -gt 0 ] && needs_change=1
[ ${#DUPLICATES[@]} -gt 0 ] && needs_change=1
[ ${#OVERRIDES[@]} -gt 0 ] && needs_change=1

if [ "$CHECK_ONLY" -eq 1 ]; then
  if [ "$needs_change" -eq 0 ]; then
    echo "Environment file is synchronized."
    exit 0
  fi
  echo "Check-only mode: changes are required; no files were modified."
  exit 3
fi

if [ "$needs_change" -eq 1 ] && [ "$ASSUME_YES" -ne 1 ]; then
  if [ ! -t 0 ]; then
    echo "error: changes require confirmation; rerun interactively or pass --yes" >&2
    exit 4
  fi
  read -r -p "Apply the listed changes to $ENV_FILE? [y/N] " answer
  case "$answer" in y|Y|yes|YES|да|Да|ДА) ;; *) echo "Cancelled."; exit 1;; esac
fi

if [ "$needs_change" -eq 1 ]; then
  mkdir -p "$(dirname "$ENV_FILE")"
  if [ -f "$ENV_FILE" ]; then
    backup_dir="$(dirname "$ENV_FILE")/backups/env"
    mkdir -p "$backup_dir"
    backup="$backup_dir/$(basename "$ENV_FILE").$(date -u +%Y%m%d-%H%M%S)-$$"
    cp -p "$ENV_FILE" "$backup"
    chmod 600 "$backup" 2>/dev/null || true
    echo "Backup: $backup"
  fi

  python3 - "$ENV_FILE" "$EXAMPLE" "$PLAN_FILE" <<'PY_APPLY'
from __future__ import annotations
from pathlib import Path
import base64, json, os, re, sys

env_path=Path(sys.argv[1]); example_path=Path(sys.argv[2])
plan=json.loads(Path(sys.argv[3]).read_text(encoding='utf-8'))
secret_specs={k:int(v) for k,v in plan['secret_specs'].items()}
key_re=re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')

def random_secret(n:int)->str:
    return base64.urlsafe_b64encode(os.urandom(n)).decode('ascii').rstrip('=')

def defaults(path:Path):
    out={}
    for raw in path.read_text(encoding='utf-8-sig').splitlines():
        if not raw or raw.lstrip().startswith('#') or '=' not in raw: continue
        k,v=raw.split('=',1); k=k.strip()
        if key_re.fullmatch(k): out[k]=v
    return out

def key_of(line:str):
    if not line or line.lstrip().startswith('#') or '=' not in line: return None
    k=line.split('=',1)[0].strip()
    if k.startswith('export '): k=k[7:].strip()
    return k if key_re.fullmatch(k) else None

def normalize(lines):
    last={}
    for i,line in enumerate(lines):
        k=key_of(line)
        if k: last[k]=i
    return [line for i,line in enumerate(lines) if not (k:=key_of(line)) or last[k]==i]

def set_value(lines,key,value):
    out=[]; done=False
    for line in lines:
        if key_of(line)==key:
            if not done: out.append(f'{key}={value}'); done=True
        else: out.append(line)
    if not done: out.append(f'{key}={value}')
    return out

d=defaults(example_path)
if env_path.exists():
    lines=normalize(env_path.read_text(encoding='utf-8-sig').splitlines())
else:
    lines=example_path.read_text(encoding='utf-8-sig').splitlines()
for key in plan['missing']:
    value=random_secret(secret_specs[key]) if key in secret_specs else d.get(key,'')
    lines=set_value(lines,key,value)
for key in plan['unsafe_secrets']:
    lines=set_value(lines,key,random_secret(secret_specs[key]))
for key,value in plan['overrides'].items():
    lines=set_value(lines,key,value)
while lines and not lines[-1].strip(): lines.pop()
env_path.write_text('\n'.join(lines)+'\n',encoding='utf-8')
PY_APPLY
fi

chmod 600 "$ENV_FILE" 2>/dev/null || true

read_env_value() {
  python3 - "$ENV_FILE" "$1" <<'PY_READ'
from pathlib import Path
import re,sys
value=''
for raw in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if not raw or raw.lstrip().startswith('#') or '=' not in raw: continue
    k,v=raw.split('=',1)
    if k.strip()==sys.argv[2]: value=v
print(value)
PY_READ
}

PRIVATE_KEY="$(read_env_value CODE_ANALYZER_PRIVATE_KEY_PATH)"
PUBLIC_KEY="$(read_env_value CODE_ANALYZER_PUBLIC_KEY_PATH)"
[ -n "$PRIVATE_KEY" ] || { echo "error: CODE_ANALYZER_PRIVATE_KEY_PATH is empty" >&2; exit 2; }
[ -n "$PUBLIC_KEY" ] || { echo "error: CODE_ANALYZER_PUBLIC_KEY_PATH is empty" >&2; exit 2; }

command -v openssl >/dev/null 2>&1 || { echo "error: openssl is required to prepare code-analyzer keys" >&2; exit 2; }
mkdir -p "$(dirname "$PRIVATE_KEY")" "$(dirname "$PUBLIC_KEY")"
tmp_private="${PRIVATE_KEY}.tmp.$$"
tmp_public="${PUBLIC_KEY}.tmp.$$"
trap 'rm -f "$PLAN_FILE" "${tmp_private:-}" "${tmp_public:-}"' EXIT
umask 077

if [ ! -s "$PRIVATE_KEY" ]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$tmp_private" 2>/dev/null
  openssl pkey -in "$tmp_private" -check -noout >/dev/null
  install -m 0600 "$tmp_private" "$PRIVATE_KEY"
  echo "Generated code-analyzer RSA private key."
else
  if ! openssl pkey -in "$PRIVATE_KEY" -check -noout >/dev/null; then
    echo "error: code-analyzer private key is invalid: $PRIVATE_KEY" >&2
    exit 2
  fi
fi

openssl pkey -in "$PRIVATE_KEY" -pubout -out "$tmp_public"
if [ ! -s "$PUBLIC_KEY" ] || ! cmp -s "$tmp_public" "$PUBLIC_KEY"; then
  install -m 0644 "$tmp_public" "$PUBLIC_KEY"
  echo "Synchronized code-analyzer public key with the private signing key."
fi

rm -f "$tmp_private" "$tmp_public"
chmod 600 "$PRIVATE_KEY" 2>/dev/null || true
chmod 644 "$PUBLIC_KEY" 2>/dev/null || true

echo "Prepared $ENV_FILE"
