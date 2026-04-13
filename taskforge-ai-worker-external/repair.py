"""Draft repair logic — both the LLM-driven repair loop and the deterministic
fallback repair.

BUG-FIX: ``int()`` calls replaced with ``safe_int()`` throughout.
"""

import json
import re
import time
from typing import Any, Dict, List

from config import (
    MAX_REPAIR_ATTEMPTS,
    MIN_DESCRIPTION_LEN,
    MIN_HIDDEN_TESTS,
    MIN_PUBLIC_TESTS,
    ROUTE_AWARE_REPAIR,
)
from log import log
from llm_client import call_llm
from payload import sanitize_result_payload
from prompt_builder import build_repair_prompt, _extract_instruction_contract, _instruction_strictness
from reviews import run_similarity_review
from text_utils import has_html_markup, normalize_text, strip_conflicting_lists, truncate_text
from validators import attach_self_check, run_self_check


# ── Deterministic fallback repair ─────────────────────

def _gather_review_hints(reviews: List[Dict[str, Any]]) -> List[str]:
    hints: List[str] = []
    for review in reviews[:6]:
        if not isinstance(review, dict):
            continue
        result = review.get("result") if isinstance(review.get("result"), dict) else {}
        for finding in (result.get("findings") if isinstance(result.get("findings"), list) else [])[:2]:
            if isinstance(finding, dict):
                msg = normalize_text(finding.get("message"))
                if msg:
                    hints.append(msg)
    return hints


def _unique_tests(items: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    seen: set[str] = set()
    result: List[Dict[str, Any]] = []
    for item in items:
        if not isinstance(item, dict):
            continue
        key = f"{normalize_text(item.get('input'))}|{normalize_text(item.get('expectedOutput'))}"
        if key in seen:
            continue
        seen.add(key)
        result.append(dict(item))
    return result


def _rebalance_code_tests(draft: Dict[str, Any]) -> None:
    public_tests = _unique_tests([dict(x) for x in list(draft.get("publicTests") or []) if isinstance(x, dict)])
    hidden_tests = _unique_tests([dict(x) for x in list(draft.get("hiddenTests") or []) if isinstance(x, dict)])
    while len(public_tests) < MIN_PUBLIC_TESTS and len(hidden_tests) > MIN_HIDDEN_TESTS:
        public_tests.append(hidden_tests.pop(0))
    while len(hidden_tests) < MIN_HIDDEN_TESTS and len(public_tests) > MIN_PUBLIC_TESTS:
        hidden_tests.append(public_tests.pop())
    draft["publicTests"] = public_tests
    draft["hiddenTests"] = hidden_tests


def _wrap_python_solution(code: str) -> str:
    raw = str(code or "").rstrip()
    if not raw:
        return raw
    low = raw.lower()
    if "def solve" in raw or "if __name__ == '__main__'" in low or 'if __name__ == "__main__"' in low:
        return raw + ("\n" if not raw.endswith("\n") else "")
    body = "\n".join(f"    {line}" if line.strip() else "" for line in raw.splitlines())
    return (
        "def solve():\n"
        f"{body}\n\n"
        "if __name__ == '__main__':\n"
        "    solve()\n"
    )


def _pad_description(description: str, hints: List[str]) -> str:
    text = normalize_text(description)
    if len(text) >= MIN_DESCRIPTION_LEN:
        return text
    suffix_bits = [
        "Уточни формат входных данных, ожидаемый формат вывода и ограничение на крайние случаи.",
    ]
    if hints:
        suffix_bits.append("Ключевые замечания для исправления: " + "; ".join(hints[:3]))
    padded = (text + "\n\n" + " ".join(suffix_bits)).strip() if text else " ".join(suffix_bits)
    return truncate_text(padded, max(MIN_DESCRIPTION_LEN + 160, len(padded)))


def _apply_route_aware_repairs(repaired: Dict[str, Any], primary_route: str, hints: List[str]) -> None:
    if not ROUTE_AWARE_REPAIR:
        return
    route = normalize_text(primary_route).lower() or "general"
    if route in {"description", "brief", "general"}:
        repaired["description"] = _pad_description(str(repaired.get("description") or ""), hints)
    if repaired.get("assignmentType") == "code-test":
        repaired["publicTests"] = [dict(x) for x in list(repaired.get("publicTests") or []) if isinstance(x, dict)]
        repaired["hiddenTests"] = [dict(x) for x in list(repaired.get("hiddenTests") or []) if isinstance(x, dict)]
        required, forbidden, _ = strip_conflicting_lists(repaired.get("requiredCalls"), repaired.get("forbiddenCalls"))
        repaired["requiredCalls"] = required
        repaired["forbiddenCalls"] = forbidden
        if route in {"tests", "general"}:
            _rebalance_code_tests(repaired)
        if route in {"solution", "general"}:
            code = normalize_text(repaired.get("referenceSolutionPython"))
            if code:
                repaired["referenceSolutionPython"] = _wrap_python_solution(code)
        if route == "policy":
            repaired["requiredCalls"] = required
            repaired["forbiddenCalls"] = forbidden


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
    if title:
        repaired["title"] = re.sub(r"\s+[—-]\s*(revised|draft|final|version)\b.*$", "", title, flags=re.I).strip() or title

    description = str(repaired.get("description") or "")
    if description and has_html_markup(description):
        repaired["description"] = normalize_text(description)

    findings_digest = _gather_review_hints(reviews)
    _apply_route_aware_repairs(repaired, primary_route, findings_digest)

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
        "routeAware": bool(ROUTE_AWARE_REPAIR),
    }
    repaired["meta"] = meta
    wrapped = sanitize_result_payload(job.get("type") or "assignment_repair", payload, {"draft": repaired})
    repaired_wrapped = wrapped.get("draft") if isinstance(wrapped.get("draft"), dict) else repaired
    validation = run_self_check(repaired_wrapped)
    return {
        "schemaVersion": normalize_text(payload.get("schemaVersion")) or "draft-v2",
        "draft": repaired_wrapped,
        "repairSummary": f"Fallback repair applied via route {primary_route} with deterministic route-aware adjustments.",
        "draftValidation": validation,
    }


def _with_reference_similarity_validation(payload: Dict[str, Any], draft: Dict[str, Any], validation: Dict[str, Any]) -> Dict[str, Any]:
    refs = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    if not refs:
        return validation
    similarity = run_similarity_review({"draft": draft, "referenceAssignments": refs}, {})
    top = similarity.get("topReferences") if isinstance(similarity.get("topReferences"), list) else []
    checks = list(validation.get("checks") or [])
    findings = list(validation.get("findings") or [])
    status = normalize_text(validation.get("status")).lower() or "passed"
    top_ref = top[0] if top and isinstance(top[0], dict) else {}
    max_sim = float(top_ref.get("combinedSimilarity") or 0.0)
    title_sim = float(top_ref.get("titleSimilarity") or 0.0)
    desc_sim = float(top_ref.get("descriptionSimilarity") or 0.0)
    ref_title = normalize_text(top_ref.get("title"))
    exact_title = bool(ref_title and normalize_text(draft.get("title")).casefold() == ref_title.casefold())
    duplicate_status = "passed"
    duplicate_details = f"maxSimilarity={max_sim:.2f}"
    if exact_title or max_sim >= 0.86 or desc_sim >= 0.78:
        duplicate_status = "failed"
        status = "needs-review"
        duplicate_details = f"Слишком близко к существующему заданию: {ref_title or 'reference'} ({max_sim:.2f})"
    elif max_sim >= 0.72 or title_sim >= 0.72:
        duplicate_status = "warning"
        if status == "passed":
            status = "needs-review"
        duplicate_details = f"Похоже на существующее задание: {ref_title or 'reference'} ({max_sim:.2f})"
    checks.append({"name": "reference-similarity", "status": duplicate_status, "details": duplicate_details})
    if duplicate_status != "passed":
        findings.append({
            "severity": "high" if duplicate_status == "failed" else "medium",
            "code": "reference-similarity",
            "message": duplicate_details,
            "repairHint": "Измени учебную цель и shape задачи: другой ввод, другая операция, другой формат вывода или другой ожидаемый результат.",
        })
    passed = sum(1 for item in checks if normalize_text((item or {}).get("status")).lower() == "passed")
    total = max(1, len(checks))
    summary = normalize_text(validation.get("summary")) or "Code-test self-check completed."
    if duplicate_status != "passed":
        summary = f"{summary} Reference similarity requires changes."
    return {
        **validation,
        "status": status,
        "score": passed / total,
        "checks": checks,
        "findings": findings,
        "summary": summary,
    }


def _with_instruction_fidelity_validation(payload: Dict[str, Any], draft: Dict[str, Any], validation: Dict[str, Any]) -> Dict[str, Any]:
    strictness = _instruction_strictness(payload)
    if strictness < 70:
        return validation
    contract = _extract_instruction_contract(payload)
    exact = [str(x).strip() for x in (contract.get("exactSnippets") or []) if str(x).strip()]
    forbidden = [str(x).strip() for x in (contract.get("forbiddenSnippets") or []) if str(x).strip()]
    if not exact and not forbidden and not contract.get("preserveOrder"):
        return validation

    haystack = "\n".join([
        normalize_text(draft.get("title")),
        normalize_text(draft.get("description")),
        normalize_text(draft.get("referenceSolutionPython")),
    ])
    low_haystack = haystack.casefold()
    checks = list(validation.get("checks") or [])
    findings = list(validation.get("findings") or [])
    status = normalize_text(validation.get("status")).lower() or "passed"

    missing_exact = [snippet for snippet in exact[:6] if snippet.casefold() not in low_haystack]
    present_positions = []
    for snippet in exact[:6]:
        idx = low_haystack.find(snippet.casefold())
        if idx >= 0:
            present_positions.append((snippet, idx))
    order_ok = True
    if contract.get("preserveOrder") and len(present_positions) >= 2:
        order_ok = [idx for _, idx in present_positions] == sorted(idx for _, idx in present_positions)

    forbidden_hits = [snippet for snippet in forbidden[:6] if snippet.casefold() in low_haystack]

    if missing_exact:
        checks.append({"name": "instruction-exact-snippets", "status": "failed", "details": "Не сохранены явные пользовательские фрагменты: " + "; ".join(missing_exact[:4])})
        findings.append({
            "severity": "high" if strictness >= 90 else "medium",
            "code": "instruction-exact-snippets",
            "message": "В draft пропали явные фрагменты из пользовательской инструкции: " + "; ".join(missing_exact[:4]),
            "repairHint": "Сохрани обязательные пользовательские фрагменты дословно в уместном месте draft, не заменяй их абстрактным пересказом.",
        })
        status = "needs-review"
    else:
        checks.append({"name": "instruction-exact-snippets", "status": "passed", "details": f"preserved={len(present_positions)}"})

    if contract.get("preserveOrder"):
        if order_ok:
            checks.append({"name": "instruction-order", "status": "passed", "details": "Порядок пользовательских шагов сохранён."})
        else:
            checks.append({"name": "instruction-order", "status": "failed", "details": "Порядок явных пользовательских шагов нарушен."})
            findings.append({
                "severity": "medium",
                "code": "instruction-order",
                "message": "Draft нарушает порядок шагов/примеров, который пользователь просил сохранить.",
                "repairHint": "Верни шаги и примеры в том же порядке, в каком их задал пользователь.",
            })
            status = "needs-review"

    if forbidden_hits:
        checks.append({"name": "instruction-forbidden-snippets", "status": "failed", "details": "Нарушены пользовательские запреты: " + "; ".join(forbidden_hits[:4])})
        findings.append({
            "severity": "high" if strictness >= 85 else "medium",
            "code": "instruction-forbidden-snippets",
            "message": "Draft содержит то, что пользователь явно запретил: " + "; ".join(forbidden_hits[:4]),
            "repairHint": "Удали или перепиши запрещённые пользователем фрагменты и не добавляй их обратно при ремонте.",
        })
        status = "needs-review"
    else:
        checks.append({"name": "instruction-forbidden-snippets", "status": "passed", "details": "Явные пользовательские запреты соблюдены."})

    passed = sum(1 for item in checks if normalize_text((item or {}).get("status")).lower() == "passed")
    total = max(1, len(checks))
    summary = normalize_text(validation.get("summary")) or "Draft self-check completed."
    if missing_exact or forbidden_hits or (contract.get("preserveOrder") and not order_ok):
        summary = f"{summary} Instruction fidelity requires changes."
    return {
        **validation,
        "status": status,
        "score": passed / total,
        "checks": checks,
        "findings": findings,
        "summary": summary,
    }


# ── LLM-driven repair loop ───────────────────────────

def _coerce_repair_result(job: Dict[str, Any], payload: Dict[str, Any], repaired: Any) -> Dict[str, Any] | None:
    if not isinstance(repaired, dict):
        return None
    job_type = job.get("type") or "assignment_repair"
    sanitized = sanitize_result_payload(job_type, payload, repaired)
    if isinstance(sanitized.get("draft"), dict):
        return sanitized
    for key in ("result", "assignment", "candidate", "payload"):
        nested = repaired.get(key)
        if not isinstance(nested, dict):
            continue
        wrapped = sanitize_result_payload(job_type, payload, nested)
        if isinstance(wrapped.get("draft"), dict):
            return wrapped
        wrapped = sanitize_result_payload(job_type, payload, {"draft": nested})
        if isinstance(wrapped.get("draft"), dict):
            return wrapped
    if repaired.get("title") and repaired.get("description"):
        wrapped = sanitize_result_payload(job_type, payload, repaired)
        if isinstance(wrapped.get("draft"), dict):
            return wrapped
    return None


def try_improve_generation(
    job: Dict[str, Any],
    payload: Dict[str, Any],
    result: Dict[str, Any],
) -> Dict[str, Any]:
    draft = result.get("draft") if isinstance(result.get("draft"), dict) else None
    if not isinstance(draft, dict):
        return result
    validation = _with_instruction_fidelity_validation(payload, draft, _with_reference_similarity_validation(payload, draft, run_self_check(draft)))
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
            repaired = call_llm(repair_prompt)
            repaired = _coerce_repair_result(job, payload, repaired)
            repaired_draft = repaired.get("draft") if isinstance(repaired, dict) and isinstance(repaired.get("draft"), dict) else None
            if not isinstance(repaired_draft, dict):
                log("repair returned no draft", {"jobId": job.get("id"), "attempt": attempt_no})
                continue
            current_validation = _with_instruction_fidelity_validation(payload, repaired_draft, _with_reference_similarity_validation(payload, repaired_draft, run_self_check(repaired_draft)))
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
