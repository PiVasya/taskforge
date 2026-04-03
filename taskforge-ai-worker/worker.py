import ast
import difflib
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


def compact_historical_planner_priors(payload: Dict[str, Any]) -> Dict[str, Any]:
    priors = payload.get("historicalPlannerPriors")
    if not isinstance(priors, dict):
        return {}
    return {
        "sourceBatchCount": priors.get("sourceBatchCount") or 0,
        "publicationOutcomes": (priors.get("publicationOutcomes")[:6] if isinstance(priors.get("publicationOutcomes"), list) else []),
        "strongSkills": (priors.get("strongSkills")[:8] if isinstance(priors.get("strongSkills"), list) else []),
        "weakSkills": (priors.get("weakSkills")[:8] if isinstance(priors.get("weakSkills"), list) else []),
        "recurringRepairRoutes": (priors.get("recurringRepairRoutes")[:6] if isinstance(priors.get("recurringRepairRoutes"), list) else []),
        "riskyTransitions": (priors.get("riskyTransitions")[:8] if isinstance(priors.get("riskyTransitions"), list) else []),
        "antiPatterns": (priors.get("antiPatterns")[:10] if isinstance(priors.get("antiPatterns"), list) else []),
    }

def compact_historical_slot_priors(payload: Dict[str, Any]) -> Dict[str, Any]:
    priors = payload.get("historicalSlotPriors")
    if not isinstance(priors, dict):
        return {}
    return {
        "targetSkill": normalize_text(priors.get("targetSkill")),
        "difficultyTarget": priors.get("difficultyTarget"),
        "sourceItemCount": priors.get("sourceItemCount") or 0,
        "recommendedDifficultyBand": priors.get("recommendedDifficultyBand"),
        "strongExamples": (priors.get("strongExamples")[:5] if isinstance(priors.get("strongExamples"), list) else []),
        "weakExamples": (priors.get("weakExamples")[:5] if isinstance(priors.get("weakExamples"), list) else []),
        "routeHints": (priors.get("routeHints")[:5] if isinstance(priors.get("routeHints"), list) else []),
    }



def extract_historical_skill_biases(payload: Dict[str, Any]) -> Dict[str, List[str]]:
    priors = compact_historical_planner_priors(payload)
    strong = []
    weak = []
    for item in priors.get("strongSkills") or []:
        if isinstance(item, dict):
            value = normalize_text(item.get("targetSkill") or item.get("skill"))
            if value:
                strong.append(value)
    for item in priors.get("weakSkills") or []:
        if isinstance(item, dict):
            value = normalize_text(item.get("targetSkill") or item.get("skill"))
            if value:
                weak.append(value)
    return {"strong": strong[:10], "weak": weak[:10]}


def build_fallback_plan_tasks(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    count = max(1, int(payload.get("count") or 1))
    prompt = normalize_text(payload.get("prompt"))
    words = [x for x in re.split(r"[^\wа-яА-Я]+", prompt) if len(x) >= 4]
    unique_words = []
    seen = set()
    for word in words:
        low = word.lower()
        if low not in seen:
            seen.add(low)
            unique_words.append(word)
    biases = extract_historical_skill_biases(payload)
    strong = biases["strong"]
    weak = {x.lower() for x in biases["weak"]}
    seeds = [s for s in strong if s.lower() not in weak]
    seeds.extend([w for w in unique_words if w.lower() not in weak])
    if not seeds:
        seeds = [f"skill-{i+1}" for i in range(count)]
    tasks = []
    base_difficulty = max(1, min(5, int(payload.get("difficulty") or 2)))
    for i in range(count):
        seed = seeds[i % len(seeds)]
        difficulty = max(1, min(5, base_difficulty + (1 if i >= max(2, count // 2) else 0) + (1 if i >= max(4, count - 2) else 0)))
        anti = ["Избегай дословного дублирования referenceAssignments"]
        if biases["weak"]:
            anti.append("Не повторяй исторически слабые patterns из historicalPlannerPriors")
        tasks.append({
            "index": i + 1,
            "targetSkill": seed,
            "microGoal": f"Сделать отдельную задачу по поднавыку '{seed}' без смешивания нескольких учебных целей.",
            "difficultyTarget": difficulty,
            "whyItExists": "fallback planning with historical priors",
            "antiDuplicateHints": anti,
            "decisionLog": [{"stage": "batch_plan", "message": "Создан fallback slot с учётом historical planner priors"}],
        })
    return tasks


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
    if t == "assignment_course_profile_build":
        return fallback_course_profile(payload, job={})
    if t == "assignment_gap_analysis":
        return fallback_gap_analysis(payload, job={})
    if t in {"assignment_batch_plan", "assignment_batch_replan"}:
        count = max(1, int(payload.get("count") or 1))
        tasks = build_fallback_plan_tasks(payload)
        return {
            "canonicalRequest": {
                "assignmentType": payload.get("assignmentType"),
                "mode": payload.get("mode") or "topic-pack",
                "count": count,
                "normalizedPrompt": normalize_text(payload.get("prompt")),
            },
            "coverage": {"source": "fallback", "referenceCount": len(compact_reference_assignments(payload)), "historicalPriors": compact_historical_planner_priors(payload)},
            "summary": f"Построен fallback-{'replan' if t == 'assignment_batch_replan' else 'plan'} на {count} задач с учётом historical planner priors.",
            "decisionSummary": {"planningMode": "fallback", "count": count, "usedHistoricalPlannerPriors": bool(compact_historical_planner_priors(payload).get("sourceBatchCount"))},
            "plan": {"tasks": tasks}
        }
    if t == "assignment_reference_pack_build":
        brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
        priors = compact_historical_planner_priors(payload)
        slot_priors = compact_historical_slot_priors(payload)
        avoid = ["Дословное дублирование referenceAssignments", "Слишком широкая формулировка", "Однотипные тесты"]
        if priors.get("antiPatterns"):
            avoid.append("Не повторяй исторические anti-patterns из historicalPlannerPriors")
        if slot_priors.get("weakExamples"):
            avoid.append("Не повторяй слабые historical slot patterns для этого targetSkill")
        return {
            "stylePack": {"targetDescriptionStyle": "html-structured", "targetLength": 500, "notes": ["Держи полноценное описание с вводом/выводом", "Учитывай historical planner priors"]},
            "policyPack": {"allowedLanguages": payload.get("assignmentType"), "requiredCalls": [], "forbiddenCalls": []},
            "negativePack": {"avoid": avoid, "historicalAntiPatterns": priors.get("antiPatterns") or []},
            "exemplarPack": {"selectedReferences": compact_reference_assignments(payload)[:8]},
            "signals": {"targetSkill": brief.get("targetSkill") or brief.get("titleHint"), "difficultyTarget": brief.get("difficultyTarget") or payload.get("difficulty") or 2, "historicalSlotPriors": slot_priors},
            "generationHints": {"titleHint": brief.get("titleHint"), "generationPrompt": brief.get("generationPrompt") or normalize_text(payload.get("prompt")), "sourceText": brief.get("sourceText"), "notes": brief.get("notes"), "historicalPlannerPriors": priors, "historicalSlotPriors": slot_priors},
            "decisionLog": [{"stage": "reference_pack_build", "message": "Fallback reference pack собран с учётом historical planner priors."}],
        }
    if t == "assignment_batch_review":
        return {
            "status": "passed",
            "score": 0.72,
            "summary": "Fallback batch review built.",
            "checks": [{"name": "coherence", "status": "passed", "details": "fallback"}],
            "findings": [],
            "progression": {"status": "ok"},
            "coverage": {"status": "ok"},
            "decisionSummary": {"mode": "fallback"}
        }
    if t == "assignment_brief_generate":
        task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
        skill = task.get("TargetSkill") or task.get("targetSkill") or f"task-{task.get('Index') or task.get('index') or 1}"
        micro = task.get("MicroGoal") or task.get("microGoal") or "Сформулировать отдельную учебную цель"
        biases = extract_historical_skill_biases(payload)
        slot_priors = compact_historical_slot_priors(payload)
        weak_note = " Не повторяй исторически слабые patterns." if biases.get("weak") or slot_priors.get("weakExamples") else ""
        return {
            "titleHint": str(skill),
            "summary": str(micro),
            "generationPrompt": f"Сгенерируй одно качественное задание по теме: {skill}. Учебная цель: {micro}. Не смешивай несколько больших тем в одной задаче.{weak_note}",
            "sourceText": str(micro),
            "notes": "Используй стиль курса, historical planner priors, historical slot priors и не дублируй referenceAssignments.",
            "decisionLog": [{"stage": "brief_generate", "message": "Fallback brief создан с учётом historical planner priors."}],
            "difficultyTarget": int(task.get("difficultyTarget") or 2),
            "targetSkill": str(skill),
            "decisionLog": [{"stage": "brief_generate", "message": f"Brief создан для навыка {skill}"}],
        }
    if t == "assignment_brief_review":
        return run_brief_review(payload, job)
    if t == "assignment_brief_repair":
        brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
        review = payload.get("briefReview") if isinstance(payload.get("briefReview"), dict) else {}
        scorecard = payload.get("scorecard") if isinstance(payload.get("scorecard"), dict) else {}
        repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
        review_results = payload.get("reviewResults") if isinstance(payload.get("reviewResults"), list) else []
        target_skill = brief.get("targetSkill") or payload.get("targetSkill") or "task"
        summary = brief.get("summary") or brief.get("generationPrompt") or ""
        route = normalize_text(repair_plan.get("primaryRoute") or "brief") or "brief"
        route_notes = []
        if route == "brief":
            route_notes.append("Смести микроцель, чтобы задача не дублировала соседние drafts и referenceAssignments.")
        if route == "description":
            route_notes.append("Сузь brief до одной ясной учебной цели и более конкретного IO-контракта.")
        if route == "tests":
            route_notes.append("Предусмотри edge cases и более сильную тестовую идею уже на уровне brief.")
        findings_notes = []
        for review_item in review_results[:6]:
            if isinstance(review_item, dict):
                result = review_item.get("result") if isinstance(review_item.get("result"), dict) else {}
                for finding in (result.get("findings") if isinstance(result.get("findings"), list) else [])[:2]:
                    if isinstance(finding, dict):
                        findings_notes.append(normalize_text(finding.get("message")))
        summary_suffix = f" Routed by {route}." if route else ""
        return {
            "titleHint": brief.get("titleHint") or target_skill,
            "summary": summary or f"Уточнённый brief по теме {target_skill}",
            "generationPrompt": (brief.get("generationPrompt") or f"Сгенерируй одно качественное задание по теме {target_skill}.") + summary_suffix,
            "sourceText": brief.get("sourceText") or summary,
            "notes": " ".join(x for x in [(brief.get("notes") or "").strip(), "Исправлено по draft review.", *route_notes, *findings_notes[:4]] if x),
            "difficultyTarget": int(brief.get("difficultyTarget") or 2),
            "targetSkill": str(target_skill),
            "decisionLog": [{"stage": "brief_repair", "message": f"Brief repaired after review: {normalize_text(review.get('summary'))}", "route": route, "scoreBand": scorecard.get("band")}],
        }
    if t == "assignment_validate_draft":
        return (
            "Проверь существующий draft максимально строго."
            " Верни только JSON вида: "
            "{\"draftId\":\"...\",\"status\":\"passed|failed|needs-review\",\"summary\":\"...\",\"score\":0.0,\"checks\":[{\"name\":\"...\",\"status\":\"passed|failed|warning\",\"details\":\"...\"}]}"
        )
    if t == "assignment_analyze_existing":
        return "Проанализируй существующее задание и верни JSON: {\"assignmentId\":\"...\",\"kind\":\"quality-audit\",\"summary\":\"...\",\"suggestions\":[\"...\",\"...\"]}"
    if t == "assignment_repair":
        return (
            "Исправь существующий draft по findings и reviewResults. Верни только JSON вида {\"draft\": {...}, \"repairSummary\": \"...\"}. "
            "Не придумывай новую тему без причины: сохрани ядро задания, но исправь формулировку, тесты, solution и policy там, где review нашёл проблемы."
        )
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




def build_course_profile_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    return (
        "Ты — TaskForge AI course profiler. Верни только валидный JSON без markdown.\n\n"
        "Нужно построить профиль курса по существующим referenceAssignments.\n"
        "Формат JSON: {\"courseProfile\":{\"dominantSkills\":[...],\"difficultyDistribution\":{...},\"styleProfile\":{...},\"policyProfile\":{...},\"testProfile\":{...},\"assignmentOntology\":{...},\"exemplarSignals\":{...},\"negativePatterns\":[...]},\"summary\":\"...\",\"decisionSummary\":{...}}.\n\n"
        f"Payload:\n{json.dumps(compact_payload, ensure_ascii=False, indent=2)}"
    )


def build_gap_analysis_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    return (
        "Ты — TaskForge AI gap analyst. Верни только валидный JSON без markdown.\n\n"
        "Нужно проанализировать пробелы курса и вернуть, чего не хватает относительно запроса.\n"
        "Формат JSON: {\"gapAnalysis\":{\"coveredTopics\":[...],\"missingTopics\":[...],\"weakCoverageTopics\":[...],\"duplicateClusters\":[...],\"recommendedFocus\":[...],\"curriculumRisks\":[...]} ,\"coverage\":{...},\"summary\":\"...\",\"decisionSummary\":{...}}.\n\n"
        f"Payload:\n{json.dumps(compact_payload, ensure_ascii=False, indent=2)}"
    )


def fallback_course_profile(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    refs = compact_reference_assignments(payload)
    skills = []
    langs = {}
    types = {}
    for r in refs:
        title = normalize_text(r.get("title"))
        if title:
            skills.append(title[:80])
        for lang in (r.get("allowedLanguagesCsv") or "").split(','):
            lang = normalize_text(lang)
            if lang:
                langs[lang] = langs.get(lang, 0) + 1
        t = normalize_text(r.get("type"))
        if t:
            types[t] = types.get(t, 0) + 1
    avg_hidden = round(sum(int(r.get("hiddenTestsCount") or 0) for r in refs) / len(refs), 2) if refs else 0
    return {
        "courseProfile": {
            "dominantSkills": skills[:8],
            "difficultyDistribution": {"approximate": True},
            "styleProfile": {"descriptionLength": "mixed", "htmlPreferred": True, "tone": "teaching"},
            "policyProfile": {"allowedLanguages": langs, "assignmentTypes": types},
            "testProfile": {"referenceCount": len(refs), "publicExamplesPerTask": "1-2", "hiddenTests": "usually-present", "hiddenTestsAvg": avg_hidden},
            "assignmentOntology": {"coreSkillSeeds": skills[:12], "assignmentTypes": list(types.keys())},
            "exemplarSignals": {"goodPatterns": ["html-description", "examples", "hidden-tests"], "referenceCount": len(refs)},
            "negativePatterns": ["слишком широкая задача", "дословный дубль title", "слабые edge cases"],
        },
        "summary": f"Построен fallback course profile по {len(refs)} referenceAssignments.",
        "decisionSummary": {"profileSource": "fallback", "referenceCount": len(refs)}
    }


def fallback_gap_analysis(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    prompt = normalize_text(payload.get("prompt"))
    refs = compact_reference_assignments(payload)
    covered = [normalize_text(r.get("title")) for r in refs if normalize_text(r.get("title"))][:8]
    tokens = [x for x in re.split(r"[^\wа-яА-Я]+", prompt) if len(x) > 3]
    missing = []
    for t in tokens:
        low = t.lower()
        if not any(low in c.lower() for c in covered):
            missing.append(t)
    seen = set()
    missing_unique = []
    for x in missing:
        lx = x.lower()
        if lx not in seen:
            seen.add(lx)
            missing_unique.append(x)
    return {
        "gapAnalysis": {
            "coveredTopics": covered,
            "missingTopics": missing_unique[:10],
            "weakCoverageTopics": missing_unique[:5],
            "duplicateClusters": [],
            "recommendedFocus": missing_unique[:6] or ["Сделать прогрессивный набор без дублей"],
            "curriculumRisks": ["слишком широкий prompt", "нужна прогрессия сложности"] if len(tokens) > 5 else [],
        },
        "coverage": {"referenceCount": len(refs), "promptTokenCount": len(tokens)},
        "summary": f"Сделан fallback gap analysis по {len(refs)} referenceAssignments.",
        "decisionSummary": {"gapSource": "fallback", "missingTopicsCount": len(missing_unique)}
    }


def build_batch_plan_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    request_kind = "replan" if (job.get("type") or "").lower().strip() == "assignment_batch_replan" else "plan"
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    compact_payload["historicalPlannerPriors"] = compact_historical_planner_priors(payload)
    compact_payload["historicalSlotPriors"] = compact_historical_slot_priors(payload)
    return (
        "Ты — TaskForge AI planner. Верни только валидный JSON без markdown.\n\n"
        f"Сейчас режим: {request_kind}. Нужно построить план набора задач, а не сами задачи.\n"
        "Используй courseProfile, gapAnalysis и historicalPlannerPriors, если они есть.\n"
        "Historical planner priors — это память о сильных/слабых skill patterns, risky transitions, anti-patterns и repair routes из прошлых batch waves того же курса.\n"
        "Сначала нормализуй запрос, потом верни coverage/gaps и план пакета.\n"
        "Формат JSON: {\"canonicalRequest\":{...},\"coverage\":{...},\"summary\":\"...\",\"decisionSummary\":{...},\"plan\":{\"tasks\":[{\"index\":1,\"targetSkill\":\"...\",\"microGoal\":\"...\",\"difficultyTarget\":2,\"whyItExists\":\"...\",\"antiDuplicateHints\":[\"...\"],\"decisionLog\":[{\"stage\":\"batch_plan\",\"message\":\"...\"}]}]}}\n"
        "Не делай пустой план. Количество tasks должно соответствовать count.\n"
        "Избегай исторически слабых targetSkill patterns и risky transitions, если их можно обойти.\n\n"
        f"Batch payload:\n{json.dumps(compact_payload, ensure_ascii=False, indent=2)}"
    )


def build_brief_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    compact_payload["historicalPlannerPriors"] = compact_historical_planner_priors(payload)
    compact_payload["historicalSlotPriors"] = compact_historical_slot_priors(payload)
    return (
        "Ты — TaskForge AI brief writer. Верни только валидный JSON без markdown.\n\n"
        "Нужно написать идеальный brief для одной будущей задачи, а не сам draft задания.\n"
        "Используй historicalPlannerPriors, historicalSlotPriors, institutionalMemory и antiPatternMemory как ограничения: усиливай удачные patterns и не повторяй исторически слабые.\n"
        "Формат JSON: {\"titleHint\":\"...\",\"summary\":\"...\",\"generationPrompt\":\"...\",\"sourceText\":\"...\",\"notes\":\"...\",\"difficultyTarget\":2,\"targetSkill\":\"...\",\"decisionLog\":[{\"stage\":\"brief_generate\",\"message\":\"...\"}]}.\n"
        "generationPrompt должен быть понятным, узким и не смешивать много учебных целей.\n\n"
        f"Brief payload:\n{json.dumps(compact_payload, ensure_ascii=False, indent=2)}"
    )


def build_reference_pack_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    compact_payload["historicalPlannerPriors"] = compact_historical_planner_priors(payload)
    compact_payload["historicalSlotPriors"] = compact_historical_slot_priors(payload)
    return (
        "Ты — TaskForge AI reference pack builder. Верни только валидный JSON без markdown.\n\n"
        "Нужно собрать compact reference pack для одной будущей задачи: stylePack, policyPack, negativePack, exemplarPack, signals, generationHints.\n"
        "Используй brief, briefReview, courseProfile, antiPatternMemory, historicalPlannerPriors и historicalSlotPriors.\n"
        "Negative pack должен явно перечислять, чего НЕ надо повторять из слабых исторических паттернов.\n"
        "Формат JSON: {\"stylePack\":{...},\"policyPack\":{...},\"negativePack\":{...},\"exemplarPack\":{...},\"signals\":{...},\"generationHints\":{...},\"decisionLog\":[{\"stage\":\"reference_pack_build\",\"message\":\"...\"}]}.\n\n"
        f"Reference pack payload:\n{json.dumps(compact_payload, ensure_ascii=False, indent=2)}"
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


def build_findings_from_checks(checks: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    findings: List[Dict[str, Any]] = []
    for check in checks:
        status = str(check.get("status") or "").lower()
        if status == "passed":
            continue
        severity = "high" if status == "failed" else "medium"
        findings.append({
            "severity": severity,
            "code": str(check.get("name") or "check").replace(" ", "_"),
            "message": str(check.get("details") or "review finding"),
            "repairHint": "Исправь указанную проблему и перепроверь draft.",
        })
    return findings


def run_structural_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    validation = run_self_check(draft)
    validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
    validation["findings"] = build_findings_from_checks(validation.get("checks") or [])
    return validation


def fallback_pedagogy_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    description = normalize_text(draft.get("description"))
    checks = []
    if len(description) >= 160:
        checks.append({"name": "pedagogy-length", "status": "passed", "details": f"description.len={len(description)}"})
    else:
        checks.append({"name": "pedagogy-length", "status": "failed", "details": "Слишком короткое объяснение для учебной задачи"})
    if any(word in description.lower() for word in ["встав", "удал", "поиск"]) and sum(1 for w in ["встав", "удал", "поиск"] if w in description.lower()) > 1:
        checks.append({"name": "single-learning-goal", "status": "warning", "details": "Похоже, в задаче объединено несколько учебных целей"})
    else:
        checks.append({"name": "single-learning-goal", "status": "passed", "details": "Учебная цель выглядит достаточно узкой"})
    status = summarize_status(checks)
    return {
        "draftId": payload.get("draftId") or job.get("targetEntityId"),
        "status": status,
        "summary": "Fallback pedagogy review completed.",
        "score": sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks)),
        "checks": checks,
        "findings": build_findings_from_checks(checks),
    }


def run_style_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    description = normalize_text(draft.get("description"))
    refs = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    lengths = [len(normalize_text(r.get("descriptionSummary") or r.get("description"))) for r in refs if isinstance(r, dict) and normalize_text(r.get("descriptionSummary") or r.get("description"))]
    avg_len = int(sum(lengths) / len(lengths)) if lengths else 0
    checks = []
    findings = []
    score = 1.0
    if has_html_markup(description):
        checks.append({"name": "style-html", "status": "passed", "details": "description использует html-структуру"})
    else:
        checks.append({"name": "style-html", "status": "warning", "details": "description без html-структуры"})
        findings.append({"severity": "warning", "code": "style-html", "message": "Описание без привычной html-разметки курса.", "suggestedRepair": "Добавь <p>, <ul>, <li> и структурируй секции.", "confidence": 0.72})
        score -= 0.15
    if avg_len > 0:
        delta = abs(len(description) - avg_len)
        if delta <= max(120, avg_len // 2):
            checks.append({"name": "style-length-fit", "status": "passed", "details": f"description.len={len(description)}, course.avg={avg_len}"})
        else:
            checks.append({"name": "style-length-fit", "status": "warning", "details": f"description.len={len(description)}, course.avg={avg_len}"})
            findings.append({"severity": "warning", "code": "style-length-fit", "message": "Длина описания выбивается из привычного профиля курса.", "suggestedRepair": "Подровняй глубину и детализацию под средний стиль курса.", "confidence": 0.68})
            score -= 0.12
    title = normalize_text(draft.get("title"))
    ref_titles = [normalize_text(r.get("title")) for r in refs if isinstance(r, dict) and normalize_text(r.get("title"))]
    if title and ref_titles and any(title.lower() == x.lower() for x in ref_titles):
        checks.append({"name": "style-title-originality", "status": "failed", "details": "title совпадает с reference title"})
        findings.append({"severity": "high", "code": "style-title-originality", "message": "Заголовок дословно совпадает с одним из referenceAssignments.", "suggestedRepair": "Перефразируй title и сдвинь акцент формулировки.", "confidence": 0.93})
        score -= 0.35
    else:
        checks.append({"name": "style-title-originality", "status": "passed", "details": "title не совпадает дословно"})
    status = summarize_status(checks)
    return {"draftId": payload.get("draftId") or job.get("targetEntityId"), "status": status, "score": max(0.0, round(score, 2)), "summary": "Проверена стилистическая совместимость draft с course profile и referenceAssignments.", "checks": checks, "findings": findings, "topReferences": ref_titles[:5]}


def tokenize_similarity_text(value: Any) -> List[str]:
    text = normalize_text(value).lower()
    cleaned = []
    for ch in text:
        cleaned.append(ch if ch.isalnum() or ch.isspace() else ' ')
    return [x for x in ''.join(cleaned).split() if x]


def compute_text_similarity(a: Any, b: Any) -> float:
    a_text = normalize_text(a)
    b_text = normalize_text(b)
    if not a_text or not b_text:
        return 0.0
    seq = difflib.SequenceMatcher(None, a_text.lower(), b_text.lower()).ratio()
    a_tokens = set(tokenize_similarity_text(a_text))
    b_tokens = set(tokenize_similarity_text(b_text))
    jaccard = (len(a_tokens & b_tokens) / len(a_tokens | b_tokens)) if (a_tokens or b_tokens) else 0.0
    return max(seq, jaccard)


def run_similarity_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    references = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    title = normalize_text(draft.get("title"))
    description = normalize_text(draft.get("description"))
    checks = []
    scored = []
    for ref in references[:20]:
        if not isinstance(ref, dict):
            continue
        ref_title = normalize_text(ref.get("title"))
        ref_desc = normalize_text(ref.get("descriptionSummary") or ref.get("description"))
        title_sim = compute_text_similarity(title, ref_title)
        desc_sim = compute_text_similarity(description, ref_desc)
        combined = max(title_sim, desc_sim)
        scored.append({
            "referenceId": ref.get("id"),
            "title": ref_title,
            "titleSimilarity": round(title_sim, 4),
            "descriptionSimilarity": round(desc_sim, 4),
            "combinedSimilarity": round(combined, 4),
        })
    scored.sort(key=lambda x: x["combinedSimilarity"], reverse=True)
    top = scored[:3]
    max_sim = top[0]["combinedSimilarity"] if top else 0.0
    if max_sim >= 0.9:
        checks.append({"name": "similarity-max", "status": "failed", "details": f"Слишком высокая похожесть на существующее задание: {max_sim:.2f}"})
    elif max_sim >= 0.75:
        checks.append({"name": "similarity-max", "status": "warning", "details": f"Похожесть на существующее задание выглядит высокой: {max_sim:.2f}"})
    else:
        checks.append({"name": "similarity-max", "status": "passed", "details": f"Максимальная похожесть приемлемая: {max_sim:.2f}"})
    if len(top) >= 2 and top[0]["combinedSimilarity"] >= 0.75 and top[1]["combinedSimilarity"] >= 0.75:
        checks.append({"name": "similarity-cluster", "status": "warning", "details": "Draft похож сразу на несколько referenceAssignments"})
    else:
        checks.append({"name": "similarity-cluster", "status": "passed", "details": "Явного кластера дублей не найдено"})
    status = summarize_status(checks)
    findings = build_findings_from_checks(checks)
    if top and max_sim >= 0.75:
        findings.append({
            "severity": "medium" if max_sim < 0.9 else "high",
            "code": "similarity-top-reference",
            "message": f"Наиболее похожий reference: {top[0].get('title') or top[0].get('referenceId')}",
            "repairHint": "Измени учебную цель, формулировку и тесты, чтобы задача меньше дублировала существующие задания.",
        })
    return {
        "draftId": payload.get("draftId") or job.get("targetEntityId"),
        "status": status,
        "summary": "Similarity review completed.",
        "score": sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks)),
        "checks": checks,
        "findings": findings,
        "topReferences": top,
    }


def run_runtime_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    validation = run_self_check(draft)
    checks = list(validation.get("checks") or [])
    assignment_type = str(draft.get("assignmentType") or "").strip().lower()

    if assignment_type == "code-test":
        runtime_failures = sum(1 for x in checks if str(x.get("name") or "").startswith("test-") and x.get("status") == "failed")
        if runtime_failures == 0:
            checks.append({"name": "runtime-suite", "status": "passed", "details": "referenceSolutionPython проходит все тесты"})
        else:
            checks.append({"name": "runtime-suite", "status": "failed", "details": f"referenceSolutionPython провалил {runtime_failures} тест(ов)"})
    elif assignment_type == "math":
        answer_blocks = sum(1 for x in checks if str(x.get("name") or "").startswith("Блок "))
        checks.append({"name": "runtime-math-coverage", "status": "passed" if answer_blocks > 0 else "failed", "details": f"Проверено answer-blocks={answer_blocks}"})
    elif assignment_type == "test":
        question_checks = sum(1 for x in checks if str(x.get("name") or "").startswith("Вопрос "))
        checks.append({"name": "runtime-test-coverage", "status": "passed" if question_checks > 0 else "failed", "details": f"Проверено questions={question_checks}"})

    status = summarize_status(checks)
    score = sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks))
    return {
        "draftId": payload.get("draftId") or job.get("targetEntityId"),
        "status": status,
        "summary": f"Runtime review completed for {assignment_type or 'unknown'} draft.",
        "score": score,
        "checks": checks,
        "findings": build_findings_from_checks(checks),
    }




def run_code_test_suite(source_code: str, tests: List[Dict[str, Any]]) -> Dict[str, Any]:
    results = []
    passed = 0
    for idx, test in enumerate(tests, start=1):
        if not isinstance(test, dict):
            continue
        stdin_text = str(test.get("input") or "")
        expected = normalize_text(test.get("expectedOutput"))
        try:
            runtime = run_python_solution(source_code, stdin_text)
            actual = normalize_text(runtime.get("stdout"))
            ok = runtime.get("returncode") == 0 and actual == expected
            if ok:
                passed += 1
            results.append({
                "index": idx,
                "passed": ok,
                "returncode": runtime.get("returncode"),
                "actual": actual,
                "expected": expected,
                "stderr": truncate_text(runtime.get("stderr"), 180),
            })
        except Exception as ex:
            results.append({"index": idx, "passed": False, "error": str(ex)})
    return {"passed": passed, "total": len(results), "results": results}


def build_python_mutants(source_code: str) -> List[Dict[str, Any]]:
    patterns = [
        ("boundary", "off_by_one_plus", "+ 1", "+ 0", "Потенциальный off-by-one в +1"),
        ("boundary", "off_by_one_minus", "- 1", "- 0", "Потенциальный off-by-one в -1"),
        ("comparison", "strict_gt", ">=", ">", "Ослабление граничного условия >= -> >"),
        ("comparison", "strict_lt", "<=", "<", "Ослабление граничного условия <= -> <"),
        ("comparison", "equality_flip", "==", "!=", "Инверсия equality-проверки"),
        ("whitespace", "drop_strip", ".strip()", "", "Убрать strip() и проверить пробелы/пустой ввод"),
        ("whitespace", "drop_split_strip", "splitlines()", "split('\\n')", "Ослабить нормализацию переноса строк"),
        ("aggregation", "reverse_sort", "sorted(", "list(", "Сломать сортировку/нормализацию порядка"),
    ]
    mutants = []
    seen = set()
    for family, name, needle, repl, summary in patterns:
        if needle not in source_code:
            continue
        mutated = source_code.replace(needle, repl, 1)
        if mutated == source_code or mutated in seen:
            continue
        try:
            compile(mutated, "<mutant>", "exec")
        except Exception:
            continue
        seen.add(mutated)
        mutants.append({"family": family, "name": name, "code": mutated, "summary": summary})
    if "replace(" in source_code:
        mutants.append({"family": "string-processing", "name": "first_occurrence_bias", "code": source_code, "summary": "Нужны тесты, отличающие замену первого и всех вхождений."})
    return mutants[:10]


def assess_mutation_strength(draft: Dict[str, Any]) -> Dict[str, Any]:
    if str(draft.get("assignmentType") or "").strip().lower() != "code-test":
        return {"generated": 0, "killed": 0, "survived": 0, "skipped": 0, "killRatio": 1.0, "survivors": [], "mutants": []}
    tests = [t for t in list(draft.get("publicTests") or []) + list(draft.get("hiddenTests") or []) if isinstance(t, dict)]
    source_code = str(draft.get("referenceSolutionPython") or "")
    mutants = build_python_mutants(source_code)
    if not source_code or not tests or not mutants:
        return {"generated": len(mutants), "killed": 0, "survived": 0, "skipped": 0, "killRatio": 1.0 if not mutants else 0.0, "survivors": [], "mutants": mutants}
    survivors = []
    killed = 0
    skipped = 0
    for mutant in mutants:
        code = mutant.get("code")
        if not isinstance(code, str) or code == source_code:
            skipped += 1
            continue
        suite = run_code_test_suite(code, tests)
        if suite.get("total") == 0:
            skipped += 1
            continue
        if suite.get("passed") == suite.get("total"):
            survivors.append({"name": mutant.get("name"), "summary": mutant.get("summary")})
        else:
            killed += 1
    executed = max(1, len(mutants) - skipped)
    return {
        "generated": len(mutants),
        "killed": killed,
        "survived": len(survivors),
        "skipped": skipped,
        "killRatio": round(killed / executed, 3),
        "survivors": survivors[:5],
        "families": sorted({str(m.get("family") or "general") for m in mutants}),
        "mutants": [{"family": m.get("family"), "name": m.get("name"), "summary": m.get("summary")} for m in mutants],
    }


def run_test_strength_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    assignment_type = str(draft.get("assignmentType") or "").strip().lower()
    checks = []
    findings = []
    mutation_analysis = None
    if assignment_type == "code-test":
        public_tests = list(draft.get("publicTests") or [])
        hidden_tests = list(draft.get("hiddenTests") or [])
        all_tests = public_tests + hidden_tests
        sigs = set()
        for t in all_tests:
            if isinstance(t, dict):
                sigs.add((normalize_text(t.get("input")), normalize_text(t.get("expectedOutput"))))
        if len(sigs) < len(all_tests):
            checks.append({"name": "duplicate-tests", "status": "warning", "details": "Есть повторяющиеся тесты"})
        else:
            checks.append({"name": "duplicate-tests", "status": "passed", "details": "Повторяющихся тестов не найдено"})
        edge_hits = 0
        for t in all_tests:
            text_case = (normalize_text(t.get("input")) + " " + normalize_text(t.get("expectedOutput"))) if isinstance(t, dict) else ""
            if any(token in text_case for token in ["0", "-1", "  "]) or text_case.strip() == "":
                edge_hits += 1
        if len(hidden_tests) >= MIN_HIDDEN_TESTS:
            checks.append({"name": "hidden-tests-count", "status": "passed", "details": f"hidden={len(hidden_tests)}"})
        else:
            checks.append({"name": "hidden-tests-count", "status": "failed", "details": f"hidden={len(hidden_tests)}"})
        if edge_hits > 0:
            checks.append({"name": "edge-case-presence", "status": "passed", "details": f"Найдено edge-like тестов: {edge_hits}"})
        else:
            checks.append({"name": "edge-case-presence", "status": "warning", "details": "Не видно явных edge cases"})
        if len(all_tests) >= max(MIN_PUBLIC_TESTS + MIN_HIDDEN_TESTS, 7):
            checks.append({"name": "test-volume", "status": "passed", "details": f"tests={len(all_tests)}"})
        else:
            checks.append({"name": "test-volume", "status": "warning", "details": f"Малый объём тестов: {len(all_tests)}"})

        mutation_analysis = assess_mutation_strength(draft)
        if mutation_analysis.get("generated", 0) == 0:
            checks.append({"name": "mutation-groundwork", "status": "warning", "details": "Не удалось построить прокси-мутанты по referenceSolutionPython"})
        else:
            kill_ratio = float(mutation_analysis.get("killRatio") or 0.0)
            if kill_ratio >= 0.7:
                checks.append({"name": "mutation-kill-ratio", "status": "passed", "details": f"killRatio={kill_ratio:.2f}"})
            elif kill_ratio >= 0.4:
                checks.append({"name": "mutation-kill-ratio", "status": "warning", "details": f"killRatio={kill_ratio:.2f}"})
            else:
                checks.append({"name": "mutation-kill-ratio", "status": "failed", "details": f"killRatio={kill_ratio:.2f}"})
            families = list(mutation_analysis.get("families") or [])
            if families:
                checks.append({"name": "mutation-families", "status": "passed" if len(families) >= 3 else "warning", "details": ",".join(families)})
            if mutation_analysis.get("survived", 0) > 0:
                findings.append({
                    "severity": "high" if kill_ratio < 0.4 else "medium",
                    "code": "tests.mutation_survivors",
                    "message": f"Часть прокси-мутантов переживает test suite: {mutation_analysis.get('survived')}",
                    "suggestedRepair": "Усиль hidden tests и добавь boundary/edge cases, убивающие surviving mutants.",
                    "confidence": round(min(0.98, 0.45 + mutation_analysis.get("survived", 0) * 0.1), 3),
                })
    elif assignment_type == "test":
        questions = list(draft.get("questions") or [])
        checks.append({"name": "questions-count", "status": "passed" if len(questions) >= 5 else "failed", "details": f"questions={len(questions)}"})
    elif assignment_type == "math":
        blocks = list(draft.get("blocks") or [])
        answer_blocks = sum(1 for b in blocks if isinstance(b, dict) and str(b.get("blockType") or "").lower() not in ["info", "text"])
        checks.append({"name": "answer-blocks", "status": "passed" if answer_blocks > 0 else "failed", "details": f"answerBlocks={answer_blocks}"})
    else:
        checks.append({"name": "unsupported-type", "status": "warning", "details": assignment_type or "unknown"})
    status = summarize_status(checks)
    findings.extend(build_findings_from_checks(checks))
    return {
        "draftId": payload.get("draftId") or job.get("targetEntityId"),
        "status": status,
        "summary": "Test strength review completed.",
        "score": sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks)),
        "checks": checks,
        "findings": findings,
        "mutationAnalysis": mutation_analysis,
    }

def fallback_repair_result(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    reviews = payload.get("reviewResults") if isinstance(payload.get("reviewResults"), list) else []
    repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
    scorecard = payload.get("scorecard") if isinstance(payload.get("scorecard"), dict) else {}
    routes = [normalize_text(x) for x in (repair_plan.get("routes") if isinstance(repair_plan.get("routes"), list) else []) if normalize_text(x)]
    primary_route = normalize_text(repair_plan.get("primaryRoute") or "general") or "general"
    if primary_route not in routes:
        routes.append(primary_route)
    repaired = json.loads(json.dumps(draft, ensure_ascii=False)) if draft else {}
    title = normalize_text(repaired.get("title"))
    if title and title.lower().startswith("ai fallback"):
        repaired["title"] = title.replace("AI fallback:", "").strip() or title

    description = str(repaired.get("description") or "")
    if description and not has_html_markup(description):
        repaired["description"] = f"<p>{description}</p>"
    if len(normalize_text(repaired.get("description"))) < MIN_DESCRIPTION_LEN or "description" in routes:
        repaired["description"] = (
            "<p>Исправленная AI-версией формулировка задания.</p>"
            "<p><strong>Входные данные:</strong> явно укажи формат ввода, включая пробелы и крайние случаи.</p>"
            "<p><strong>Выходные данные:</strong> выведите точный результат решения задачи.</p>"
            "<p><strong>Примечание:</strong> формулировка была автоматически расширена после review и scorecard aggregation.</p>"
        )

    if repaired.get("assignmentType") == "code-test":
        public_tests = list(repaired.get("publicTests") or [])
        hidden_tests = list(repaired.get("hiddenTests") or [])
        while len(public_tests) < MIN_PUBLIC_TESTS and hidden_tests:
            public_tests.append(hidden_tests.pop(0))
        while len(public_tests) < MIN_PUBLIC_TESTS:
            public_tests.append({"input": "1\n", "expectedOutput": "1"})
        if "tests" in routes or len(hidden_tests) < MIN_HIDDEN_TESTS:
            candidate_hidden = [
                {"input": "0\n", "expectedOutput": "0"},
                {"input": "1\n", "expectedOutput": normalize_text(public_tests[0].get("expectedOutput")) or "1"},
                {"input": "2\n", "expectedOutput": "2"},
                {"input": "10\n", "expectedOutput": "10"},
                {"input": "100\n", "expectedOutput": "100"},
            ]
            for test in candidate_hidden:
                if len(hidden_tests) >= MIN_HIDDEN_TESTS:
                    break
                hidden_tests.append(test)
        repaired["publicTests"] = public_tests
        repaired["hiddenTests"] = hidden_tests
        code = str(repaired.get("referenceSolutionPython") or "")
        if code and "if __name__ == '__main__':" not in code:
            code = code.rstrip() + "\n\nif __name__ == '__main__':\n    import sys\n    print(solve(sys.stdin.read()))\n"
        if code and "def solve" not in code:
            code = "def solve(data: str) -> str:\n    return str(data.strip())\n\n" + code
        repaired["referenceSolutionPython"] = code or "import sys\n\ndef solve(data: str) -> str:\n    return data.strip()\n\nif __name__ == '__main__':\n    print(solve(sys.stdin.read()))"
        if "policy" in routes:
            repaired["requiredCalls"] = list(repaired.get("requiredCalls") or ["solve"])
            repaired["forbiddenCalls"] = list(repaired.get("forbiddenCalls") or ["Process.Start", "__import__"])
    if "brief" in routes:
        repaired["title"] = normalize_text(repaired.get("title") or "Новая вариация задачи") + " — revised"

    findings_digest = []
    for review in reviews[:6]:
        if not isinstance(review, dict):
            continue
        result = review.get("result") if isinstance(review.get("result"), dict) else {}
        for finding in (result.get("findings") if isinstance(result.get("findings"), list) else [])[:2]:
            if isinstance(finding, dict):
                findings_digest.append(normalize_text(finding.get("message")))

    meta = repaired.get("meta") if isinstance(repaired.get("meta"), dict) else {}
    meta["repairSummary"] = {
        "source": "assignment_repair",
        "repairRoute": primary_route,
        "repairRoutes": routes,
        "publishRecommendation": repair_plan.get("publishRecommendation"),
        "scoreBand": scorecard.get("band"),
        "overallScore": scorecard.get("overallScore"),
        "repairedAt": int(time.time()),
        "reviewHints": findings_digest[:6],
    }
    repaired["meta"] = meta
    validation = run_self_check(repaired)
    return {
        "draft": repaired,
        "repairSummary": f"Fallback repair applied via route {primary_route}.",
        "draftValidation": validation,
    }

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
    if t == "assignment_course_profile_build":
        return fallback_course_profile(payload, job={})
    if t == "assignment_gap_analysis":
        return fallback_gap_analysis(payload, job={})
    if t in {"assignment_batch_plan", "assignment_batch_replan"}:
        count = max(1, int(payload.get("count") or 1))
        tasks = build_fallback_plan_tasks(payload)
        priors = compact_historical_planner_priors(payload)
        return {
            "canonicalRequest": {
                "assignmentType": payload.get("assignmentType"),
                "mode": payload.get("mode") or "topic-pack",
                "count": count,
                "normalizedPrompt": normalize_text(payload.get("prompt")),
            },
            "coverage": {"source": "fallback", "referenceCount": len(compact_reference_assignments(payload)), "historicalPriors": priors},
            "summary": f"Построен fallback-{'replan' if t == 'assignment_batch_replan' else 'plan'} на {count} задач с учётом historical planner priors.",
            "decisionSummary": {"planningMode": "fallback", "count": count, "usedHistoricalPlannerPriors": bool(priors.get("sourceBatchCount"))},
            "plan": {"tasks": tasks}
        }
    if t == "assignment_reference_pack_build":
        brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
        priors = compact_historical_planner_priors(payload)
        slot_priors = compact_historical_slot_priors(payload)
        avoid = ["Дословное дублирование referenceAssignments", "Слишком широкая формулировка", "Однотипные тесты"]
        if priors.get("antiPatterns"):
            avoid.append("Не повторяй исторические anti-patterns из historicalPlannerPriors")
        if slot_priors.get("weakExamples"):
            avoid.append("Не повторяй слабые historical slot patterns для этого targetSkill")
        return {
            "stylePack": {"targetDescriptionStyle": "html-structured", "targetLength": 500, "notes": ["Держи полноценное описание с вводом/выводом", "Учитывай historical planner priors"]},
            "policyPack": {"allowedLanguages": payload.get("assignmentType"), "requiredCalls": [], "forbiddenCalls": []},
            "negativePack": {"avoid": avoid, "historicalAntiPatterns": priors.get("antiPatterns") or []},
            "exemplarPack": {"selectedReferences": compact_reference_assignments(payload)[:8]},
            "signals": {"targetSkill": brief.get("targetSkill") or brief.get("titleHint"), "difficultyTarget": brief.get("difficultyTarget") or payload.get("difficulty") or 2, "historicalSlotPriors": slot_priors},
            "generationHints": {"titleHint": brief.get("titleHint"), "generationPrompt": brief.get("generationPrompt") or normalize_text(payload.get("prompt")), "sourceText": brief.get("sourceText"), "notes": brief.get("notes"), "historicalPlannerPriors": priors, "historicalSlotPriors": slot_priors},
            "decisionLog": [{"stage": "reference_pack_build", "message": "Fallback reference pack собран с учётом historical planner priors."}],
        }
    if t == "assignment_batch_review":
        return {
            "status": "passed",
            "score": 0.72,
            "summary": "Fallback batch review built.",
            "checks": [{"name": "coherence", "status": "passed", "details": "fallback"}],
            "findings": [],
            "progression": {"status": "ok"},
            "coverage": {"status": "ok"},
            "decisionSummary": {"mode": "fallback"}
        }
    if t == "assignment_brief_generate":
        task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
        skill = task.get("TargetSkill") or task.get("targetSkill") or f"task-{task.get('Index') or task.get('index') or 1}"
        micro = task.get("MicroGoal") or task.get("microGoal") or "Сформулировать отдельную учебную цель"
        biases = extract_historical_skill_biases(payload)
        slot_priors = compact_historical_slot_priors(payload)
        weak_note = " Не повторяй исторически слабые patterns." if biases.get("weak") or slot_priors.get("weakExamples") else ""
        return {
            "titleHint": str(skill),
            "summary": str(micro),
            "generationPrompt": f"Сгенерируй одно качественное задание по теме: {skill}. Учебная цель: {micro}. Не смешивай несколько больших тем в одной задаче.{weak_note}",
            "sourceText": str(micro),
            "notes": "Используй стиль курса, historical planner priors, historical slot priors и не дублируй referenceAssignments.",
            "decisionLog": [{"stage": "brief_generate", "message": "Fallback brief создан с учётом historical planner priors."}],
            "difficultyTarget": int(task.get("difficultyTarget") or 2),
            "targetSkill": str(skill),
            "decisionLog": [{"stage": "brief_generate", "message": f"Brief создан для навыка {skill}"}],
        }
    if t == "assignment_brief_review":
        return run_brief_review(payload, job)
    if t == "assignment_brief_repair":
        brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
        review = payload.get("briefReview") if isinstance(payload.get("briefReview"), dict) else {}
        scorecard = payload.get("scorecard") if isinstance(payload.get("scorecard"), dict) else {}
        repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
        review_results = payload.get("reviewResults") if isinstance(payload.get("reviewResults"), list) else []
        target_skill = brief.get("targetSkill") or payload.get("targetSkill") or "task"
        summary = brief.get("summary") or brief.get("generationPrompt") or ""
        route = normalize_text(repair_plan.get("primaryRoute") or "brief") or "brief"
        route_notes = []
        if route == "brief":
            route_notes.append("Смести микроцель, чтобы задача не дублировала соседние drafts и referenceAssignments.")
        if route == "description":
            route_notes.append("Сузь brief до одной ясной учебной цели и более конкретного IO-контракта.")
        if route == "tests":
            route_notes.append("Предусмотри edge cases и более сильную тестовую идею уже на уровне brief.")
        findings_notes = []
        for review_item in review_results[:6]:
            if isinstance(review_item, dict):
                result = review_item.get("result") if isinstance(review_item.get("result"), dict) else {}
                for finding in (result.get("findings") if isinstance(result.get("findings"), list) else [])[:2]:
                    if isinstance(finding, dict):
                        findings_notes.append(normalize_text(finding.get("message")))
        summary_suffix = f" Routed by {route}." if route else ""
        return {
            "titleHint": brief.get("titleHint") or target_skill,
            "summary": summary or f"Уточнённый brief по теме {target_skill}",
            "generationPrompt": (brief.get("generationPrompt") or f"Сгенерируй одно качественное задание по теме {target_skill}.") + summary_suffix,
            "sourceText": brief.get("sourceText") or summary,
            "notes": " ".join(x for x in [(brief.get("notes") or "").strip(), "Исправлено по draft review.", *route_notes, *findings_notes[:4]] if x),
            "difficultyTarget": int(brief.get("difficultyTarget") or 2),
            "targetSkill": str(target_skill),
            "decisionLog": [{"stage": "brief_repair", "message": f"Brief repaired after review: {normalize_text(review.get('summary'))}", "route": route, "scoreBand": scorecard.get("band")}],
        }
    if t == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        return validation
    if t == "assignment_structural_review":
        return run_structural_review(payload, job)
    if t == "assignment_pedagogy_review":
        return fallback_pedagogy_review(payload, job)
    if t == "assignment_style_review":
        return run_style_review(payload, job)
    if t == "assignment_similarity_review":
        return run_similarity_review(payload, job)
    if t == "assignment_runtime_review":
        return run_runtime_review(payload, job)
    if t == "assignment_repair":
        return fallback_repair_result(payload, job)
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




def run_brief_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    findings = []
    checks = []
    title_hint = normalize_text(brief.get("titleHint"))
    generation_prompt = normalize_text(brief.get("generationPrompt"))
    source_text = normalize_text(brief.get("sourceText"))
    difficulty = int(brief.get("difficultyTarget") or 0)
    target_skill = normalize_text(brief.get("targetSkill"))
    if not target_skill:
        findings.append({"severity": "high", "code": "brief.no_target_skill", "message": "В brief отсутствует targetSkill.", "suggestedRepair": "Добавь конкретный targetSkill."})
    checks.append({"name": "targetSkill", "status": "passed" if target_skill else "failed", "details": target_skill or "missing"})
    if not generation_prompt or len(generation_prompt) < 40:
        findings.append({"severity": "high", "code": "brief.short_generation_prompt", "message": "generationPrompt слишком короткий или пустой.", "suggestedRepair": "Уточни generationPrompt и учебную цель."})
    checks.append({"name": "generationPrompt", "status": "passed" if generation_prompt and len(generation_prompt) >= 40 else "failed", "details": str(len(generation_prompt))})
    if not source_text or len(source_text) < 10:
        findings.append({"severity": "medium", "code": "brief.short_source_text", "message": "sourceText слишком короткий.", "suggestedRepair": "Добавь предметную суть темы и edge cases."})
    checks.append({"name": "sourceText", "status": "passed" if source_text and len(source_text) >= 10 else "warning", "details": str(len(source_text))})
    broad_markers = ["и т.п", "и т.д", "всё", "несколько больших тем", "поиск +", "вставка +"]
    broad = any(m in generation_prompt.lower() for m in broad_markers)
    if broad:
        findings.append({"severity": "high", "code": "brief.too_broad", "message": "Brief слишком широкий и размытый.", "suggestedRepair": "Сузь brief до одной учебной цели и одной основной операции."})
    checks.append({"name": "scope", "status": "failed" if broad else "passed", "details": "broad" if broad else "focused"})
    if difficulty < 1 or difficulty > 5:
        findings.append({"severity": "medium", "code": "brief.bad_difficulty", "message": "difficultyTarget вне диапазона 1..5.", "suggestedRepair": "Приведи difficultyTarget к диапазону 1..5."})
    checks.append({"name": "difficultyTarget", "status": "passed" if 1 <= difficulty <= 5 else "warning", "details": str(difficulty)})
    status = "passed"
    if any(f["severity"] == "high" for f in findings):
        status = "failed"
    elif findings:
        status = "needs-review"
    score = max(0.0, 1.0 - 0.2 * len([f for f in findings if f["severity"] == "high"]) - 0.1 * len([f for f in findings if f["severity"] != "high"]))
    return {"status": status, "score": round(score, 3), "summary": "Brief review completed", "checks": checks, "findings": findings, "titleHint": title_hint}


def run_batch_context_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    refs = payload.get("batchPeerDrafts") if isinstance(payload.get("batchPeerDrafts"), list) else []
    item_ctx = payload.get("batchItemContext") if isinstance(payload.get("batchItemContext"), dict) else {}
    title = normalize_text(draft.get("title"))
    description = normalize_text(draft.get("description"))
    findings = []
    checks = []
    overlaps = []
    title_words = set(title.lower().split())
    desc_words = set(description.lower().split())
    for ref in refs[:20]:
        if not isinstance(ref, dict):
            continue
        rtitle = normalize_text(ref.get("title"))
        rdesc = normalize_text(ref.get("description") or ref.get("descriptionSummary"))
        tscore = jaccard(title_words, set(rtitle.lower().split())) if title_words else 0.0
        dscore = jaccard(desc_words, set(rdesc.lower().split())) if desc_words and rdesc else 0.0
        combo = max(tscore, dscore)
        if combo >= 0.45:
            overlaps.append({"id": ref.get("id"), "title": rtitle, "score": round(combo, 3)})
    overlaps.sort(key=lambda x: x["score"], reverse=True)
    if overlaps and overlaps[0]["score"] >= 0.75:
        findings.append({"severity": "high", "code": "batch_context.duplicate", "message": "Draft слишком похож на соседнюю задачу набора.", "suggestedRepair": "Измени микроцель, пример и тестовое ядро.", "confidence": overlaps[0]["score"]})
    elif len(overlaps) >= 2:
        findings.append({"severity": "medium", "code": "batch_context.cluster_overlap", "message": "Draft частично пересекается с несколькими соседними задачами набора.", "suggestedRepair": "Усиль отличие по навыку, формату или ограничениям.", "confidence": overlaps[0]["score"]})
    checks.append({"name": "peerOverlap", "status": "failed" if any(f["severity"] == "high" for f in findings) else ("warning" if findings else "passed"), "details": json.dumps(overlaps[:3], ensure_ascii=False)})

    target_skill = normalize_text(item_ctx.get("TargetSkill") or item_ctx.get("targetSkill"))
    difficulty_target = int(item_ctx.get("DifficultyTarget") or item_ctx.get("difficultyTarget") or 0)
    if target_skill and target_skill.lower() in description.lower():
        checks.append({"name": "skill-anchor", "status": "passed", "details": target_skill})
    elif target_skill:
        checks.append({"name": "skill-anchor", "status": "warning", "details": f"targetSkill={target_skill}"})
    if difficulty_target:
        checks.append({"name": "difficulty-target", "status": "passed", "details": str(difficulty_target)})

    status = "passed"
    if any(f["severity"] == "high" for f in findings):
        status = "failed"
    elif findings or any(c.get("status") == "warning" for c in checks):
        status = "needs-review"
    score = 1.0 - min(len(overlaps), 4) * 0.15 - (0.1 if any(c.get("status") == "warning" for c in checks) else 0.0)
    return {"status": status, "score": round(max(0.0, score), 3), "summary": "Batch context review completed.", "checks": checks, "findings": findings, "topOverlaps": overlaps[:5], "batchItemContext": item_ctx}

def build_batch_review_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    return (
        "Ты — TaskForge AI batch coherence reviewer. Верни только JSON без markdown.\n\n"
        "Оцени пакет задач как учебную систему: progression, coverage, redundancy, variety, risks.\n"
        "Верни status, score, summary, checks, findings, progression, coverage, decisionSummary.\n\n"
        f"Payload:\n{json.dumps(payload, ensure_ascii=False, indent=2)}"
    )


def run_batch_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    drafts = payload.get("batchPeerDrafts") if isinstance(payload.get("batchPeerDrafts"), list) else []
    batch_items = payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []
    titles = []
    dup = 0
    difficulty_jumps = 0
    repeated_skills = 0
    weak_items = 0
    previous_difficulty = None
    seen_skills = set()
    for item in batch_items:
        if not isinstance(item, dict):
            continue
        diff = item.get("difficultyTarget") if isinstance(item.get("difficultyTarget"), int) else item.get("DifficultyTarget")
        if isinstance(diff, int):
            if previous_difficulty is not None and abs(diff - previous_difficulty) > 1:
                difficulty_jumps += 1
            previous_difficulty = diff
        skill = normalize_text(item.get("targetSkill") or item.get("TargetSkill"))
        if skill:
            if skill.lower() in seen_skills:
                repeated_skills += 1
            seen_skills.add(skill.lower())
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        overall = scorecard.get("overallScore") if isinstance(scorecard.get("overallScore"), int) else None
        if isinstance(overall, int) and overall < 65:
            weak_items += 1
    for d in drafts:
        if isinstance(d, dict):
            t = normalize_text(d.get("title"))
            if t in titles and t:
                dup += 1
            titles.append(t)
    findings = []
    checks = []
    if dup:
        findings.append({"severity": "medium", "code": "batch.duplicate_titles", "message": "В batch есть повторяющиеся или слишком похожие заголовки.", "suggestedRepair": "Развести микроцели и названия задач.", "confidence": min(0.95, 0.4 + dup * 0.1)})
    if difficulty_jumps:
        findings.append({"severity": "medium" if difficulty_jumps == 1 else "high", "code": "batch.difficulty_jumps", "message": "В наборе есть резкие скачки сложности между соседними слотами.", "suggestedRepair": "Сгладить progression и переставить/переписать отдельные задачи.", "confidence": min(0.95, 0.5 + difficulty_jumps * 0.1)})
    if repeated_skills >= 2:
        findings.append({"severity": "medium", "code": "batch.skill_redundancy", "message": "Несколько batch items повторяют одну и ту же микроцель или skill-anchor.", "suggestedRepair": "Сместить targetSkill и microGoal у соседних items.", "confidence": 0.74})
    if weak_items:
        findings.append({"severity": "high" if weak_items >= 2 else "medium", "code": "batch.weak_items", "message": "В batch есть слабые items по scorecard aggregation.", "suggestedRepair": "Сначала вылечить слабые items, потом повторить batch review.", "confidence": min(0.97, 0.55 + weak_items * 0.1)})

    checks.append({"name": "redundancy", "status": "warning" if dup else "passed", "details": f"duplicateTitles={dup}"})
    checks.append({"name": "coverage", "status": "passed" if len(drafts) >= 2 else "warning", "details": f"draftCount={len(drafts)}"})
    checks.append({"name": "difficulty-balance", "status": "failed" if difficulty_jumps > 1 else ("warning" if difficulty_jumps else "passed"), "details": f"difficultyJumps={difficulty_jumps}"})
    checks.append({"name": "skill-diversity", "status": "warning" if repeated_skills else "passed", "details": f"repeatedSkills={repeated_skills}"})
    checks.append({"name": "student-journey", "status": "failed" if weak_items >= 2 else ("warning" if weak_items else "passed"), "details": f"weakItems={weak_items}"})

    status = summarize_status(checks)
    score = max(0.05, 0.95 - dup * 0.08 - difficulty_jumps * 0.1 - repeated_skills * 0.05 - weak_items * 0.12)
    return {
        "status": status,
        "score": round(score, 3),
        "summary": "Batch coherence review completed",
        "checks": checks,
        "findings": findings,
        "progression": {"status": "ok" if difficulty_jumps == 0 else "weak", "difficultyJumps": difficulty_jumps},
        "coverage": {"status": "ok" if drafts else "weak", "draftCount": len(drafts), "plannedItems": len(batch_items)},
        "difficultyBalance": {"status": "ok" if difficulty_jumps == 0 else "repair-needed", "jumps": difficulty_jumps},
        "studentJourney": {"status": "ok" if weak_items == 0 else "risk", "weakItems": weak_items},
        "decisionSummary": {"duplicateTitles": dup, "draftCount": len(drafts), "difficultyJumps": difficulty_jumps, "weakItems": weak_items, "repeatedSkills": repeated_skills}
    }

def build_student_journey_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    return (
        "Ты — TaskForge AI student journey simulator. Верни только JSON без markdown.\n\n"
        "Симулируй проход студента по batch items по порядку. Оцени мостики между задачами, резкость усложнения, потерю контекста и нехватку промежуточных шагов.\n"
        "Верни status, score, summary, checks, findings, transitions, journeySteps, decisionSummary.\n\n"
        f"Payload:\n{json.dumps(payload, ensure_ascii=False, indent=2)}"
    )


def run_student_journey_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    batch_items = sorted([x for x in (payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []) if isinstance(x, dict)], key=lambda x: int(x.get("index") or x.get("Index") or 0))
    findings = []
    checks = []
    transitions = []
    abrupt = 0
    missing_bridges = 0
    repeated_adjacent = 0
    weak_prereq = 0
    for idx, item in enumerate(batch_items):
        if idx == 0:
            continue
        prev = batch_items[idx - 1]
        prev_diff = int(prev.get("difficultyTarget") or prev.get("DifficultyTarget") or 0)
        current_diff = int(item.get("difficultyTarget") or item.get("DifficultyTarget") or 0)
        prev_skill = normalize_text(prev.get("targetSkill") or prev.get("TargetSkill"))
        current_skill = normalize_text(item.get("targetSkill") or item.get("TargetSkill"))
        prev_scorecard = prev.get("scorecard") if isinstance(prev.get("scorecard"), dict) else {}
        prev_score = prev_scorecard.get("overallScore") if isinstance(prev_scorecard.get("overallScore"), int) else None
        delta = current_diff - prev_diff
        risk = "ok"
        reasons = []
        if abs(delta) > 1:
            abrupt += 1
            missing_bridges += 1
            risk = "failed"
            reasons.append("difficulty-jump")
        if prev_score is not None and prev_score < 70 and current_diff >= prev_diff:
            weak_prereq += 1
            risk = "failed" if risk == "ok" else risk
            reasons.append("weak-prerequisite")
        if prev_skill and current_skill and prev_skill.lower() == current_skill.lower():
            repeated_adjacent += 1
            risk = "warning" if risk == "ok" else risk
            reasons.append("adjacent-skill-repeat")
        if current_diff - prev_diff == 1 and prev_score is not None and prev_score >= 80 and not reasons:
            reasons.append("healthy-step")
        transitions.append({
            "fromIndex": int(prev.get("index") or prev.get("Index") or idx),
            "toIndex": int(item.get("index") or item.get("Index") or idx + 1),
            "fromSkill": prev_skill,
            "toSkill": current_skill,
            "difficultyDelta": delta,
            "risk": risk,
            "reasons": reasons,
        })

    if abrupt:
        findings.append({"severity": "high" if abrupt >= 2 else "medium", "code": "journey.difficulty_jump", "message": "У студента есть резкие скачки сложности между соседними задачами.", "suggestedRepair": "Добавить bridge-item или переписать соседние briefs/difficulty targets.", "confidence": min(0.97, 0.52 + abrupt * 0.1)})
    if weak_prereq:
        findings.append({"severity": "medium", "code": "journey.weak_prerequisite", "message": "Следующая задача опирается на слабую предыдущую опору по scorecard.", "suggestedRepair": "Сначала вылечить слабый item или упростить следующий slot.", "confidence": min(0.95, 0.48 + weak_prereq * 0.08)})
    if repeated_adjacent >= 2:
        findings.append({"severity": "medium", "code": "journey.repeated_anchor", "message": "Соседние задачи слишком долго держатся за одинаковый skill-anchor.", "suggestedRepair": "Развести соседние microGoals и добавить variation step.", "confidence": 0.73})
    if missing_bridges:
        findings.append({"severity": "medium", "code": "journey.missing_bridge", "message": "В batch не хватает промежуточных мостиков между этапами обучения.", "suggestedRepair": "Вернуть planner/brief layer и вставить bridging item или сгладить progression.", "confidence": min(0.92, 0.45 + missing_bridges * 0.08)})

    checks.append({"name": "transition-bridges", "status": "failed" if missing_bridges >= 2 else ("warning" if missing_bridges else "passed"), "details": f"missingBridges={missing_bridges}"})
    checks.append({"name": "difficulty-progression", "status": "failed" if abrupt >= 2 else ("warning" if abrupt else "passed"), "details": f"abruptTransitions={abrupt}"})
    checks.append({"name": "prerequisite-health", "status": "warning" if weak_prereq else "passed", "details": f"weakPrerequisites={weak_prereq}"})
    checks.append({"name": "adjacent-variety", "status": "warning" if repeated_adjacent else "passed", "details": f"repeatedAdjacent={repeated_adjacent}"})

    status = summarize_status(checks)
    score = max(0.05, 0.96 - abrupt * 0.12 - weak_prereq * 0.08 - repeated_adjacent * 0.04 - missing_bridges * 0.06)
    journey_steps = []
    for item in batch_items[:12]:
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        journey_steps.append({
            "index": int(item.get("index") or item.get("Index") or 0),
            "targetSkill": normalize_text(item.get("targetSkill") or item.get("TargetSkill")),
            "difficultyTarget": int(item.get("difficultyTarget") or item.get("DifficultyTarget") or 0),
            "itemScore": scorecard.get("overallScore"),
        })
    return {
        "status": status,
        "score": round(score, 3),
        "summary": "Student journey review completed.",
        "checks": checks,
        "findings": findings,
        "transitions": transitions,
        "journeySteps": journey_steps,
        "decisionSummary": {"abruptTransitions": abrupt, "missingBridges": missing_bridges, "weakPrerequisites": weak_prereq, "repeatedAdjacent": repeated_adjacent},
    }


def run_batch_publish_prepare(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    batch_items = [x for x in (payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []) if isinstance(x, dict)]
    batch_review = payload.get("batchReview") if isinstance(payload.get("batchReview"), dict) else {}
    student_journey = payload.get("studentJourney") if isinstance(payload.get("studentJourney"), dict) else {}
    batch_summary = payload.get("batchSummary") if isinstance(payload.get("batchSummary"), dict) else {}

    item_scores = []
    weak_items = []
    for item in batch_items:
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        overall = scorecard.get("overallScore") if isinstance(scorecard.get("overallScore"), int) else None
        if overall is not None:
            item_scores.append(overall)
            if overall < 65:
                weak_items.append({"index": item.get("index") or item.get("Index"), "targetSkill": normalize_text(item.get("targetSkill") or item.get("TargetSkill")), "overallScore": overall})

    avg_score = round(sum(item_scores) / len(item_scores)) if item_scores else 0
    batch_review_score = int(round(float(batch_review.get("score") or 0) * 100)) if isinstance(batch_review.get("score"), (int, float)) else 0
    student_journey_score = int(round(float(student_journey.get("score") or 0) * 100)) if isinstance(student_journey.get("score"), (int, float)) else 0
    high_findings = 0
    for source in [batch_review, student_journey]:
        for finding in (source.get("findings") if isinstance(source.get("findings"), list) else []):
            if isinstance(finding, dict) and str(finding.get("severity") or "").lower() == "high":
                high_findings += 1

    readiness = "partial"
    if weak_items or batch_review_score < 65 or student_journey_score < 65 or high_findings >= 2:
        readiness = "repair-needed" if avg_score >= 50 else "blocked"
    elif avg_score >= 90 and batch_review_score >= 85 and student_journey_score >= 80 and high_findings == 0:
        readiness = "ready"
    elif avg_score < 75:
        readiness = "repair-needed"

    checks = [
        {"name": "item-score-floor", "status": "failed" if weak_items else "passed", "details": f"weakItems={len(weak_items)}"},
        {"name": "batch-coherence", "status": summarize_gate_status(batch_review_score), "details": f"batchReviewScore={batch_review_score}"},
        {"name": "student-journey", "status": summarize_gate_status(student_journey_score), "details": f"studentJourneyScore={student_journey_score}"},
        {"name": "average-item-quality", "status": summarize_gate_status(avg_score), "details": f"averageItemScore={avg_score}"},
    ]
    findings = []
    if weak_items:
        findings.append({"severity": "high" if len(weak_items) >= 2 else "medium", "code": "publish.weak_items", "message": "Не все items проходят минимальный quality floor для публикации.", "suggestedRepair": "Сначала закрыть слабые items и только потом публиковать batch.", "confidence": min(0.98, 0.5 + len(weak_items) * 0.1)})
    if batch_review_score < 75:
        findings.append({"severity": "medium", "code": "publish.batch_coherence", "message": "Batch coherence ещё недостаточно сильный для уверенной публикации.", "suggestedRepair": "Повторить batch planner / brief routing для проблемных slots.", "confidence": 0.76})
    if student_journey_score < 75:
        findings.append({"severity": "medium", "code": "publish.student_journey", "message": "Student journey всё ещё несёт риски пропусков или резких переходов.", "suggestedRepair": "Сгладить progression и вставить bridge-steps между задачами.", "confidence": 0.74})

    score = max(0.05, min(0.99, (avg_score * 0.55 + batch_review_score * 0.25 + student_journey_score * 0.20) / 100.0))
    return {
        "status": summarize_status(checks),
        "score": round(score, 3),
        "summary": "Batch publication readiness prepared.",
        "checks": checks,
        "findings": findings,
        "publicationDecision": {
            "readiness": readiness,
            "shouldPublish": readiness == "ready",
            "averageItemScore": avg_score,
            "batchReviewScore": batch_review_score,
            "studentJourneyScore": student_journey_score,
            "weakItems": weak_items,
        },
        "publishPack": {
            "readyCandidateIndexes": [int((x.get("index") or x.get("Index") or 0)) for x in batch_items if isinstance(x, dict) and isinstance((x.get("scorecard") if isinstance(x.get("scorecard"), dict) else {}).get("overallScore"), int) and (x.get("scorecard") or {}).get("overallScore") >= 80],
            "blockedCandidateIndexes": [int((x.get("index") or x.get("Index") or 0)) for x in batch_items if not (isinstance((x.get("scorecard") if isinstance(x.get("scorecard"), dict) else {}).get("overallScore"), int) and (x.get("scorecard") or {}).get("overallScore") >= 80)],
        },
        "decisionSummary": {"readiness": readiness, "highFindings": high_findings, "weakItems": len(weak_items), "itemsCount": len(batch_items)},
    }


def run_batch_planner_feedback(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    batch_items = sorted([x for x in (payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []) if isinstance(x, dict)], key=lambda x: int(x.get("index") or x.get("Index") or 0))
    batch_review = payload.get("batchReview") if isinstance(payload.get("batchReview"), dict) else {}
    student_journey = payload.get("studentJourney") if isinstance(payload.get("studentJourney"), dict) else {}
    publication_audit = payload.get("publicationAudit") if isinstance(payload.get("publicationAudit"), dict) else {}
    publish_pack = payload.get("publishPack") if isinstance(payload.get("publishPack"), dict) else {}
    publication_decision = publication_audit.get("publicationDecision") if isinstance(publication_audit.get("publicationDecision"), dict) else {}
    readiness = normalize_text(publication_decision.get("readiness") or "partial") or "partial"

    slot_recommendations = []
    anti_patterns = []
    planner_adjustments = []
    auto_brief_repair_candidates = 0
    replan_suggested = False

    for item in batch_items:
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        overall = scorecard.get("overallScore") if isinstance(scorecard.get("overallScore"), int) else None
        repair_plan = scorecard.get("repairPlan") if isinstance(scorecard.get("repairPlan"), dict) else {}
        primary_route = normalize_text(repair_plan.get("primaryRoute") or "general") or "general"
        recommendation = None
        if overall is not None and overall < 50:
            recommendation = "rewrite-brief"
        elif primary_route in ["brief", "description"] or (overall is not None and overall < 65):
            recommendation = "brief-repair"
        elif primary_route == "tests":
            recommendation = "test-repair"
        elif primary_route in ["solution", "policy"]:
            recommendation = "draft-repair"
        if recommendation:
            slot_recommendations.append({
                "index": int(item.get("index") or item.get("Index") or 0),
                "targetSkill": normalize_text(item.get("targetSkill") or item.get("TargetSkill")),
                "overallScore": overall,
                "primaryRoute": primary_route,
                "targetAction": recommendation,
                "reason": f"score={overall}, route={primary_route}",
            })
            if "brief" in recommendation:
                auto_brief_repair_candidates += 1

    transitions = [x for x in (student_journey.get("transitions") if isinstance(student_journey.get("transitions"), list) else []) if isinstance(x, dict)]
    risky_transitions = [x for x in transitions if normalize_text(x.get("risk")) and normalize_text(x.get("risk")) != "ok"]
    for transition in risky_transitions[:8]:
        reasons = [normalize_text(r) for r in (transition.get("reasons") if isinstance(transition.get("reasons"), list) else []) if normalize_text(r)]
        planner_adjustments.append({
            "type": "bridge-adjustment",
            "fromIndex": transition.get("fromIndex"),
            "toIndex": transition.get("toIndex"),
            "action": "insert bridge or lower difficulty delta",
            "reasons": reasons,
        })
        anti_patterns.append({
            "kind": "journey-transition",
            "fromIndex": transition.get("fromIndex"),
            "toIndex": transition.get("toIndex"),
            "risk": normalize_text(transition.get("risk")),
            "reasons": reasons,
        })
        if any(r in ["difficulty-jump", "missing-bridge", "weak-prerequisite"] for r in reasons):
            replan_suggested = True

    for source_name, source in [("batch", batch_review), ("journey", student_journey), ("publication", publication_audit)]:
        for finding in (source.get("findings") if isinstance(source.get("findings"), list) else []):
            if not isinstance(finding, dict):
                continue
            anti_patterns.append({
                "kind": f"{source_name}-finding",
                "code": normalize_text(finding.get("code")),
                "severity": normalize_text(finding.get("severity") or "medium"),
                "message": normalize_text(finding.get("message")),
                "suggestedRepair": normalize_text(finding.get("suggestedRepair")),
            })

    batch_action = "ready"
    if readiness in ["blocked", "repair-needed"]:
        batch_action = "re-brief-weak-items" if auto_brief_repair_candidates else "replan-batch"
    elif readiness == "partial":
        batch_action = "stabilize-and-repair"
    if replan_suggested and auto_brief_repair_candidates >= 2:
        batch_action = "replan-batch"

    checks = [
        {"name": "publication-readiness", "status": "passed" if readiness == "ready" else ("warning" if readiness == "partial" else "failed"), "details": readiness},
        {"name": "auto-brief-repair-candidates", "status": "warning" if auto_brief_repair_candidates else "passed", "details": f"count={auto_brief_repair_candidates}"},
        {"name": "progression-replan-signal", "status": "warning" if replan_suggested else "passed", "details": f"riskyTransitions={len(risky_transitions)}"},
    ]
    findings = []
    if auto_brief_repair_candidates:
        findings.append({"severity": "medium" if auto_brief_repair_candidates == 1 else "high", "code": "planner.auto_brief_repair", "message": "Часть slots лучше отправить назад в brief layer, а не пытаться чинить только draft-слой.", "suggestedRepair": "Запусти brief repair для слабых items с planner feedback context.", "confidence": min(0.98, 0.55 + auto_brief_repair_candidates * 0.08)})
    if replan_suggested:
        findings.append({"severity": "medium", "code": "planner.replan_signal", "message": "Batch-level progression signals подсказывают, что planner стоит переоценить на уровне связок между tasks.", "suggestedRepair": "Вернуть batch в planner/brief layer и сгладить risky transitions.", "confidence": 0.78})

    score = max(0.05, min(0.99, (float(publication_audit.get("score") or 0.0) * 0.55) + (float(student_journey.get("score") or 0.0) * 0.20) + (0.20 if auto_brief_repair_candidates == 0 else 0.08) + (0.04 if not replan_suggested else 0.0)))
    return {
        "status": summarize_status(checks),
        "score": round(score, 3),
        "summary": "Batch planner feedback prepared.",
        "checks": checks,
        "findings": findings,
        "plannerAdjustments": planner_adjustments[:10],
        "slotRecommendations": slot_recommendations[:12],
        "antiPatterns": anti_patterns[:16],
        "decisionSummary": {
            "publicationReadiness": readiness,
            "batchAction": batch_action,
            "autoBriefRepairCandidates": auto_brief_repair_candidates,
            "replanSuggested": replan_suggested,
            "riskyTransitions": len(risky_transitions),
        },
    }


def summarize_gate_status(score: int) -> str:
    if score >= 85:
        return "passed"
    if score >= 70:
        return "warning"
    return "failed"


def process_job(job: Dict[str, Any]) -> Dict[str, Any]:
    log("process_job >>>", {"jobId": job.get("id"), "type": job.get("type"), "courseId": job.get("courseId"), "targetEntityType": job.get("targetEntityType"), "targetEntityId": job.get("targetEntityId")})
    payload = parse_payload(job)
    job_type = (job.get("type") or "").lower().strip()
    if job_type == "assignment_course_profile_build":
        prompt = build_course_profile_prompt(job, payload)
        log("built course profile prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("course profile ollama failed, using fallback:", ex)
            result = fallback_course_profile(payload, job)
        log("process_job <<< course profile", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
        return result
    if job_type == "assignment_gap_analysis":
        prompt = build_gap_analysis_prompt(job, payload)
        log("built gap analysis prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("gap analysis ollama failed, using fallback:", ex)
            result = fallback_gap_analysis(payload, job)
        log("process_job <<< gap analysis", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
        return result
    if job_type in {"assignment_batch_plan", "assignment_batch_replan"}:
        prompt = build_batch_plan_prompt(job, payload)
        log("built batch plan/replan prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("batch plan/replan ollama failed, using fallback:", ex)
            result = fallback_result(job)
        log("process_job <<< batch plan/replan", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
        return result
    if job_type == "assignment_reference_pack_build":
        prompt = build_reference_pack_prompt(job, payload)
        log("built reference pack prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("reference pack ollama failed, using fallback:", ex)
            result = fallback_result(job)
        log("process_job <<< reference pack", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
        return result
    if job_type == "assignment_brief_generate":
        prompt = build_brief_prompt(job, payload)
        log("built brief prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("brief ollama failed, using fallback:", ex)
            result = fallback_result(job)
        log("process_job <<< brief result", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
        return result
    if job_type == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        log("running local validation self-check", {"jobId": job.get("id"), "draftKeys": list(draft.keys())[:30]})
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        log("process_job <<< validation result", {"jobId": job.get("id"), "status": validation.get("status"), "score": validation.get("score"), "summary": validation.get("summary")})
        return validation
    if job_type == "assignment_brief_review":
        review = run_brief_review(payload, job)
        log("process_job <<< brief review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_brief_repair":
        repaired = fallback_result(job)
        log("process_job <<< brief repair", {"jobId": job.get("id"), "keys": list(repaired.keys())[:30]})
        return repaired
    if job_type == "assignment_structural_review":
        review = run_structural_review(payload, job)
        log("process_job <<< structural review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_pedagogy_review":
        review = fallback_pedagogy_review(payload, job)
        log("process_job <<< pedagogy review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_style_review":
        review = run_style_review(payload, job)
        log("process_job <<< style review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_similarity_review":
        review = run_similarity_review(payload, job)
        log("process_job <<< similarity review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_batch_context_review":
        review = run_batch_context_review(payload, job)
        log("process_job <<< batch context review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_test_strength_review":
        review = run_test_strength_review(payload, job)
        log("process_job <<< test strength review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_runtime_review":
        review = run_runtime_review(payload, job)
        log("process_job <<< runtime review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_student_journey_review":
        review = run_student_journey_review(payload, job)
        log("process_job <<< student journey review", {"jobId": job.get("id"), "status": review.get("status"), "score": review.get("score")})
        return review
    if job_type == "assignment_batch_publish_prepare":
        result = run_batch_publish_prepare(payload, job)
        log("process_job <<< batch publish prepare", {"jobId": job.get("id"), "status": result.get("status"), "score": result.get("score")})
        return result
    if job_type == "assignment_batch_planner_feedback":
        result = run_batch_planner_feedback(payload, job)
        log("process_job <<< batch planner feedback", {"jobId": job.get("id"), "status": result.get("status"), "score": result.get("score")})
        return result
    if job_type == "assignment_batch_review":
        prompt = build_batch_review_prompt(job, payload)
        log("built batch review prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("batch review ollama failed, using local fallback:", ex)
            result = run_batch_review(payload, job)
        log("process_job <<< batch review", {"jobId": job.get("id"), "status": result.get("status"), "score": result.get("score")})
        return result
    if job_type == "assignment_repair":
        prompt = build_prompt(job, payload)
        log("built repair prompt", {"jobId": job.get("id"), "promptLen": len(prompt), "promptPreview": prompt[:1500]})
        try:
            result = call_ollama(prompt)
        except Exception as ex:
            log("repair ollama failed, using fallback:", ex)
            repaired = fallback_repair_result(payload, job)
            log("process_job <<< fallback repair", {"jobId": job.get("id"), "keys": list(repaired.keys())[:30]})
            return repaired
        repaired_draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
        if isinstance(repaired_draft, dict):
            validation = run_self_check(repaired_draft)
            result["draftValidation"] = validation
        log("process_job <<< repair result", {"jobId": job.get("id"), "keys": list(result.keys())[:30]})
        return result
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
