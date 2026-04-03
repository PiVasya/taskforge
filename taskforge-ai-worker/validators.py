"""Draft validation, quality checks and self-check orchestration.

BUG-FIX: ``run_self_check`` and ``attach_self_check`` were *called* in the
original monolith but never *defined*.  They are implemented here.
"""

from typing import Any, Dict, List

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_DESCRIPTION_LEN
from text_utils import (
    normalize_text,
    truncate_text,
    has_html_markup,
    safe_eval_number,
    safe_int,
)
from runners import run_python_solution


# ── Shared helpers ───────────────────────────────────

def has_html(text: str) -> bool:
    return has_html_markup(text)


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


def summarize_gate_status(score: int) -> str:
    """Convert a 0-100 score into a gate status string."""
    if score >= 85:
        return "passed"
    if score >= 70:
        return "warning"
    return "failed"


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


# ── Type-specific validators ─────────────────────────

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
        unique_pairs = {
            f"{normalize_text(t.get('input'))}|{normalize_text(t.get('expectedOutput'))}"
            for t in tests if isinstance(t, dict)
        }
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
            checks.append({
                "name": f"test-{idx}",
                "status": "passed" if ok else "failed",
                "details": (
                    f"expected={expected!r}; actual={actual!r}; "
                    f"rc={res.get('returncode')} "
                    f"stderr={truncate_text(res.get('stderr') or '', 200)!r}"
                ),
            })
            if ok:
                passed_runtime += 1
        except Exception as ex:
            checks.append({"name": f"test-{idx}", "status": "failed", "details": f"runtime error: {ex}"})

    status = summarize_status(checks)
    score = sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks))
    summary = (
        f"Code-test self-check: runtime {passed_runtime}/{len(tests)}, "
        f"checks passed {sum(1 for x in checks if x.get('status') == 'passed')}/{len(checks)}."
    )
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
        elif kind == "match" and not (
            (block.get("leftItems") or [])
            and (block.get("rightItems") or [])
            and (block.get("pairs") or block.get("matchPairs") or [])
        ):
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
    seen_prompts: set = set()
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


# ── Unified dispatcher (BUG-FIX: was never defined) ──

def run_self_check(draft: Dict[str, Any]) -> Dict[str, Any]:
    """Dispatch to the correct type-specific validator based on assignmentType.

    This function was *called* throughout the original worker.py but was
    never actually defined — causing a guaranteed NameError at runtime.
    """
    assignment_type = str(draft.get("assignmentType") or "").strip().lower()
    if assignment_type == "code-test":
        return validate_code_test_draft(draft)
    if assignment_type == "math":
        return validate_math_draft(draft)
    if assignment_type == "test":
        return validate_test_draft(draft)
    # Unknown type — return a minimal pass-through validation
    checks = collect_quality_checks_common(draft)
    return {
        "status": summarize_status(checks),
        "summary": f"Generic self-check for type={assignment_type or 'unknown'}.",
        "score": sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks)),
        "checks": checks,
    }


def attach_self_check(
    result: Dict[str, Any],
    draft: Dict[str, Any],
    validation: Dict[str, Any],
) -> Dict[str, Any]:
    """Merge *validation* into *result* under ``draftValidation`` and ensure
    the draft's ``meta`` dict carries a self-check summary.

    This function was *called* throughout the original worker.py but was
    never actually defined — causing a guaranteed NameError at runtime.
    """
    result = dict(result)  # shallow copy so caller's dict isn't mutated
    result["draftValidation"] = validation

    # Stamp a short summary into draft.meta so downstream stages can see it
    if isinstance(draft, dict):
        meta = draft.get("meta") if isinstance(draft.get("meta"), dict) else {}
        meta["selfCheck"] = {
            "status": validation.get("status"),
            "score": validation.get("score"),
            "summary": validation.get("summary"),
        }
        draft["meta"] = meta
        result["draft"] = draft
    return result
