#!/usr/bin/env bash
set -Eeuo pipefail

BASE_URL="${1:-${TASKFORGE_BROWSER_BASE_URL:-https://taskforge.by}}"
BASE_URL="${BASE_URL%/}"
SITE="${TASKFORGE_BROWSER_SITE:-main}"
PATH_TO_CHECK="${TASKFORGE_BROWSER_PATH:-/}"
WIDTH="${TASKFORGE_BROWSER_WIDTH:-390}"
HEIGHT="${TASKFORGE_BROWSER_HEIGHT:-844}"
ACCESS_TOKEN="${TASKFORGE_BROWSER_ACCESS_TOKEN:-}"

tmp_dir="$(mktemp -d)"
session_id=""
session_token=""

cleanup() {
  if [ -n "$session_id" ] && [ -n "$session_token" ]; then
    auth_args=()
    if [ -n "$ACCESS_TOKEN" ]; then auth_args=(-H "Authorization: Bearer $ACCESS_TOKEN"); fi
    curl -fsS -X DELETE \
      "${auth_args[@]}" \
      -H "X-TaskForge-Browser-Session-Token: $session_token" \
      "$BASE_URL/api/browser/sessions/$session_id" >/dev/null 2>&1 || true
  fi
  rm -rf "$tmp_dir"
}
trap cleanup EXIT HUP INT TERM

command -v curl >/dev/null 2>&1 || { echo 'error: curl is required' >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo 'error: python3 is required' >&2; exit 2; }

auth_args=()
if [ -n "$ACCESS_TOKEN" ]; then auth_args=(-H "Authorization: Bearer $ACCESS_TOKEN"); fi

printf '[browser-smoke] discovery\n'
curl -fsS "$BASE_URL/.well-known/taskforge-ai.json" -o "$tmp_dir/discovery.json"
curl -fsS "$BASE_URL/llms.txt" -o "$tmp_dir/llms.txt"
curl -fsS "${auth_args[@]}" "$BASE_URL/api/site/info" -o "$tmp_dir/info.json"
curl -fsS "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  "$BASE_URL/api/site/routes" -o "$tmp_dir/routes.json"

printf '[browser-smoke] semantic snapshot\n'
curl -fsS "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  --data-urlencode "path=$PATH_TO_CHECK" \
  --data-urlencode "width=$WIDTH" \
  --data-urlencode "height=$HEIGHT" \
  --data-urlencode "waitMs=300" \
  "$BASE_URL/api/site/snapshot" -o "$tmp_dir/snapshot.json"

printf '[browser-smoke] PNG and PDF renders\n'
curl -fsS "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  --data-urlencode "path=$PATH_TO_CHECK" \
  --data-urlencode "width=$WIDTH" \
  --data-urlencode "height=$HEIGHT" \
  --data-urlencode "waitMs=300" \
  --data-urlencode "fullPage=false" \
  "$BASE_URL/api/site/render" -o "$tmp_dir/render.png"
curl -fsS "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  --data-urlencode "path=$PATH_TO_CHECK" \
  --data-urlencode "width=$WIDTH" \
  --data-urlencode "height=$HEIGHT" \
  --data-urlencode "waitMs=300" \
  --data-urlencode "fullPage=false" \
  "$BASE_URL/api/site/render.pdf" -o "$tmp_dir/render.pdf"

printf '[browser-smoke] interactive read-only session\n'
python3 - "$SITE" "$PATH_TO_CHECK" "$WIDTH" "$HEIGHT" > "$tmp_dir/create-body.json" <<'PY'
import json, sys
print(json.dumps({
    'site': sys.argv[1],
    'path': sys.argv[2],
    'width': int(sys.argv[3]),
    'height': int(sys.argv[4]),
    'readOnly': True,
    'waitMs': 300,
}))
PY
curl -fsS "${auth_args[@]}" \
  -H 'Content-Type: application/json' \
  --data-binary "@$tmp_dir/create-body.json" \
  "$BASE_URL/api/browser/sessions" -o "$tmp_dir/session.json"

readarray -t session_values < <(python3 - "$tmp_dir/session.json" <<'PY'
import json, sys
value=json.load(open(sys.argv[1], encoding='utf-8'))
print(value['id'])
print(value['sessionToken'])
PY
)
session_id="${session_values[0]}"
session_token="${session_values[1]}"

curl -fsS "${auth_args[@]}" \
  -H "X-TaskForge-Browser-Session-Token: $session_token" \
  "$BASE_URL/api/browser/sessions/$session_id/snapshot" -o "$tmp_dir/session-snapshot.json"

python3 - "$tmp_dir" <<'PY'
from pathlib import Path
import json, sys
root=Path(sys.argv[1])

def load(name):
    return json.loads((root/name).read_text(encoding='utf-8'))

discovery=load('discovery.json')
info=load('info.json')
routes=load('routes.json')
snapshot=load('snapshot.json')
session=load('session.json')
session_snapshot=load('session-snapshot.json')

assert discovery.get('apiVersion') == '1.1', 'unexpected discovery API version'
assert info.get('name') == 'TaskForge', 'site info is invalid'
assert isinstance(routes.get('routes'), list) and routes['routes'], 'route catalog is empty'
for value, label in ((snapshot, 'stateless'), (session_snapshot, 'session')):
    assert value.get('url'), f'{label} snapshot has no URL'
    assert isinstance(value.get('elements'), list), f'{label} snapshot elements are missing'
    assert 'ariaSnapshot' in value, f'{label} snapshot ARIA field is missing'
assert session.get('readOnly') is True, 'anonymous smoke session is not read-only'
assert session.get('sessionToken'), 'session token is missing'

png=(root/'render.png').read_bytes()
pdf=(root/'render.pdf').read_bytes()
assert png.startswith(b'\x89PNG\r\n\x1a\n'), 'render endpoint did not return PNG'
assert pdf.startswith(b'%PDF-'), 'render.pdf endpoint did not return PDF'
assert len(png) > 1000, 'PNG render is unexpectedly small'
assert len(pdf) > 1000, 'PDF render is unexpectedly small'

print(f"[browser-smoke] ok url={snapshot['url']} elements={len(snapshot['elements'])} png={len(png)}B pdf={len(pdf)}B")
PY
