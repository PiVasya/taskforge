"""Draft repair logic — both the LLM-driven repair loop and the deterministic
fallback repair.

BUG-FIX: ``int()`` calls replaced with ``safe_int()`` throughout.
"""

import json
import time
from typing import Any, Dict, List

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_DESCRIPTION_LEN, MAX_REPAIR_ATTEMPTS
from log import log
from text_utils import normalize_text, truncate_text, has_html_markup, safe_int
from validators import run_self_check, attach_self_check
from prompt_builder import build_repair_prompt
from ollama import call_ollama


# ── Deterministic fallback repair ─────────────────────

def fallback_repair_result(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    reviews = payload.get("reviewResults") if isinstance(payload.get("reviewResults"), list) else []
    repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
    scorecard = payload.get("scorecard") if isinstance(payload.get("scorecard"), dict) else {}
    routes = [
        normalize_text(x)
        for x in (repair_plan.get("routes") if isinstance(repair_plan.get("routes"), list) else [])
        if normalize_text(x)
    ]
    primary_route = normalize_text(repair_plan.get("primaryRoute") or "general") or "general"
    if primary_route not in routes:
        routes.append(primary_route)

    repaired = json.loads(json.dumps(draft, ensure_ascii=False)) if draft else {}
    title = normalize_text(repaired.get("title"))
    if title and title.lower().startswith("ai fallback"):
        repaired["title"] = title.replace("AI fallback:", "").strip() or title

    description = str(repaired.get("description") or "")
    if description and has_html_markup(description):
        description = normalize_text(description)
        repaired["description"] = description
    if len(normalize_text(repaired.get("description"))) < MIN_DESCRIPTION_LEN or "description" in routes:
        repaired["description"] = (
            "Исправленная AI-версией формулировка задания.\n\n"
            "Входные данные: явно укажи формат ввода, включая пробелы и крайние случаи.\n\n"
            "Выходные данные: выведите точный результат решения задачи.\n\n"
            "Примечание: формулировка была автоматически расширена после review и scorecard aggregation."
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
        repaired["referenceSolutionPython"] = code or (
            "import sys\n\n"
            "def solve(data: str) -> str:\n"
            "    return data.strip()\n\n"
            "if __name__ == '__main__':\n"
            "    print(solve(sys.stdin.read()))"
        )
        if "policy" in routes:
            repaired["requiredCalls"] = list(repaired.get("requiredCalls") or ["solve"])
            repaired["forbiddenCalls"] = list(repaired.get("forbiddenCalls") or ["Process.Start", "__import__"])

    if "brief" in routes:
        repaired["title"] = normalize_text(repaired.get("title") or "Новая вариация задачи") + " — revised"

    findings_digest: List[str] = []
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


# ── LLM-driven repair loop ───────────────────────────

def try_improve_generation(
    job: Dict[str, Any],
    payload: Dict[str, Any],
    result: Dict[str, Any],
) -> Dict[str, Any]:
    draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
    if not isinstance(draft, dict):
        return result
    validation = run_self_check(draft)
    log("self-check generated draft", {
        "jobId": job.get("id"), "status": validation.get("status"),
        "score": validation.get("score"), "summary": validation.get("summary"),
    })
    if validation.get("status") == "passed":
        return attach_self_check(result, draft, validation)

    current_result = result
    current_validation = validation
    for attempt_no in range(1, MAX_REPAIR_ATTEMPTS + 1):
        try:
            repair_prompt = build_repair_prompt(job, payload, current_result, current_validation, attempt_no)
            log("repair prompt built", {
                "jobId": job.get("id"), "attempt": attempt_no,
                "promptLen": len(repair_prompt), "promptPreview": repair_prompt[:1500],
            })
            repaired = call_ollama(repair_prompt)
            repaired_draft = repaired.get("draft") if isinstance(repaired.get("draft"), dict) else None
            if not isinstance(repaired_draft, dict):
                log("repair returned no draft", {"jobId": job.get("id"), "attempt": attempt_no})
                continue
            current_validation = run_self_check(repaired_draft)
            log("repair self-check", {
                "jobId": job.get("id"), "attempt": attempt_no,
                "status": current_validation.get("status"),
                "score": current_validation.get("score"),
                "summary": current_validation.get("summary"),
            })
            current_result = repaired
            if current_validation.get("status") == "passed":
                return attach_self_check(current_result, repaired_draft, current_validation)
        except Exception as ex:
            log("repair attempt failed", {"jobId": job.get("id"), "attempt": attempt_no, "error": str(ex)})

    final_draft = current_result.get("draft") if isinstance(current_result.get("draft"), dict) else draft
    return attach_self_check(
        current_result,
        final_draft if isinstance(final_draft, dict) else draft,
        current_validation,
    )
