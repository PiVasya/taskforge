"""Draft validation, quality checks and self-check orchestration.

BUG-FIX: ``run_self_check`` and ``attach_self_check`` were *called* in the
original monolith but never *defined*.  They are implemented here.
"""

from typing import Any, Dict, List

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_TOTAL_TESTS, MIN_DESCRIPTION_LEN
from text_utils import (
    normalize_text,
    truncate_text,
    has_html_markup,
    safe_eval_number,
    safe_int,
)
from runners import run_python_solution


# ── Shared helpers ───────────────────────────────────

def _is_site_incompatible_test_input(value: Any) -> bool:
    if value is None:
        return True
    text = str(value)
    return text != "" and text.strip() == ""


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
    if description:
        checks.append({"name": "description-format", "status": "passed", "details": "description допускается как plain text и затем нормализуется при публикации"})
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

def _contains_any_keys(item: Dict[str, Any], keys: List[str]) -> List[str]:
    return [key for key in keys if key in item]


def validate_code_test_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    public_tests = list(draft.get("publicTests") or [])
    hidden_tests = list(draft.get("hiddenTests") or [])
    tests = public_tests + hidden_tests
    code = draft.get("referenceSolutionPython")
    meta = draft.get("meta") if isinstance(draft.get("meta"), dict) else {}
    qg = meta.get("qualityGates") if isinstance(meta.get("qualityGates"), dict) else {}
    min_public = safe_int(qg.get("minPublicTests"), MIN_PUBLIC_TESTS)
    min_hidden = safe_int(qg.get("minHiddenTests"), MIN_HIDDEN_TESTS)
    min_total = safe_int(qg.get("minTotalTests"), max(MIN_TOTAL_TESTS, min_public + min_hidden))
    prefer_public_more = bool(qg.get("preferPublicTestsMoreThanHidden", True))
    expected_langs = [normalize_text(x).lower() for x in list(meta.get("expectedAllowedLanguages") or []) if normalize_text(x)]
    checks: List[Dict[str, Any]] = collect_quality_checks_common(draft)

    if "codePolicy" in draft:
        checks.append({"name": "code-policy-shape", "status": "failed", "details": "Используй root-level requiredCalls/forbiddenCalls, а не nested codePolicy"})
    else:
        checks.append({"name": "code-policy-shape", "status": "passed", "details": "Используются канонические root-level policy fields"})

    if len(public_tests) >= min_public:
        checks.append({"name": "public-tests-count", "status": "passed", "details": f"publicTests={len(public_tests)}"})
    else:
        checks.append({"name": "public-tests-count", "status": "failed", "details": f"Нужно минимум {min_public}, сейчас {len(public_tests)}"})
    if len(hidden_tests) >= min_hidden:
        checks.append({"name": "hidden-tests-count", "status": "passed", "details": f"hiddenTests={len(hidden_tests)}"})
    else:
        checks.append({"name": "hidden-tests-count", "status": "failed", "details": f"Нужно минимум {min_hidden}, сейчас {len(hidden_tests)}"})

    incompatible = []
    for idx, test in enumerate((t for t in public_tests + hidden_tests if isinstance(t, dict)), start=1):
        if _is_site_incompatible_test_input(test.get("input")):
            incompatible.append(idx)
    if incompatible:
        checks.append({"name": "site-compatible-inputs", "status": "failed", "details": f"Недопустимые тесты с вводом только из пробелов: {incompatible[:5]}"})
    else:
        checks.append({"name": "site-compatible-inputs", "status": "passed", "details": "Тесты совместимы с ограничениями ввода сайта"})
    if len(tests) >= min_total:
        checks.append({"name": "tests-total-count", "status": "passed", "details": f"tests={len(tests)}"})
    else:
        checks.append({"name": "tests-total-count", "status": "failed", "details": f"Нужно минимум {min_total} тестов суммарно, сейчас {len(tests)}"})
    if prefer_public_more:
        checks.append({
            "name": "public-vs-hidden-balance",
            "status": "passed" if len(public_tests) > len(hidden_tests) else "warning",
            "details": "publicTests > hiddenTests" if len(public_tests) > len(hidden_tests) else f"Предпочтительно publicTests > hiddenTests, сейчас {len(public_tests)} <= {len(hidden_tests)}"
        })

    if not tests:
        checks.append({"name": "tests", "status": "failed", "details": "publicTests/hiddenTests empty"})
    else:
        unique_pairs = {
            f"{normalize_text(t.get('input'))}|{normalize_text(t.get('expectedOutput'))}"
            for t in tests if isinstance(t, dict)
        }
        checks.append({
            "name": "tests-unique",
            "status": "passed" if len(unique_pairs) == len(tests) else "failed",
            "details": f"unique={len(unique_pairs)} total={len(tests)}"
        })
        public_pairs = {
            f"{normalize_text(t.get('input'))}|{normalize_text(t.get('expectedOutput'))}"
            for t in public_tests if isinstance(t, dict)
        }
        hidden_pairs = {
            f"{normalize_text(t.get('input'))}|{normalize_text(t.get('expectedOutput'))}"
            for t in hidden_tests if isinstance(t, dict)
        }
        overlap = public_pairs & hidden_pairs
        checks.append({
            "name": "hidden-vs-public-overlap",
            "status": "passed" if not overlap else "failed",
            "details": "hiddenTests не дублируют publicTests" if not overlap else f"Есть пересечения hidden/public: {len(overlap)}"
        })
        actual_langs = [normalize_text(x).lower() for x in list(draft.get("allowedLanguages") or []) if normalize_text(x)]
        if expected_langs:
            checks.append({
                "name": "allowedLanguages-match",
                "status": "passed" if actual_langs == expected_langs else "failed",
                "details": f"expected={expected_langs} actual={actual_langs}"
            })

    if isinstance(code, str) and normalize_text(code):
        if "solve" in code or "main" in code.lower():
            checks.append({"name": "reference-solution-shape", "status": "passed", "details": "Есть solve/main"})
        else:
            checks.append({"name": "reference-solution-shape", "status": "failed", "details": "Нет явного solve/main"})
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
            timed_out = bool(res.get("timedOut"))
            crashed = res.get("returncode") not in {0, None}
            ok = res.get("returncode") == 0 and not timed_out and actual == expected
            if timed_out:
                details = f"timeout after sandbox limit; expected={expected!r}; actual={actual!r}; stderr={truncate_text(res.get('stderr') or '', 200)!r}"
            else:
                details = (
                    f"expected={expected!r}; actual={actual!r}; "
                    f"rc={res.get('returncode')} signal={res.get('signal')} sandboxed={res.get('sandboxed')} mode={res.get('sandboxMode')} stderr={truncate_text(res.get('stderr') or '', 200)!r}"
                )
            checks.append({
                "name": f"test-{idx}",
                "status": "passed" if ok else "failed",
                "details": details,
            })
            if timed_out:
                checks.append({"name": f"test-{idx}-runtime-timeout", "status": "failed", "details": "referenceSolution превысил sandbox timeout"})
            elif crashed:
                checks.append({"name": f"test-{idx}-runtime-exit", "status": "failed", "details": f"process exited with rc={res.get('returncode')} signal={res.get('signal')} mode={res.get('sandboxMode')}"})
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
    if "allowedLanguages" in draft:
        checks.append({"name": "allowedLanguages", "status": "failed", "details": "Math draft не должен содержать allowedLanguages"})
    if not isinstance(draft.get("settings"), dict):
        checks.append({"name": "settings", "status": "failed", "details": "settings отсутствует или не объект"})
    else:
        settings = draft.get("settings") or {}
        missing = [key for key in ["maxAttempts", "passPercent", "shuffleBlocks", "allowReview", "attemptTimeLimitsSeconds"] if key not in settings]
        checks.append({"name": "settings-shape", "status": "passed" if not missing else "failed", "details": "settings canonical" if not missing else f"missing settings fields: {missing}"})
    if not blocks:
        checks.append({"name": "blocks", "status": "failed", "details": "no blocks"})
        return {"status": summarize_status(checks), "summary": "Math draft пустой.", "score": 0.0, "checks": checks}
    checks.append({"name": "blocks-count", "status": "passed" if len(blocks) >= 2 else "failed", "details": f"blocks={len(blocks)}"})
    answer_blocks = 0
    legacy_keys = ["blockType", "title", "points", "promptContent", "answers", "correctAnswers", "items", "steps", "leftItems", "rightItems", "pairs"]
    for idx, block in enumerate(blocks, start=1):
        kind = str(block.get("kind") or "").strip().lower()
        title = str(block.get("prompt") or f"Блок {idx}")
        ok = True
        details = "структура выглядит валидно"
        found_legacy = _contains_any_keys(block, legacy_keys)
        if found_legacy:
            ok = False
            details = f"legacy alias keys not allowed: {found_legacy}"
        elif not normalize_text(block.get("prompt") or ""):
            ok = False
            details = "нет prompt"
        elif kind not in {"info", "number", "expression", "set", "single-choice", "multi-choice", "order", "match"}:
            ok = False
            details = f"unsupported kind={kind!r}"
        elif block.get("score") is None or block.get("isRequired") is None:
            ok = False
            details = "score и isRequired обязательны"
        elif kind in {"number", "expression", "set"}:
            answer_blocks += 1
            answers = block.get("acceptedAnswers") or []
            if not answers:
                ok = False
                details = "нет acceptedAnswers"
            elif block.get("caseSensitive") is None or block.get("trim") is None:
                ok = False
                details = "для answer blocks нужны caseSensitive и trim"
            elif kind == "number" and any(safe_eval_number(str(x)) is None for x in answers):
                ok = False
                details = "acceptedAnswers не парсятся как числа/выражения"
        elif kind in {"single-choice", "multi-choice"}:
            answer_blocks += 1
            options = list(block.get("options") or [])
            correct = list(block.get("correctOptionKeys") or [])
            option_keys = {str(x.get("key")) for x in options if isinstance(x, dict)}
            if len(options) < 2 or not correct or any(str(x) not in option_keys for x in correct):
                ok = False
                details = "для choice нужны минимум 2 options и корректные correctOptionKeys"
        elif kind == "order":
            answer_blocks += 1
            items = block.get("orderItems") or []
            if len(items) < 2:
                ok = False
                details = "для order нужно минимум 2 элемента"
        elif kind == "match":
            answer_blocks += 1
            left_items = block.get("matchLeftItems") or []
            right_items = block.get("matchRightItems") or []
            pairs = block.get("matchPairs") or []
            if not (left_items and right_items and pairs):
                ok = False
                details = "для match нужны matchLeftItems/matchRightItems/matchPairs"
        checks.append({"name": title, "status": "passed" if ok else "failed", "details": details})
    checks.append({"name": "answer-blocks", "status": "passed" if answer_blocks > 0 else "failed", "details": f"answerBlocks={answer_blocks}"})
    status = summarize_status(checks)
    score = sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks))
    return {"status": status, "summary": f"Math self-check: blocks={len(blocks)}, answerBlocks={answer_blocks}.", "score": score, "checks": checks}


def validate_test_draft(draft: Dict[str, Any]) -> Dict[str, Any]:
    questions = list(draft.get("questions") or [])
    checks: List[Dict[str, Any]] = collect_quality_checks_common(draft)
    if "allowedLanguages" in draft:
        checks.append({"name": "allowedLanguages", "status": "failed", "details": "Test draft не должен содержать allowedLanguages"})
    if not isinstance(draft.get("settings"), dict):
        checks.append({"name": "settings", "status": "failed", "details": "settings отсутствует или не объект"})
    else:
        settings = draft.get("settings") or {}
        missing = [key for key in ["maxAttempts", "passPercent", "shuffleQuestions", "shuffleAnswers", "allowReview", "attemptTimeLimitsSeconds"] if key not in settings]
        checks.append({"name": "settings-shape", "status": "passed" if not missing else "failed", "details": "settings canonical" if not missing else f"missing settings fields: {missing}"})
    if len(questions) >= 5:
        checks.append({"name": "questions-count", "status": "passed", "details": f"questions={len(questions)}"})
    elif questions:
        checks.append({"name": "questions-count", "status": "failed", "details": f"Нужно минимум 5, сейчас {len(questions)}"})
    else:
        checks.append({"name": "questions-count", "status": "failed", "details": "no questions"})
        return {"status": summarize_status(checks), "summary": "Test draft пустой.", "score": 0.0, "checks": checks}
    seen_prompts: set = set()
    duplicate_prompts = 0
    legacy_keys = ["questionType", "title", "correctKeys", "correct", "answers", "correctAnswers"]
    for idx, q in enumerate(questions, start=1):
        qtype = str(q.get("type") or "").strip().lower()
        title = str(q.get("prompt") or f"Вопрос {idx}")
        ok = True
        details = "структура выглядит валидно"
        found_legacy = _contains_any_keys(q, legacy_keys)
        normalized_prompt = normalize_text(q.get("prompt") or "")
        if found_legacy:
            ok = False
            details = f"legacy alias keys not allowed: {found_legacy}"
        elif not normalized_prompt:
            ok = False
            details = "нет prompt"
        elif normalized_prompt in seen_prompts:
            duplicate_prompts += 1
        else:
            seen_prompts.add(normalized_prompt)
        if qtype in {"single-choice", "multi-choice"}:
            options = list(q.get("options") or [])
            correct = list(q.get("correctOptionKeys") or [])
            option_keys = {str(x.get("key")) for x in options if isinstance(x, dict)}
            if len(options) < 2 or not correct or any(str(x) not in option_keys for x in correct):
                ok = False
                details = "для choice нужны минимум 2 options и корректные correctOptionKeys"
        elif qtype in {"fill", "text"}:
            answers = q.get("acceptedAnswers") or []
            has_case_sensitive = q.get("caseSensitive") is not None
            has_trim = q.get("trim") is not None
            if not answers:
                ok = False
                details = "нет acceptedAnswers"
            elif not has_case_sensitive or not has_trim:
                ok = False
                details = "для fill/text нужны caseSensitive и trim"
        else:
            ok = False
            details = f"unsupported type={qtype!r}"
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
