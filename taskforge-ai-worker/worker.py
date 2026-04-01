import ast
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
from typing import Any, Dict, List, Optional

import requests

API_BASE = os.getenv("TASKFORGE_API_BASE", "http://api:8080").rstrip("/")
API_KEY = os.getenv("TASKFORGE_INTERNAL_KEY", "")
WORKER_ID = os.getenv("TASKFORGE_AI_WORKER_ID", f"ai-worker-{socket.gethostname()}")
OLLAMA_BASE = os.getenv("OLLAMA_BASE_URL", "http://ollama:11434").rstrip("/")
OLLAMA_MODEL = os.getenv("OLLAMA_MODEL", "qwen3:14b")
POLL_INTERVAL = int(os.getenv("POLL_INTERVAL_SECONDS", "8"))
CAPABILITIES = [x.strip() for x in os.getenv("TASKFORGE_AI_CAPABILITIES", "*").split(",") if x.strip()]
TIMEOUT = int(os.getenv("TASKFORGE_AI_TIMEOUT_SECONDS", "240"))

session = requests.Session()
session.headers.update({"X-Internal-Key": API_KEY})


def log(*parts: Any) -> None:
    print("[taskforge-ai-worker]", *parts, flush=True)


def post(path: str, payload: Dict[str, Any], expected: Optional[List[int]] = None):
    expected = expected or [200]
    resp = session.post(f"{API_BASE}{path}", json=payload, timeout=30)
    if resp.status_code not in expected:
        raise RuntimeError(f"POST {path} -> {resp.status_code}: {resp.text[:500]}")
    return resp


def pull_job():
    resp = post("/api/internal/ai/jobs/pull", {"workerId": WORKER_ID, "capabilities": CAPABILITIES}, expected=[200, 204])
    if resp.status_code == 204:
        return None
    return resp.json()


def heartbeat(job_id: str):
    post(f"/api/internal/ai/jobs/{job_id}/heartbeat", {"workerId": WORKER_ID}, expected=[200, 404])


def complete(job_id: str, result: Dict[str, Any]):
    post(f"/api/internal/ai/jobs/{job_id}/complete", {
        "workerId": WORKER_ID,
        "modelName": OLLAMA_MODEL,
        "resultJson": json.dumps(result, ensure_ascii=False),
    }, expected=[200, 404])


def fail(job_id: str, error_text: str, retryable: bool = True, retry_delay_seconds: int = 120):
    post(f"/api/internal/ai/jobs/{job_id}/fail", {
        "workerId": WORKER_ID,
        "errorText": error_text[:4000],
        "retryable": retryable,
        "retryDelaySeconds": retry_delay_seconds,
    }, expected=[200, 404])


def parse_payload(job: Dict[str, Any]) -> Dict[str, Any]:
    try:
        value = json.loads(job.get("inputJson") or "{}")
        return value if isinstance(value, dict) else {}
    except Exception:
        return {}


def pretty_payload(job: Dict[str, Any]) -> str:
    payload = parse_payload(job)
    return json.dumps(payload, ensure_ascii=False, indent=2)


def files_text(job: Dict[str, Any]) -> str:
    lines = []
    for f in job.get("files") or []:
        name = f.get("originalName") or f.get("fileKey") or "unknown-file"
        url = f.get("publicUrl") or ""
        mime = f.get("mimeType") or ""
        extra = f" ({mime})" if mime else ""
        lines.append(f"- {name}{extra}: {url}" if url else f"- {name}{extra}")
    return "\n".join(lines) if lines else "- no files attached"


def build_job_specific_instructions(job_type: str) -> str:
    t = (job_type or "").lower().strip()
    if t.startswith("assignment_generate"):
        return """
Сгенерируй черновик задания TaskForge.
Для math используй только blockType: info, number, expression, set, single-choice, multi-choice, order, match.
Для test используй question types: single-choice, multi-choice, fill, text.
Для code-test обязательно верни publicTests, hiddenTests и referenceSolutionPython.
Верни JSON вида {"draft": {...}} без markdown.
""".strip()
    if t == "assignment_validate_draft":
        return """
Проверь существующий draft. Верни только JSON вида:
{"draftId":"...","status":"passed|failed|needs-review","summary":"...","score":0.0,"checks":[{"name":"...","status":"passed|failed|warning","details":"..."}]}
""".strip()
    if t == "assignment_analyze_existing":
        return """
Проанализируй существующее задание и верни JSON:
{"assignmentId":"...","kind":"quality-audit","summary":"...","suggestions":["...","..."]}
""".strip()
    if t == "submission_review":
        return """
Верни JSON review попытки:
{"assignmentId":"...","userId":"...","sourceType":"code|test|math|image","sourceAttemptId":"...","verdict":"ok|needs-review|suspicious","score":0.0,"summary":"...","signals":[{"code":"...","weight":0.2,"note":"..."}]}
""".strip()
    if t == "user_risk_review":
        return """
Верни JSON risk-review пользователя:
{"userId":"...","riskLevel":"low|medium|high","score":0.0,"summary":"...","signals":[{"code":"...","weight":0.2,"note":"..."}]}
""".strip()
    return "Верни только валидный JSON по задаче."


def build_prompt(job: Dict[str, Any]) -> str:
    return (
        "Ты — TaskForge AI. Возвращай только валидный JSON без markdown.\n\n"
        f"Тип job: {job.get('type')}\n"
        f"Target entity type: {job.get('targetEntityType') or '-'}\n"
        f"Target entity id: {job.get('targetEntityId') or '-'}\n"
        f"Course id: {job.get('courseId') or '-'}\n\n"
        f"{build_job_specific_instructions(job.get('type') or '')}\n\n"
        f"Payload:\n{pretty_payload(job)}\n\n"
        f"Files:\n{files_text(job)}"
    )


def call_ollama(prompt: str) -> Dict[str, Any]:
    resp = requests.post(
        f"{OLLAMA_BASE}/api/generate",
        json={"model": OLLAMA_MODEL, "prompt": prompt, "stream": False, "options": {"temperature": 0.2}},
        timeout=TIMEOUT,
    )
    resp.raise_for_status()
    data = resp.json()
    raw = (data.get("response") or "").strip()
    if not raw:
        raise RuntimeError("Ollama returned empty response")
    return json.loads(raw)


def normalize_text(value: Any) -> str:
    return str(value or "").replace("\r\n", "\n").strip()


def safe_eval_number(expr: str) -> Optional[float]:
    try:
        node = ast.parse(expr, mode="eval")
    except Exception:
        return None
    safe_names = {"pi": 3.141592653589793, "e": 2.718281828459045, "abs": abs, "sqrt": lambda x: x ** 0.5}
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Pow, ast.USub, ast.UAdd, ast.Mod, ast.FloorDiv, ast.Load, ast.Call, ast.Name)
    for child in ast.walk(node):
        if not isinstance(child, allowed):
            return None
        if isinstance(child, ast.Call) and (not isinstance(child.func, ast.Name) or child.func.id not in safe_names):
            return None
        if isinstance(child, ast.Name) and child.id not in safe_names:
            return None
    try:
        return float(eval(compile(node, "<expr>", "eval"), {"__builtins__": {}}, safe_names))
    except Exception:
        return None


def run_python_solution(source_code: str, stdin_text: str) -> Dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="tf-ai-") as tmp:
        path = os.path.join(tmp, "solution.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(source_code)
        proc = subprocess.run([sys.executable, path], input=stdin_text, text=True, capture_output=True, timeout=6)
        return {"returncode": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


def validate_code_test_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    public_tests = list(draft.get("publicTests") or [])
    hidden_tests = list(draft.get("hiddenTests") or [])
    tests = public_tests + hidden_tests
    code = draft.get("referenceSolutionPython")
    if not tests:
        return {"status": "failed", "summary": "У code-test нет тестов для самопроверки.", "score": 0.0, "checks": [{"name": "tests", "status": "failed", "details": "publicTests/hiddenTests empty"}]}
    if not code:
        return {"status": "failed", "summary": "У code-test нет referenceSolutionPython.", "score": 0.0, "checks": [{"name": "reference-solution", "status": "failed", "details": "missing"}]}
    checks = []
    passed = 0
    if not public_tests:
        checks.append({"name": "public-tests", "status": "failed", "details": "Нет publicTests"})
    if not hidden_tests:
        checks.append({"name": "hidden-tests", "status": "failed", "details": "Нет hiddenTests"})
    for idx, test in enumerate(tests, start=1):
        res = run_python_solution(code, str(test.get("input") or ""))
        expected = normalize_text(test.get("expectedOutput") or "")
        actual = normalize_text(res.get("stdout") or "")
        ok = res.get("returncode") == 0 and actual == expected
        checks.append({"name": f"test-{idx}", "status": "passed" if ok else "failed", "details": f"expected={expected!r}; actual={actual!r}; rc={res.get('returncode')}"})
        if ok:
            passed += 1
    structural_failed = any(x.get("status") == "failed" for x in checks[:2]) if len(checks) >= 2 else False
    all_tests_passed = passed == len(tests)
    status = "passed" if all_tests_passed and not structural_failed else ("needs-review" if passed > 0 else "failed")
    return {"status": status, "summary": f"Python self-check: {passed}/{len(tests)} тестов прошло.", "score": passed / max(1, len(tests)), "checks": checks}


def validate_math_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    blocks = list(draft.get("blocks") or [])
    if not blocks:
        return {"status": "failed", "summary": "Math draft пустой.", "score": 0.0, "checks": [{"name": "blocks", "status": "failed", "details": "no blocks"}]}
    checks = []
    passed = 0
    if not public_tests:
        checks.append({"name": "public-tests", "status": "failed", "details": "Нет publicTests"})
    if not hidden_tests:
        checks.append({"name": "hidden-tests", "status": "failed", "details": "Нет hiddenTests"})
    for idx, block in enumerate(blocks, start=1):
        kind = str(block.get("blockType") or block.get("kind") or "info").strip().lower()
        title = str(block.get("title") or block.get("prompt") or f"Блок {idx}")
        ok = True
        details = "структура выглядит валидно"
        if not str(block.get("title") or block.get("prompt") or "").strip():
            ok = False
            details = "нет title/prompt"
        if kind in {"number", "expression", "set"}:
            answers = block.get("acceptedAnswers") or block.get("answers") or block.get("correctAnswers") or []
            if not answers:
                ok = False
                details = "нет acceptedAnswers"
            elif kind == "number" and any(safe_eval_number(str(x)) is None for x in answers):
                ok = False
                details = "acceptedAnswers не парсятся как числа/выражения"
        elif kind in {"single-choice", "multi-choice"}:
            if not (block.get("options") and (block.get("correctOptionKeys") or block.get("correctKeys") or block.get("correct"))):
                ok = False
                details = "нет options или correctOptionKeys"
        elif kind == "order" and len(block.get("items") or block.get("steps") or []) < 2:
            ok = False
            details = "для order нужно минимум 2 элемента"
        elif kind == "match" and not ((block.get("leftItems") or []) and (block.get("rightItems") or []) and (block.get("pairs") or block.get("matchPairs") or [])):
            ok = False
            details = "для match нужны leftItems/rightItems/pairs"
        checks.append({"name": title, "status": "passed" if ok else "failed", "details": details})
        if ok:
            passed += 1
    status = "passed" if passed == len(checks) else ("needs-review" if passed > 0 else "failed")
    return {"status": status, "summary": f"Math self-check: {passed}/{len(checks)} блоков выглядят валидно.", "score": passed / max(1, len(checks)), "checks": checks}


def validate_test_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    questions = list(draft.get("questions") or [])
    if not questions:
        return {"status": "failed", "summary": "Test draft пустой.", "score": 0.0, "checks": [{"name": "questions", "status": "failed", "details": "no questions"}]}
    checks = []
    passed = 0
    if not public_tests:
        checks.append({"name": "public-tests", "status": "failed", "details": "Нет publicTests"})
    if not hidden_tests:
        checks.append({"name": "hidden-tests", "status": "failed", "details": "Нет hiddenTests"})
    for idx, q in enumerate(questions, start=1):
        qtype = str(q.get("type") or q.get("questionType") or "single-choice")
        title = str(q.get("prompt") or q.get("title") or f"Вопрос {idx}")
        ok = True
        details = "структура выглядит валидно"
        if not str(block.get("title") or block.get("prompt") or "").strip():
            ok = False
            details = "нет title/prompt"
        if qtype in {"single-choice", "multi-choice"}:
            if not (q.get("options") and (q.get("correctOptionKeys") or q.get("correctKeys") or q.get("correct"))):
                ok = False
                details = "нет options или correctOptionKeys"
        else:
            answers = q.get("acceptedAnswers") or q.get("answers") or q.get("correctAnswers") or []
            if not answers:
                ok = False
                details = "нет acceptedAnswers"
        checks.append({"name": title, "status": "passed" if ok else "failed", "details": details})
        if ok:
            passed += 1
    status = "passed" if passed == len(checks) else ("needs-review" if passed > 0 else "failed")
    return {"status": status, "summary": f"Test self-check: {passed}/{len(checks)} вопросов выглядят валидно.", "score": passed / max(1, len(checks)), "checks": checks}


def run_self_check(draft: Dict[str, Any]) -> Dict[str, Any]:
    assignment_type = str(draft.get("assignmentType") or "").strip().lower()
    if assignment_type == "code-test":
        return validate_code_test_draft(draft)
    if assignment_type == "math":
        return validate_math_draft(draft)
    if assignment_type == "test":
        return validate_test_draft(draft)
    return {"status": "needs-review", "summary": f"Нет self-check для типа {assignment_type or 'unknown'}.", "score": 0.0, "checks": [{"name": "unsupported-type", "status": "warning", "details": assignment_type or 'unknown'}]}


def attach_self_check(result: Dict[str, Any], draft: Dict[str, Any], validation: Dict[str, Any]) -> Dict[str, Any]:
    meta = draft.get("meta") if isinstance(draft.get("meta"), dict) else {}
    meta["selfCheck"] = validation
    draft["meta"] = meta
    result["draft"] = draft
    result["draftValidation"] = validation
    return result


def fallback_result(job: Dict[str, Any]) -> Dict[str, Any]:
    payload = parse_payload(job)
    t = (job.get("type") or "").lower().strip()
    if t.startswith("assignment_generate"):
        assignment_type = str(payload.get("assignmentType") or "math").strip().lower()
        if assignment_type == "code-test":
            draft = {
                "assignmentType": "code-test",
                "title": "Черновик AI code-test",
                "description": "AI недоступен, создан fallback code-test.",
                "courseId": job.get("courseId"),
                "difficulty": 2,
                "rating": 1,
                "tags": "ai,fallback,code",
                "allowedLanguages": ["python"],
                "publicTests": [{"input": "2\n", "expectedOutput": "4"}],
                "hiddenTests": [{"input": "5\n", "expectedOutput": "25"}],
                "referenceSolutionPython": "import sys\n\ndef solve(data: str) -> str:\n    n = int(data.strip())\n    return str(n * n)\n\nif __name__ == '__main__':\n    print(solve(sys.stdin.read()))",
            }
            return attach_self_check({"draft": draft}, draft, run_self_check(draft))
        if assignment_type == "test":
            draft = {
                "assignmentType": "test",
                "title": "Черновик AI test",
                "description": "AI недоступен, создан fallback test.",
                "courseId": job.get("courseId"),
                "difficulty": 2,
                "rating": 1,
                "tags": "ai,fallback,test",
                "settings": {"maxAttempts": 3, "passPercent": 60, "shuffleQuestions": True, "shuffleAnswers": True, "allowReview": True},
                "questions": [{"type": "text", "prompt": "Введите ok", "acceptedAnswers": ["ok"], "trim": True, "caseSensitive": False}],
            }
            return attach_self_check({"draft": draft}, draft, run_self_check(draft))
        draft = {
            "assignmentType": "math",
            "title": "Черновик AI math",
            "description": "AI недоступен, создан fallback math.",
            "courseId": job.get("courseId"),
            "difficulty": 2,
            "rating": 1,
            "tags": "ai,fallback,math",
            "settings": {"maxAttempts": 3, "passPercent": 60, "shuffleBlocks": False, "allowReview": True},
            "blocks": [{"blockType": "number", "title": "Ответ", "prompt": "Введите квадрат числа 2", "acceptedAnswers": ["4"], "points": 1}],
        }
        return attach_self_check({"draft": draft}, draft, run_self_check(draft))
    if t == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        return validation
    if t == "assignment_analyze_existing":
        return {"assignmentId": job.get("targetEntityId"), "kind": "quality-audit", "summary": "Fallback-анализ: модель недоступна.", "suggestions": ["Проверь ясность формулировки.", "Проверь баланс сложности."]}
    if t == "submission_review":
        return {"assignmentId": job.get("targetEntityId"), "verdict": "needs-review", "score": 0.5, "summary": "Fallback-review: нужна ручная проверка.", "signals": [{"code": "fallback_review", "weight": 0.5, "note": "model unavailable"}]}
    if t == "user_risk_review":
        return {"userId": job.get("targetEntityId"), "riskLevel": "medium", "score": 0.4, "summary": "Fallback-risk-report: выполните ручную проверку.", "signals": [{"code": "fallback_risk", "weight": 0.4, "note": "model unavailable"}]}
    return {"summary": "Нейросеть недоступна, job завершён fallback-ответом."}


def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()
    if job_type == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        return validation
    prompt = build_prompt(job)
    try:
        result = call_ollama(prompt)
    except Exception as ex:
        log("ollama failed, using fallback:", ex)
        return fallback_result(job)
    if job_type.startswith("assignment_generate") and payload.get("enableSelfCheck", True):
        draft = result.get("draft")
        if isinstance(draft, dict):
            result = attach_self_check(result, draft, run_self_check(draft))
    return result


def main():
    if not API_KEY:
        raise RuntimeError("TASKFORGE_INTERNAL_KEY is not configured")
    log("started", {"api": API_BASE, "workerId": WORKER_ID, "capabilities": CAPABILITIES, "ollama": OLLAMA_BASE, "model": OLLAMA_MODEL})
    while True:
        try:
            job = pull_job()
            if not job:
                time.sleep(POLL_INTERVAL)
                continue
            job_id = job["id"]
            heartbeat(job_id)
            log("picked job", job_id, job.get("type"))
            result = process_job(job)
            complete(job_id, result)
            log("completed job", job_id)
        except KeyboardInterrupt:
            raise
        except Exception as ex:
            log("loop error:", ex)
            time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
