#!/usr/bin/env bash
set -Eeuo pipefail

BASE_URL="${1:-${TASKFORGE_BROWSER_BASE_URL:-https://taskforge.by}}"
BASE_URL="${BASE_URL%/}"
SITE="${TASKFORGE_BROWSER_SITE:-main}"
PATH_TO_CHECK="${TASKFORGE_BROWSER_PATH:-/}"
WIDTH="${TASKFORGE_BROWSER_WIDTH:-390}"
HEIGHT="${TASKFORGE_BROWSER_HEIGHT:-844}"
ACCESS_TOKEN="${TASKFORGE_BROWSER_ACCESS_TOKEN:-}"
RETRIES="${TASKFORGE_BROWSER_SMOKE_RETRIES:-12}"
RETRY_DELAY="${TASKFORGE_BROWSER_SMOKE_RETRY_DELAY:-5}"

case "$RETRIES" in ''|*[!0-9]*) echo 'error: TASKFORGE_BROWSER_SMOKE_RETRIES must be numeric' >&2; exit 2;; esac
case "$RETRY_DELAY" in ''|*[!0-9]*) echo 'error: TASKFORGE_BROWSER_SMOKE_RETRY_DELAY must be numeric' >&2; exit 2;; esac

tmp_dir="$(mktemp -d)"
session_id=""
session_token=""

auth_args=()
if [ -n "$ACCESS_TOKEN" ]; then auth_args=(-H "Authorization: Bearer $ACCESS_TOKEN"); fi

cleanup() {
  if [ -n "$session_id" ] && [ -n "$session_token" ]; then
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

curl_retry() {
  curl --retry "$RETRIES" --retry-delay "$RETRY_DELAY" --retry-connrefused \
    --connect-timeout 8 --max-time 120 "$@"
}

request_retry() {
  local label="$1"
  local output="$2"
  shift 2
  if ! curl_retry --fail-with-body -sS "$@" -o "$output"; then
    printf '[browser-smoke] ERROR: %s failed\n' "$label" >&2
    if [ -s "$output" ]; then
      printf '[browser-smoke] response body:\n' >&2
      cat "$output" >&2
      printf '\n' >&2
    fi
    return 1
  fi
}

request_once() {
  local label="$1"
  local output="$2"
  shift 2
  if ! curl --connect-timeout 8 --max-time 120 --fail-with-body -sS "$@" -o "$output"; then
    printf '[browser-smoke] ERROR: %s failed\n' "$label" >&2
    if [ -s "$output" ]; then
      printf '[browser-smoke] response body:\n' >&2
      cat "$output" >&2
      printf '\n' >&2
    fi
    return 1
  fi
}

printf '[browser-smoke] base=%s site=%s path=%s viewport=%sx%s\n' "$BASE_URL" "$SITE" "$PATH_TO_CHECK" "$WIDTH" "$HEIGHT"
printf '[browser-smoke] discovery\n'
request_retry 'AI discovery' "$tmp_dir/discovery.json" "$BASE_URL/.well-known/taskforge-ai.json"
request_retry 'llms.txt' "$tmp_dir/llms.txt" "$BASE_URL/llms.txt"
request_retry 'agent access index' "$tmp_dir/agent-access.html" "$BASE_URL/ai-access"
request_retry 'site info' "$tmp_dir/info.json" "${auth_args[@]}" "$BASE_URL/api/site/info"
request_retry 'route catalog' "$tmp_dir/routes.json" "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  "$BASE_URL/api/site/routes"

printf '[browser-smoke] semantic snapshot\n'
request_retry 'semantic snapshot' "$tmp_dir/snapshot.json" "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  --data-urlencode "path=$PATH_TO_CHECK" \
  --data-urlencode "width=$WIDTH" \
  --data-urlencode "height=$HEIGHT" \
  --data-urlencode "waitMs=300" \
  "$BASE_URL/api/site/snapshot"

printf '[browser-smoke] PNG and PDF renders\n'
request_retry 'PNG render' "$tmp_dir/render.png" "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  --data-urlencode "path=$PATH_TO_CHECK" \
  --data-urlencode "width=$WIDTH" \
  --data-urlencode "height=$HEIGHT" \
  --data-urlencode "waitMs=300" \
  --data-urlencode "fullPage=false" \
  "$BASE_URL/api/site/render"
request_retry 'PDF render' "$tmp_dir/render.pdf" "${auth_args[@]}" --get \
  --data-urlencode "site=$SITE" \
  --data-urlencode "path=$PATH_TO_CHECK" \
  --data-urlencode "width=$WIDTH" \
  --data-urlencode "height=$HEIGHT" \
  --data-urlencode "waitMs=300" \
  --data-urlencode "fullPage=false" \
  "$BASE_URL/api/site/render.pdf"

printf '[browser-smoke] crawler self-discovery artifact chain\n'
request_once 'crawler capture page' "$tmp_dir/agent-capture.html" \
  "$BASE_URL/api/site/agent/capture/main/390/844/viewport/"
readarray -t artifact_urls < <(python3 - "$tmp_dir/agent-capture.html" "$BASE_URL" <<'PY'
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urljoin
import sys

class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.hrefs=[]
    def handle_starttag(self, tag, attrs):
        if tag.lower() != 'a':
            return
        for key, value in attrs:
            if key.lower() == 'href' and value:
                self.hrefs.append(value)

parser=Links()
parser.feed(Path(sys.argv[1]).read_text(encoding='utf-8'))
base=sys.argv[2].rstrip('/') + '/'
for suffix in ('snapshot.json', 'render.png', 'render.pdf'):
    match=next((h for h in parser.hrefs if h.endswith('/' + suffix)), None)
    if not match:
        raise SystemExit(f'missing {suffix} artifact link')
    print(urljoin(base, match))
PY
)
request_retry 'crawler snapshot artifact' "$tmp_dir/agent-snapshot.json" "${artifact_urls[0]}"
request_retry 'crawler PNG artifact' "$tmp_dir/agent-render.png" "${artifact_urls[1]}"
request_retry 'crawler PDF artifact' "$tmp_dir/agent-render.pdf" "${artifact_urls[2]}"

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
# Never retry session creation: a lost response could otherwise leave duplicate
# sessions. The preceding idempotent probes already handle service warm-up.
request_once 'session creation' "$tmp_dir/session.json" "${auth_args[@]}" \
  -H 'Content-Type: application/json' \
  --data-binary "@$tmp_dir/create-body.json" \
  "$BASE_URL/api/browser/sessions"

readarray -t session_values < <(python3 - "$tmp_dir/session.json" <<'PY'
import json, sys
value=json.load(open(sys.argv[1], encoding='utf-8'))
print(value['id'])
print(value['sessionToken'])
PY
)
session_id="${session_values[0]}"
session_token="${session_values[1]}"

request_retry 'session snapshot' "$tmp_dir/session-snapshot.json" "${auth_args[@]}" \
  -H "X-TaskForge-Browser-Session-Token: $session_token" \
  "$BASE_URL/api/browser/sessions/$session_id/snapshot"

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
assert discovery.get('agentAccess'), 'AI discovery does not advertise agent access index'
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

agent_html=(root/'agent-access.html').read_text(encoding='utf-8')
assert '/api/site/agent/capture/' in agent_html, 'agent access index has no crawler capture links'
agent_snapshot=load('agent-snapshot.json')
assert agent_snapshot.get('url'), 'crawler snapshot artifact is invalid'
agent_png=(root/'agent-render.png').read_bytes()
agent_pdf=(root/'agent-render.pdf').read_bytes()
assert agent_png.startswith(b'\x89PNG\r\n\x1a\n'), 'crawler artifact did not return PNG'
assert agent_pdf.startswith(b'%PDF-'), 'crawler artifact did not return PDF'

print(f"[browser-smoke] ok url={snapshot['url']} elements={len(snapshot['elements'])} png={len(png)}B pdf={len(pdf)}B")
PY
