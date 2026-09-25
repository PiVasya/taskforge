#!/usr/bin/env bash
set -euo pipefail

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$ROOT"

export TASKFORGE_GATEWAY_URL="${TASKFORGE_GATEWAY_URL:-http://localhost:${DEV_GATEWAY_HTTP_PORT:-18080}}"
export TASKFORGE_SMOKE_EMAIL="${TASKFORGE_SMOKE_EMAIL:-admin@test.local}"
export TASKFORGE_SMOKE_PASSWORD="${TASKFORGE_SMOKE_PASSWORD:-TaskForge123!}"
export TASKFORGE_SMOKE_TIMEOUT_SECONDS="${TASKFORGE_SMOKE_TIMEOUT_SECONDS:-120}"

python3 - <<'PY'
from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

base = os.environ["TASKFORGE_GATEWAY_URL"].rstrip("/")
email = os.environ["TASKFORGE_SMOKE_EMAIL"]
password = os.environ["TASKFORGE_SMOKE_PASSWORD"]
timeout_seconds = int(os.environ.get("TASKFORGE_SMOKE_TIMEOUT_SECONDS", "120"))


def request(method: str, path: str, body=None, token: str | None = None, ok=(200, 201, 204), timeout=30):
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(base + path, data=data, method=method)
    req.add_header("Accept", "application/json")
    if body is not None:
        req.add_header("Content-Type", "application/json; charset=utf-8")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            text = resp.read().decode("utf-8", errors="replace")
            if resp.status not in ok:
                raise RuntimeError(f"{method} {path} -> HTTP {resp.status}: {text}")
            return json.loads(text) if text.strip() else None
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", errors="replace")
        if e.code in ok:
            return json.loads(text) if text.strip() else None
        raise RuntimeError(f"{method} {path} -> HTTP {e.code}: {text}") from None


def wait_health():
    deadline = time.time() + timeout_seconds
    last = None
    while time.time() < deadline:
        try:
            request("GET", "/health/gateway", ok=(200,), timeout=5)
            return
        except Exception as ex:
            last = ex
            time.sleep(2)
    raise RuntimeError(f"Gateway is not ready at {base}: {last}")


def login_or_register():
    try:
        request("POST", "/api/auth/register", {
            "email": email,
            "password": password,
            "firstName": "Smoke",
            "lastName": "Judge",
        }, ok=(200, 400))
    except Exception as ex:
        print(f"[warn] register skipped: {ex}")

    data = request("POST", "/api/auth/login", {"email": email, "password": password}, ok=(200,))
    token = data.get("accessToken")
    if not token:
        raise RuntimeError("Login response does not contain accessToken. Check TASKFORGE_SMOKE_EMAIL/PASSWORD and BOOTSTRAP_ADMIN_EMAILS.")
    role = ((data.get("user") or {}).get("role") or (data.get("user") or {}).get("roles") or "?")
    print(f"[ok] logged in as {email}, role={role}")
    return token


def create_course(token: str) -> str:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    data = request("POST", "/api/courses", {
        "title": f"Judge smoke {stamp}",
        "description": "Автоматический smoke-тест полного пути проверки решений.",
        "isPublic": True,
    }, token=token)
    cid = data.get("id") or data.get("Id")
    if not cid:
        raise RuntimeError(f"Course response does not contain id: {data}")
    print(f"[ok] created course {cid}")
    return cid


def create_assignment(token: str, course_id: str, lang: str, *, forbidden=None) -> str:
    data = request("POST", f"/api/courses/{course_id}/assignments", {
        "title": f"Smoke {lang}",
        "description": "Прочитать число и вывести число + 1.",
        "type": "code-test",
        "language": lang,
        "allowedLanguages": [lang],
        "tests": [
            {"input": "1\n", "expectedOutput": "2\n", "isHidden": False},
            {"input": "41\n", "expectedOutput": "42\n", "isHidden": True},
        ],
        "codeForbiddenCalls": forbidden or [],
        "codeRequiredCalls": [],
        "isVisible": True,
    }, token=token)
    aid = data.get("id") or data.get("Id")
    if not aid:
        raise RuntimeError(f"Assignment response does not contain id for {lang}: {data}")
    return aid


GOOD_CODE = {
    "cpp": """#include <iostream>\nusing namespace std;\nint main(){ long long x; if(cin >> x) cout << x + 1 << '\\n'; return 0; }\n""",
    "csharp": """using System;\nclass Program { static void Main() { var s = Console.ReadLine(); Console.WriteLine(int.Parse(s ?? \"0\") + 1); } }\n""",
    "java": """import java.io.*;\npublic class Main { public static void main(String[] args) throws Exception { BufferedReader br = new BufferedReader(new InputStreamReader(System.in)); int x = Integer.parseInt(br.readLine().trim()); System.out.println(x + 1); } }\n""",
    "javascript": """const fs = require('fs');\nconst input = fs.readFileSync(0, 'utf8').trim();\nconst x = Number(input || '0');\nconsole.log(x + 1);\n""",
    "pascal": """var x: integer;\nbegin\n  readln(x);\n  writeln(x + 1);\nend.\n""",
    "python": """x = int(input())\nprint(x + 1)\n""",
}


def is_pending(row):
    status = (row.get("status") or row.get("verdict") or "").lower()
    return status in {"preparing", "queued", "running"} or row.get("isPending") is True


def poll_solution(token: str, solution_id: str):
    deadline = time.time() + timeout_seconds
    last = None
    while time.time() < deadline:
        last = request("GET", f"/api/me/solutions/{solution_id}", token=token)
        if not is_pending(last):
            return last
        time.sleep(2)
    return last


def submit_and_expect(token: str, assignment_id: str, lang: str, code: str, expected: str):
    data = request("POST", f"/api/assignments/{assignment_id}/submit", {
        "language": lang,
        "code": code,
    }, token=token)
    sid = data.get("id") or data.get("Id")
    if not sid:
        raise RuntimeError(f"Submit response does not contain id for {lang}: {data}")
    final = data if not is_pending(data) else poll_solution(token, sid)
    status = final.get("status") or final.get("verdict")
    score = final.get("score")
    print(f"[judge] {lang:<10} -> {status}, score={score}, solution={sid}")
    if status != expected:
        raise RuntimeError(f"Expected {expected} for {lang}, got {status}. Full result: {json.dumps(final, ensure_ascii=False)}")
    return final


print(f"[info] gateway: {base}")
wait_health()
token = login_or_register()
course_id = create_course(token)

for lang, code in GOOD_CODE.items():
    aid = create_assignment(token, course_id, lang)
    submit_and_expect(token, aid, lang, code, "Accepted")

# Generic Unicode guard: Cyrillic identifiers must be blocked before the runner.
aid = create_assignment(token, course_id, "python")
submit_and_expect(token, aid, "python", "сумма = int(input()) + 1\nprint(сумма)\n", "PolicyFailed")

# Per-task forbidden call guard: task policy must reach code-analyzer through tasks -> solutions -> execution.
aid = create_assignment(token, course_id, "csharp", forbidden=["Console.WriteLine"])
submit_and_expect(token, aid, "csharp", GOOD_CODE["csharp"], "PolicyFailed")

print("[ok] full judge e2e passed: 6 languages + Cyrillic guard + forbidden calls")
PY
