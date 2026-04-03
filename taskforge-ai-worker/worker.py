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
MAX_REFERENCE_ASSIGNMENTS = int(os.getenv("TASKFORGE_AI_MAX_REFERENCE_ASSIGNMENTS", "20"))
MIN_PUBLIC_TESTS = int(os.getenv("TASKFORGE_AI_MIN_PUBLIC_TESTS", "2"))
MIN_HIDDEN_TESTS = int(os.getenv("TASKFORGE_AI_MIN_HIDDEN_TESTS", "5"))
MIN_DESCRIPTION_LEN = int(os.getenv("TASKFORGE_AI_MIN_DESCRIPTION_LEN", "200"))
MAX_REPAIR_ATTEMPTS = int(os.getenv("TASKFORGE_AI_REPAIR_ATTEMPTS", "2"))
OLLAMA_NUM_CTX = int(os.getenv("OLLAMA_NUM_CTX", os.getenv("OLLAMA_CONTEXT_LENGTH", "16384")))
OLLAMA_TEMPERATURE = float(os.getenv("OLLAMA_TEMPERATURE", "0.15"))
MAX_REFERENCE_DESCRIPTION_LEN = int(os.getenv("TASKFORGE_AI_REFERENCE_DESCRIPTION_LEN", "260"))

session = requests.Session()
session.headers.update({"X-Internal-Key": API_KEY})


def log(*parts: Any) -> None:
    print("[taskforge-ai-worker]", *parts, flush=True)


def post(path: str, payload: Dict[str, Any], expected: Optional[List[int]] = None):
    expected = expected or [200]
    body_preview = json.dumps(payload, ensure_ascii=False)[:1200]
    log("HTTP POST >>>", path, "expected=", expected, "payload=", body_preview)
    started = time.time()
    resp = session.post(f"{API_BASE}{path}", json=payload, timeout=30)
    elapsed_ms = int((time.time() - started) * 1000)
    text_preview = (resp.text or "")[:1200]
    log("HTTP POST <<<", path, "status=", resp.status_code, "elapsedMs=", elapsed_ms, "response=", text_preview)
    if resp.status_code not in expected:
        raise RuntimeError(f"POST {path} -> {resp.status_code}: {resp.text[:500]}")
    return resp


def pull_job():
    log("polling for job", {"workerId": WORKER_ID, "capabilities": CAPABILITIES})
    resp = post("/api/internal/ai/jobs/pull", {"workerId": WORKER_ID, "capabilities": CAPABILITIES}, expected=[200, 204])
    if resp.status_code == 204:
        log("poll result: no job")
        return None
    job = resp.json()
    log("poll result: picked job", {"id": job.get("id"), "type": job.get("type"), "priority": job.get("priority"), "courseId": job.get("courseId"), "targetEntityType": job.get("targetEntityType"), "targetEntityId": job.get("targetEntityId")})
    return job


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
        raw = job.get("inputJson") or "{}"
        value = json.loads(raw)
        if isinstance(value, dict):
            log("parsed payload ok", {"jobId": job.get("id"), "keys": list(value.keys())[:30], "inputJsonLen": len(raw)})
            return value
        log("parsed payload is not dict", {"jobId": job.get("id"), "type": type(value).__name__})
        return {}
    except Exception as ex:
        log("parsed payload failed", {"jobId": job.get("id"), "error": str(ex), "inputJsonPreview": (job.get("inputJson") or "")[:800]})
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


def truncate_text(value: Any, limit: int) -> str:
    text = normalize_text(value)
    return text if len(text) <= limit else text[:limit] + "..."


def summarize_description(value: Any, limit: int = MAX_REFERENCE_DESCRIPTION_LEN) -> str:
    raw = str(value or "")
    raw = raw.replace("\r\n", " ").replace("\n", " ").replace("\r", " ")
    raw = " ".join(raw.split())
    return raw if len(raw) <= limit else raw[:limit] + "..."


def compact_reference_assignments(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    refs = payload.get("referenceAssignments")
    if not isinstance(refs, list):
        return []
    compact = []
    for idx, item in enumerate(refs[:MAX_REFERENCE_ASSIGNMENTS], start=1):
        if not isinstance(item, dict):
            continue
        compact.append({
            "index": idx,
            "id": item.get("id"),
            "courseId": item.get("courseId"),
            "type": item.get("type"),
            "title": truncate_text(item.get("title"), 160),
            "descriptionSummary": summarize_description(item.get("description") or item.get("Description"), MAX_REFERENCE_DESCRIPTION_LEN),
            "difficulty": item.get("difficulty"),
            "rating": item.get("rating"),
            "tags": item.get("tags"),
            "allowedLanguagesCsv": item.get("allowedLanguagesCsv"),
            "hiddenTestsCount": item.get("hiddenTestsCount"),
            "blocksCount": item.get("blocksCount"),
            "questionsCount": item.get("questionsCount"),
            "publicCases": item.get("publicCases")[:2] if isinstance(item.get("publicCases"), list) else [],
            "forbiddenCalls": item.get("forbiddenCalls")[:10] if isinstance(item.get("forbiddenCalls"), list) else [],
            "requiredCalls": item.get("requiredCalls")[:10] if isinstance(item.get("requiredCalls"), list) else [],
        })
    return compact


def build_generation_requirements(payload: Dict[str, Any]) -> str:
    assignment_type = str(payload.get("assignmentType") or "").strip().lower()
    quality = payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {}
    target_schema = payload.get("targetSchema") if isinstance(payload.get("targetSchema"), dict) else {}
    lines = [
        "Работай как senior-редактор олимпиадных и учебных заданий.",
        "Сначала молча изучи referenceAssignments и вытащи паттерны хороших задач: стиль условия, детализацию, тестовое покрытие, допустимые языки, типовые ограничения.",
        "Потом придумай новое задание, не копируя названия, формулировки и тесты дословно.",
        "Затем сам проведи внутренние этапы: plan -> draft -> critique -> repair -> final.",
        "Верни только финальный JSON без markdown и без комментариев снаружи JSON.",
        "description обязано быть полноценным условием, а не одной строкой или заглушкой.",
        "description пиши в HTML: используй <p>, <ul>, <li>, при необходимости <strong>.",
        "Нельзя возвращать служебные заглушки вроде 'AI-generated draft' или 'Черновик задания опубликован из AI-draft'.",
    ]
    if assignment_type == "code-test":
        lines.extend([
            f"Нужно минимум {quality.get('minPublicTests', MIN_PUBLIC_TESTS)} publicTests.",
            f"Нужно минимум {quality.get('minHiddenTests', MIN_HIDDEN_TESTS)} hiddenTests.",
            f"description должен быть не короче {quality.get('minDescriptionLength', MIN_DESCRIPTION_LEN)} символов.",
            "В description обязательно раскрой: суть задачи, формат входных данных, формат выходных данных, ограничения, хотя бы одну заметку или пояснение.",
            "referenceSolutionPython должен быть полностью рабочим, детерминированным, читать stdin и печатать только ответ.",
            "Сгенерируй edge cases: минимальные значения, типичные значения, пограничные случаи.",
            "Если задача требует ограничений по коду, добавь forbiddenCalls и/или requiredCalls как массивы строк.",
            "Не делай все тесты однотипными.",
        ])
    elif assignment_type == "test":
        lines.extend([
            f"description должен быть не короче {quality.get('minDescriptionLength', 120)} символов.",
            f"Нужно минимум {quality.get('minQuestions', 5)} вопросов.",
            "Сделай вопросы содержательными и без дублей.",
        ])
    elif assignment_type == "math":
        lines.extend([
            f"description должен быть не короче {quality.get('minDescriptionLength', 120)} символов.",
            f"Нужно минимум {quality.get('minBlocks', 2)} блока.",
            "Должен быть хотя бы один блок с проверяемым ответом.",
        ])
    if target_schema:
        lines.append("Ориентируйся на targetSchema как на контракт полей, а не как на источник готовых примеров.")
    return "\n".join(lines)


def build_job_specific_instructions(job_type: str, payload: Dict[str, Any]) -> str:
    t = (job_type or "").lower().strip()
    if t.startswith("assignment_generate"):
        return build_generation_requirements(payload)
    if t == "assignment_validate_draft":
        return (
            "Проверь существующий draft максимально строго."
            " Верни только JSON вида: "
            "{\"draftId\":\"...\",\"status\":\"passed|failed|needs-review\",\"summary\":\"...\",\"score\":0.0,\"checks\":[{\"name\":\"...\",\"status\":\"passed|failed|warning\",\"details\":\"...\"}]}"
        )
    if t == "assignment_analyze_existing":
        return "Проанализируй существующее задание и верни JSON: {\"assignmentId\":\"...\",\"kind\":\"quality-audit\",\"summary\":\"...\",\"suggestions\":[\"...\",\"...\"]}"
    if t == "submission_review":
        return "Верни JSON review попытки: {\"assignmentId\":\"...\",\"userId\":\"...\",\"sourceType\":\"code|test|math|image\",\"sourceAttemptId\":\"...\",\"verdict\":\"ok|needs-review|suspicious\",\"score\":0.0,\"summary\":\"...\",\"signals\":[{\"code\":\"...\",\"weight\":0.2,\"note\":\"...\"}]}"
    if t == "user_risk_review":
        return "Верни JSON risk-review пользователя: {\"userId\":\"...\",\"riskLevel\":\"low|medium|high\",\"score\":0.0,\"summary\":\"...\",\"signals\":[{\"code\":\"...\",\"weight\":0.2,\"note\":\"...\"}]}"
    return "Верни только валидный JSON по задаче."


def build_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    payload_for_prompt = dict(payload)
    payload_for_prompt["count"] = min(int(payload.get("count") or 1), 1)
    payload_for_prompt["referenceAssignments"] = compact_reference_assignments(payload)
    prompt_payload = json.dumps(payload_for_prompt, ensure_ascii=False, indent=2)
    return (
        "Ты — TaskForge AI. Возвращай только валидный JSON без markdown.\n\n"
        f"Тип job: {job.get('type')}\n"
        f"Target entity type: {job.get('targetEntityType') or '-'}\n"
        f"Target entity id: {job.get('targetEntityId') or '-'}\n"
        f"Course id: {job.get('courseId') or '-'}\n\n"
        "Изучи referenceAssignments как примеры стиля и структуры, но не копируй формулировки и тесты дословно.\n"
        f"{build_job_specific_instructions(job.get('type') or '', payload)}\n\n"
        "referenceAssignments уже сжаты и отсортированы backend'ом по релевантности. Если их нет, всё равно сгенерируй полноценный draft по qualityGates.\n\n"
        f"Payload:\n{prompt_payload}\n\n"
        f"Files:\n{files_text(job)}"
    )


def build_repair_prompt(job: Dict[str, Any], payload: Dict[str, Any], bad_result: Dict[str, Any], validation: Dict[str, Any], attempt_no: int) -> str:
    draft = bad_result.get("draft") if isinstance(bad_result.get("draft"), dict) else {}
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    return (
        "Ты — TaskForge AI. Сейчас нужно исправить неудачный draft. Верни только валидный JSON вида {\"draft\": {...}} без markdown.\n\n"
        f"Тип job: {job.get('type')}\n"
        f"Попытка исправления: {attempt_no}\n\n"
        f"Требования:\n{build_job_specific_instructions(job.get('type') or '', payload)}\n\n"
        f"Изначальный payload:\n{json.dumps(compact_payload, ensure_ascii=False, indent=2)}\n\n"
        f"Плохой draft, который нужно переписать:\n{json.dumps(draft, ensure_ascii=False, indent=2)}\n\n"
        f"Ошибки self-check:\n{json.dumps(validation, ensure_ascii=False, indent=2)}\n\n"
        "Исправь все замечания, усили условие, добавь недостающие тесты и верни полностью новый готовый draft."
    )


def call_ollama(prompt: str) -> Dict[str, Any]:
    prompt_preview = prompt[:2000]
    log("ollama request >>>", {"base": OLLAMA_BASE, "model": OLLAMA_MODEL, "timeout": TIMEOUT, "promptLen": len(prompt), "promptPreview": prompt_preview})
    started = time.time()
    resp = requests.post(
        f"{OLLAMA_BASE}/api/generate",
        json={"model": OLLAMA_MODEL, "prompt": prompt, "stream": False, "options": {"temperature": OLLAMA_TEMPERATURE, "num_ctx": OLLAMA_NUM_CTX}},
        timeout=TIMEOUT,
    )
    elapsed_ms = int((time.time() - started) * 1000)
    response_preview = (resp.text or "")[:2000]
    log("ollama response <<<", {"status": resp.status_code, "elapsedMs": elapsed_ms, "bodyPreview": response_preview})
    resp.raise_for_status()
    data = resp.json()
    raw = (data.get("response") or "").strip()
    if not raw:
        raise RuntimeError("Ollama returned empty response")
    log("ollama parsed response", {"responseLen": len(raw), "responsePreview": raw[:1200]})
    return json.loads(raw)


def has_html_markup(text: str) -> bool:
    lowered = text.lower()
    return any(tag in lowered for tag in ["<p", "<ul", "<ol", "<li", "<strong", "<br", "<h1", "<h2", "<h3"])


def collect_quality_checks_common(draft: Dict[str, Any]) -> List[Dict[str, Any]]:
    checks: List[Dict[str, Any]] = []
    title = normalize_text(draft.get("title"))
    description = normalize_text(draft.get("description"))
    if title:
        checks.append({"name": "title", "status": "passed", "details": f"title.len={len(title)}"})
    else:
        checks.append({"name": "title", "status": "failed", "details": "Пустой title"})
    if len(description) >= MIN_DESCRIPTION_LEN:
        checks.append({"name": "description-length", "status": "passed", "details": f"description.len={len(description)}"})
    elif description:
        checks.append({"name": "description-length", "status": "failed", "details": f"description слишком короткое: {len(description)}"})
    else:
        checks.append({"name": "description-length", "status": "failed", "details": "description отсутствует"})
    if has_html_markup(description):
        checks.append({"name": "description-format", "status": "passed", "details": "description содержит html-разметку"})
    else:
        checks.append({"name": "description-format", "status": "warning", "details": "description без html-разметки"})
    return checks


def summarize_status(checks: List[Dict[str, Any]]) -> str:
    has_failed = any(x.get("status") == "failed" for x in checks)
    has_warning = any(x.get("status") == "warning" for x in checks)
    if has_failed:
        passed_count = sum(1 for x in checks if x.get("status") == "passed")
        return "needs-review" if passed_count > 0 else "failed"
    return "needs-review" if has_warning else "passed"


def validate_code_test_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    public_tests = list(draft.get("publicTests") or [])
    hidden_tests = list(draft.get("hiddenTests") or [])
    tests = public_tests + hidden_tests
    code = draft.get("referenceSolutionPython")
    checks: List[Dict[str, Any]] = collect_quality_checks_common(draft)

    if len(public_tests) >= MIN_PUBLIC_TESTS:
        checks.append({"name": "public-tests-count", "status": "passed", "details": f"publicTests={len(public_tests)}"})
    else:
        checks.append({"name": "public-tests-count", "status": "failed", "details": f"Нужно минимум {MIN_PUBLIC_TESTS}, сейчас {len(public_tests)}"})
    if len(hidden_tests) >= MIN_HIDDEN_TESTS:
        checks.append({"name": "hidden-tests-count", "status": "passed", "details": f"hiddenTests={len(hidden_tests)}"})
    else:
        checks.append({"name": "hidden-tests-count", "status": "failed", "details": f"Нужно минимум {MIN_HIDDEN_TESTS}, сейчас {len(hidden_tests)}"})

    if not tests:
        checks.append({"name": "tests", "status": "failed", "details": "publicTests/hiddenTests empty"})
    else:
        unique_pairs = {f"{normalize_text(t.get('input'))}|{normalize_text(t.get('expectedOutput'))}" for t in tests if isinstance(t, dict)}
        if len(unique_pairs) == len(tests):
            checks.append({"name": "tests-unique", "status": "passed", "details": f"unique={len(unique_pairs)}"})
        else:
            checks.append({"name": "tests-unique", "status": "warning", "details": f"Есть дубли тестов: unique={len(unique_pairs)} total={len(tests)}"})

    if isinstance(code, str) and normalize_text(code):
        if "solve" in code or "main" in code.lower():
            checks.append({"name": "reference-solution-shape", "status": "passed", "details": "Есть solve/main"})
        else:
            checks.append({"name": "reference-solution-shape", "status": "warning", "details": "Нет явного solve/main"})
    else:
        checks.append({"name": "reference-solution", "status": "failed", "details": "missing"})
        return {"status": summarize_status(checks), "summary": "У code-test нет referenceSolutionPython.", "score": 0.0, "checks": checks}

    passed_runtime = 0
    for idx, test in enumerate(tests, start=1):
        if not isinstance(test, dict):
            checks.append({"name": f"test-{idx}", "status": "failed", "details": "Тест не является объектом"})
            continue
        stdin_text = str(test.get("input") or "")
        expected = normalize_text(test.get("expectedOutput") or "")
        try:
            res = run_python_solution(code, stdin_text)
            actual = normalize_text(res.get("stdout") or "")
            ok = res.get("returncode") == 0 and actual == expected
            checks.append({"name": f"test-{idx}", "status": "passed" if ok else "failed", "details": f"expected={expected!r}; actual={actual!r}; rc={res.get('returncode')} stderr={truncate_text(res.get('stderr') or '', 200)!r}"})
            if ok:
                passed_runtime += 1
        except Exception as ex:
            checks.append({"name": f"test-{idx}", "status": "failed", "details": f"runtime error: {ex}"})

    status = summarize_status(checks)
    score = sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks))
    summary = f"Code-test self-check: runtime {passed_runtime}/{len(tests)}, checks passed {sum(1 for x in checks if x.get('status') == 'passed')}/{len(checks)}."
    return {"status": status, "summary": summary, "score": score, "checks": checks}


def validate_math_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    blocks = list(draft.get("blocks") or [])
    checks: List[Dict[str, Any]] = collect_quality_checks_common(draft)
    if not blocks:
        checks.append({"name": "blocks", "status": "failed", "details": "no blocks"})
        return {"status": summarize_status(checks), "summary": "Math draft пустой.", "score": 0.0, "checks": checks}
    answer_blocks = 0
    for idx, block in enumerate(blocks, start=1):
        kind = str(block.get("blockType") or block.get("kind") or "info").strip().lower()
        title = str(block.get("title") or block.get("prompt") or f"Блок {idx}")
        ok = True
        details = "структура выглядит валидно"
        if not normalize_text(block.get("title") or block.get("prompt") or ""):
            ok = False
            details = "нет title/prompt"
        if kind in {"number", "expression", "set"}:
            answer_blocks += 1
            answers = block.get("acceptedAnswers") or block.get("answers") or block.get("correctAnswers") or []
            if not answers:
                ok = False
                details = "нет acceptedAnswers"
            elif kind == "number" and any(safe_eval_number(str(x)) is None for x in answers):
                ok = False
                details = "acceptedAnswers не парсятся как числа/выражения"
        elif kind in {"single-choice", "multi-choice"}:
            answer_blocks += 1
            if not (block.get("options") and (block.get("correctOptionKeys") or block.get("correctKeys") or block.get("correct"))):
                ok = False
                details = "нет options или correctOptionKeys"
        elif kind == "order" and len(block.get("items") or block.get("steps") or []) < 2:
            answer_blocks += 1
            ok = False
            details = "для order нужно минимум 2 элемента"
        elif kind == "match" and not ((block.get("leftItems") or []) and (block.get("rightItems") or []) and (block.get("pairs") or block.get("matchPairs") or [])):
            answer_blocks += 1
            ok = False
            details = "для match нужны leftItems/rightItems/pairs"
        checks.append({"name": title, "status": "passed" if ok else "failed", "details": details})
    checks.append({"name": "answer-blocks", "status": "passed" if answer_blocks > 0 else "failed", "details": f"answerBlocks={answer_blocks}"})
    status = summarize_status(checks)
    score = sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks))
    return {"status": status, "summary": f"Math self-check: blocks={len(blocks)}, answerBlocks={answer_blocks}.", "score": score, "checks": checks}


def validate_test_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    questions = list(draft.get("questions") or [])
    checks: List[Dict[str, Any]] = collect_quality_checks_common(draft)
    if len(questions) >= 5:
        checks.append({"name": "questions-count", "status": "passed", "details": f"questions={len(questions)}"})
    elif questions:
        checks.append({"name": "questions-count", "status": "failed", "details": f"Нужно минимум 5, сейчас {len(questions)}"})
    else:
        checks.append({"name": "questions-count", "status": "failed", "details": "no questions"})
        return {"status": summarize_status(checks), "summary": "Test draft пустой.", "score": 0.0, "checks": checks}
    seen_prompts = set()
    duplicate_prompts = 0
    for idx, q in enumerate(questions, start=1):
        qtype = str(q.get("type") or q.get("questionType") or "single-choice").strip().lower()
        title = str(q.get("prompt") or q.get("title") or f"Вопрос {idx}")
        ok = True
        details = "структура выглядит валидно"
        normalized_prompt = normalize_text(q.get("prompt") or q.get("title") or "")
        if not normalized_prompt:
            ok = False
            details = "нет title/prompt"
        elif normalized_prompt in seen_prompts:
            duplicate_prompts += 1
        else:
            seen_prompts.add(normalized_prompt)
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
    if duplicate_prompts == 0:
        checks.append({"name": "duplicate-prompts", "status": "passed", "details": "Дубликатов вопросов нет"})
    else:
        checks.append({"name": "duplicate-prompts", "status": "warning", "details": f"Дубликатов вопросов: {duplicate_prompts}"})
    status = summarize_status(checks)
    score = sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks))
    return {"status": status, "summary": f"Test self-check: questions={len(questions)}, duplicates={duplicate_prompts}.", "score": score, "checks": checks}


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
    log("building fallback result", {"jobId": job.get("id"), "type": job.get("type")})
    payload = parse_payload(job)
    t = (job.get("type") or "").lower().strip()
    if t.startswith("assignment_generate"):
        assignment_type = str(payload.get("assignmentType") or "math").strip().lower()
        if assignment_type == "code-test":
            draft = {
                "assignmentType": "code-test",
                "title": "AI fallback: сумма чисел от 1 до n",
                "description": "<p>По данному целому числу <strong>n</strong> требуется вычислить сумму всех целых чисел от 1 до n включительно.</p><p><strong>Входные данные:</strong> одно целое число n.</p><p><strong>Выходные данные:</strong> одно число — искомая сумма.</p><p><strong>Ограничения:</strong> 1 ≤ n ≤ 10^6.</p>",
                "courseId": job.get("courseId"),
                "difficulty": 2,
                "rating": 1,
                "tags": "ai,fallback,code",
                "allowedLanguages": ["python", "cpp", "csharp"],
                "publicTests": [{"input": "2\n", "expectedOutput": "3"}, {"input": "5\n", "expectedOutput": "15"}],
                "hiddenTests": [{"input": "1\n", "expectedOutput": "1"}, {"input": "3\n", "expectedOutput": "6"}, {"input": "10\n", "expectedOutput": "55"}, {"input": "100\n", "expectedOutput": "5050"}, {"input": "1000\n", "expectedOutput": "500500"}],
                "referenceSolutionPython": "import sys\n\ndef solve(data: str) -> str:\n    n = int(data.strip())\n    return str(n * (n + 1) // 2)\n\nif __name__ == '__main__':\n    print(solve(sys.stdin.read()))",
                "requiredCalls": ["solve"],
                "forbiddenCalls": ["Process.Start", "__import__"],
            }
            return attach_self_check({"draft": draft}, draft, run_self_check(draft))
        if assignment_type == "test":
            draft = {
                "assignmentType": "test",
                "title": "AI fallback: базовый тест",
                "description": "<p>Ответьте на вопросы по теме.</p><p>Внимательно прочитайте формулировки и выберите правильные варианты.</p>",
                "courseId": job.get("courseId"),
                "difficulty": 2,
                "rating": 1,
                "tags": "ai,fallback,test",
                "settings": {"maxAttempts": 3, "passPercent": 60, "shuffleQuestions": True, "shuffleAnswers": True, "allowReview": True},
                "questions": [
                    {"type": "text", "prompt": "Введите ok", "acceptedAnswers": ["ok"], "trim": True, "caseSensitive": False},
                    {"type": "single-choice", "prompt": "Сколько будет 2+2?", "options": [{"key": "a", "text": "4"}, {"key": "b", "text": "5"}], "correctOptionKeys": ["a"]},
                    {"type": "fill", "prompt": "Продолжите последовательность: 2, 4, 6, __", "acceptedAnswers": ["8"]},
                    {"type": "single-choice", "prompt": "Какая буква первая в алфавите?", "options": [{"key": "a", "text": "A"}, {"key": "b", "text": "B"}], "correctOptionKeys": ["a"]},
                    {"type": "text", "prompt": "Введите слово test", "acceptedAnswers": ["test"]}
                ],
            }
            return attach_self_check({"draft": draft}, draft, run_self_check(draft))
        draft = {
            "assignmentType": "math",
            "title": "AI fallback: простая математическая задача",
            "description": "<p>Решите предложенную задачу и введите ответы в блоки.</p>",
            "courseId": job.get("courseId"),
            "difficulty": 2,
            "rating": 1,
            "tags": "ai,fallback,math",
            "settings": {"maxAttempts": 3, "passPercent": 60, "shuffleBlocks": False, "allowReview": True},
            "blocks": [
                {"blockType": "info", "title": "Условие", "prompt": "Найдите значение выражения 2 + 2.", "points": 0},
                {"blockType": "number", "title": "Ответ", "prompt": "Введите ответ", "acceptedAnswers": ["4"], "points": 1}
            ],
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


def try_improve_generation(job: Dict[str, Any], payload: Dict[str, Any], result: Dict[str, Any]) -> Dict[str, Any]:
    draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
    if not isinstance(draft, dict):
        return result
    validation = run_self_check(draft)
    log("self-check generated draft", {"jobId": job.get("id"), "status": validation.get("status"), "score": validation.get("score"), "summary": validation.get("summary")})
    if validation.get("status") == "passed":
        return attach_self_check(result, draft, validation)

    current_result = result
    current_validation = validation
    for attempt_no in range(1, MAX_REPAIR_ATTEMPTS + 1):
        try:
            repair_prompt = build_repair_prompt(job, payload, current_result, current_validation, attempt_no)
            log("repair prompt built", {"jobId": job.get("id"), "attempt": attempt_no, "promptLen": len(repair_prompt), "promptPreview": repair_prompt[:1500]})
            repaired = call_ollama(repair_prompt)
            repaired_draft = repaired.get("draft") if isinstance(repaired.get("draft"), dict) else None
            if not isinstance(repaired_draft, dict):
                log("repair returned no draft", {"jobId": job.get("id"), "attempt": attempt_no})
                continue
            current_validation = run_self_check(repaired_draft)
            log("repair self-check", {"jobId": job.get("id"), "attempt": attempt_no, "status": current_validation.get("status"), "score": current_validation.get("score"), "summary": current_validation.get("summary")})
            current_result = repaired
            if current_validation.get("status") == "passed":
                return attach_self_check(current_result, repaired_draft, current_validation)
        except Exception as ex:
            log("repair attempt failed", {"jobId": job.get("id"), "attempt": attempt_no, "error": str(ex)})
    final_draft = current_result.get("draft") if isinstance(current_result.get("draft"), dict) else draft
    return attach_self_check(current_result, final_draft if isinstance(final_draft, dict) else draft, current_validation)


def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    log("process_job >>>", {"jobId": job.get("id"), "type": job.get("type"), "courseId": job.get("courseId"), "targetEntityType": job.get("targetEntityType"), "targetEntityId": job.get("targetEntityId")})
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()
    if job_type == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        log("running local validation self-check", {"jobId": job.get("id"), "draftKeys": list(draft.keys())[:30]})
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        log("process_job <<< validation result", {"jobId": job.get("id"), "status": validation.get("status"), "score": validation.get("score"), "summary": validation.get("summary")})
        return validation
    prompt = build_prompt(job, payload)
    log("built prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500], "referenceAssignments": len(compact_reference_assignments(payload))})
    try:
        result = call_ollama(prompt)
    except Exception as ex:
        log("ollama failed, using fallback:", ex)
        fallback = fallback_result(job)
        log("process_job <<< fallback result", {"jobId": job.get("id"), "keys": list(fallback.keys())[:30]})
        return fallback
    if job_type.startswith("assignment_generate") and payload.get("enableSelfCheck", True):
        result = try_improve_generation(job, payload, result)
    log("process_job <<< result ready", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
    return result


def main():
    if not API_KEY:
        raise RuntimeError("TASKFORGE_INTERNAL_KEY is not configured")
    log("started", {"api": API_BASE, "workerId": WORKER_ID, "capabilities": CAPABILITIES, "ollama": OLLAMA_BASE, "model": OLLAMA_MODEL, "pollInterval": POLL_INTERVAL, "timeout": TIMEOUT, "maxReferences": MAX_REFERENCE_ASSIGNMENTS, "repairAttempts": MAX_REPAIR_ATTEMPTS})
    while True:
        loop_started = time.time()
        try:
            log("loop tick >>>")
            job = pull_job()
            if not job:
                log("loop tick no job; sleeping", POLL_INTERVAL)
                time.sleep(POLL_INTERVAL)
                continue
            job_id = job["id"]
            log("sending heartbeat before processing", {"jobId": job_id, "workerId": WORKER_ID})
            heartbeat(job_id)
            log("picked job", job_id, job.get("type"))
            result = process_job(job)
            result_preview = json.dumps(result, ensure_ascii=False)[:2000]
            log("sending complete", {"jobId": job_id, "resultPreview": result_preview})
            complete(job_id, result)
            log("completed job", job_id, "loopElapsedMs=", int((time.time() - loop_started) * 1000))
        except KeyboardInterrupt:
            raise
        except Exception as ex:
            log("loop error:", repr(ex))
            log("loop error type:", type(ex).__name__)
            print("[taskforge-ai-worker] traceback follows", flush=True)
            import traceback
            traceback.print_exc()
            time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
