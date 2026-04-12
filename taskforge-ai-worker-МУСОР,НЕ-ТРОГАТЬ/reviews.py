"""Individual review stages — structural, pedagogy, style, similarity,
runtime, test-strength, brief review, and batch-context review.

BUG-FIX: ``run_batch_context_review`` called bare ``jaccard()`` which was
never imported / defined in the original monolith.  Now imported from
``text_utils``.

BUG-FIX: several ``int()`` calls replaced with ``safe_int()`` to handle
float-strings and ``None`` gracefully.
"""

import json
import re
from typing import Any, Dict, List

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_TOTAL_TESTS, MIN_DESCRIPTION_LEN
from log import log
from text_utils import (
    normalize_text,
    truncate_text,
    has_html_markup,
    compute_text_similarity,
    jaccard,
    safe_int,
)
from validators import (
    collect_quality_checks_common,
    summarize_status,
    build_findings_from_checks,
    run_self_check,
)
from mutation import assess_mutation_strength
from payload import (
    compact_reference_assignments,
    detect_beginner_char_array_track,
)


# ── Structural review ────────────────────────────────

def run_structural_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    validation = run_self_check(draft)
    validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
    validation["findings"] = build_findings_from_checks(validation.get("checks") or [])
    return validation


# ── Pedagogy review (fallback / heuristic) ────────────

def fallback_pedagogy_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    description = normalize_text(draft.get("description"))
    checks: List[Dict[str, Any]] = []
    if len(description) >= 160:
        checks.append({"name": "pedagogy-length", "status": "passed", "details": f"description.len={len(description)}"})
    else:
        checks.append({"name": "pedagogy-length", "status": "failed", "details": "Слишком короткое объяснение для учебной задачи"})
    keyword_hits = sum(1 for w in ["встав", "удал", "поиск"] if w in description.lower())
    if keyword_hits > 1:
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


# ── Style review ──────────────────────────────────────

def run_style_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    description = normalize_text(draft.get("description"))
    refs = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    lengths = [
        len(normalize_text(r.get("descriptionSummary") or r.get("description")))
        for r in refs
        if isinstance(r, dict) and normalize_text(r.get("descriptionSummary") or r.get("description"))
    ]
    avg_len = int(sum(lengths) / len(lengths)) if lengths else 0
    checks: List[Dict[str, Any]] = []
    findings: List[Dict[str, Any]] = []
    score = 1.0

    checks.append({"name": "style-format", "status": "passed", "details": "description допускается в plain text; публикация нормализует его в TipTap"})

    if avg_len > 0:
        delta = abs(len(description) - avg_len)
        if delta <= max(120, avg_len // 2):
            checks.append({"name": "style-length-fit", "status": "passed", "details": f"description.len={len(description)}, course.avg={avg_len}"})
        else:
            checks.append({"name": "style-length-fit", "status": "warning", "details": f"description.len={len(description)}, course.avg={avg_len}"})
            findings.append({
                "severity": "warning", "code": "style-length-fit",
                "message": "Длина описания выбивается из привычного профиля курса.",
                "suggestedRepair": "Подровняй глубину и детализацию под средний стиль курса.",
                "confidence": 0.68,
            })
            score -= 0.12

    title = normalize_text(draft.get("title"))
    ref_titles = [normalize_text(r.get("title")) for r in refs if isinstance(r, dict) and normalize_text(r.get("title"))]
    if title and ref_titles and any(title.lower() == x.lower() for x in ref_titles):
        checks.append({"name": "style-title-originality", "status": "failed", "details": "title совпадает с reference title"})
        findings.append({
            "severity": "high", "code": "style-title-originality",
            "message": "Заголовок дословно совпадает с одним из referenceAssignments.",
            "suggestedRepair": "Перефразируй title и сдвинь акцент формулировки.",
            "confidence": 0.93,
        })
        score -= 0.35
    else:
        checks.append({"name": "style-title-originality", "status": "passed", "details": "title не совпадает дословно"})

    status = summarize_status(checks)
    return {
        "draftId": payload.get("draftId") or job.get("targetEntityId"),
        "status": status,
        "score": max(0.0, round(score, 2)),
        "summary": "Проверена стилистическая совместимость draft с course profile и referenceAssignments.",
        "checks": checks,
        "findings": findings,
        "topReferences": ref_titles[:5],
    }


# ── Similarity review ────────────────────────────────

def run_similarity_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    references = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    title = normalize_text(draft.get("title"))
    description = normalize_text(draft.get("description"))
    checks: List[Dict[str, Any]] = []
    scored: List[Dict[str, Any]] = []
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


# ── Runtime review ────────────────────────────────────

def run_runtime_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    validation = run_self_check(draft)
    checks = list(validation.get("checks") or [])
    assignment_type = str(draft.get("assignmentType") or "").strip().lower()

    if assignment_type == "code-test":
        runtime_failures = sum(
            1 for x in checks
            if str(x.get("name") or "").startswith("test-") and x.get("status") == "failed"
        )
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


# ── Test-strength review (with mutation) ─────────────

def run_test_strength_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    assignment_type = str(draft.get("assignmentType") or "").strip().lower()
    checks: List[Dict[str, Any]] = []
    findings: List[Dict[str, Any]] = []
    mutation_analysis = None

    if assignment_type == "code-test":
        public_tests = list(draft.get("publicTests") or [])
        hidden_tests = list(draft.get("hiddenTests") or [])
        all_tests = public_tests + hidden_tests
        sigs: set = set()
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
        checks.append({"name": "public-vs-hidden-balance", "status": "passed" if len(public_tests) > len(hidden_tests) else "warning", "details": f"public={len(public_tests)} hidden={len(hidden_tests)}"})
        if edge_hits > 0:
            checks.append({"name": "edge-case-presence", "status": "passed", "details": f"Найдено edge-like тестов: {edge_hits}"})
        else:
            checks.append({"name": "edge-case-presence", "status": "warning", "details": "Не видно явных edge cases"})
        if len(all_tests) >= max(MIN_TOTAL_TESTS, MIN_PUBLIC_TESTS + MIN_HIDDEN_TESTS):
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
        questions = [q for q in list(draft.get("questions") or []) if isinstance(q, dict)]
        checks.append({"name": "questions-count", "status": "passed" if len(questions) >= 5 else "failed", "details": f"questions={len(questions)}"})
        choice_like = 0
        text_like = 0
        broken = 0
        for q in questions:
            qtype = normalize_text(q.get("type")).lower()
            if qtype in ["single-choice", "multi-choice"]:
                choice_like += 1
                options = [x for x in list(q.get("options") or []) if isinstance(x, dict)]
                correct = [normalize_text(x) for x in list(q.get("correctOptionKeys") or []) if normalize_text(x)]
                option_keys = {normalize_text(x.get("key")) for x in options if isinstance(x, dict)}
                if len(options) < 2 or not correct or any(x not in option_keys for x in correct):
                    broken += 1
            elif qtype in ["fill", "text"]:
                text_like += 1
                answers = [normalize_text(x) for x in list(q.get("acceptedAnswers") or []) if normalize_text(x)]
                if not answers or q.get("caseSensitive") is None or q.get("trim") is None:
                    broken += 1
            else:
                broken += 1
        checks.append({"name": "question-shape", "status": "passed" if broken == 0 else "failed", "details": f"broken={broken}, choice={choice_like}, text={text_like}"})
    elif assignment_type == "math":
        blocks = [b for b in list(draft.get("blocks") or []) if isinstance(b, dict)]
        answer_kinds = {"single-choice", "multi-choice", "number", "expression", "set", "order", "match"}
        answer_blocks = sum(1 for b in blocks if normalize_text(b.get("kind")).lower() in answer_kinds)
        broken = 0
        for b in blocks:
            kind = normalize_text(b.get("kind")).lower()
            if kind == "number" and not list(b.get("acceptedAnswers") or []):
                broken += 1
            elif kind == "order" and not list(b.get("orderItems") or []):
                broken += 1
            elif kind == "match":
                if not list(b.get("matchLeftItems") or []) or not list(b.get("matchRightItems") or []) or not list(b.get("matchPairs") or []):
                    broken += 1
        checks.append({"name": "blocks-count", "status": "passed" if len(blocks) >= 2 else "failed", "details": f"blocks={len(blocks)}"})
        checks.append({"name": "answer-blocks", "status": "passed" if answer_blocks > 0 else "failed", "details": f"answerBlocks={answer_blocks}"})
        checks.append({"name": "block-shape", "status": "passed" if broken == 0 else "failed", "details": f"broken={broken}"})
    else:
        checks.append({"name": "unsupported-type", "status": "warning", "details": assignment_type or "unknown"})

    status = summarize_status(checks)
    findings.extend(build_findings_from_checks(checks))
    return {
        "draftId": payload.get("draftId") or job.get("targetEntityId"),
        "status": status,
        "summary": "Assignment strength review completed.",
        "score": sum(1 for x in checks if x.get("status") == "passed") / max(1, len(checks)),
        "checks": checks,
        "findings": findings,
        "mutationAnalysis": mutation_analysis,
    }


# ── Brief review ──────────────────────────────────────

def run_brief_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    findings: List[Dict[str, Any]] = []
    checks: List[Dict[str, Any]] = []
    title_hint = normalize_text(brief.get("titleHint"))
    generation_prompt = normalize_text(brief.get("generationPrompt"))
    source_text = normalize_text(brief.get("sourceText"))
    notes = normalize_text(brief.get("notes"))
    difficulty = safe_int(brief.get("difficultyTarget"), 0)
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

    broad_markers = ["и т.п", "и т.д", "всё", "несколько больших тем", "поиск +", "вставка +", "topic pack", "5 simple c++ tasks", "including"]
    broad = any(m in generation_prompt.lower() for m in broad_markers)
    if broad:
        findings.append({"severity": "high", "code": "brief.too_broad", "message": "Brief слишком широкий и размытый.", "suggestedRepair": "Сузь brief до одной учебной цели и одной основной операции."})
    checks.append({"name": "scope", "status": "failed" if broad else "passed", "details": "broad" if broad else "focused"})

    if difficulty < 1 or difficulty > 5:
        findings.append({"severity": "medium", "code": "brief.bad_difficulty", "message": "difficultyTarget вне диапазона 1..5.", "suggestedRepair": "Приведи difficultyTarget к диапазону 1..5."})
    checks.append({"name": "difficultyTarget", "status": "passed" if 1 <= difficulty <= 5 else "warning", "details": str(difficulty)})

    beginner_track = detect_beginner_char_array_track(payload)
    gp_lower = generation_prompt.lower()
    if beginner_track and any(x in gp_lower for x in ["fgets", "strcat", "strcpy", "strlen", "cstring", "scanf", "printf"]):
        findings.append({
            "severity": "high", "code": "brief.beginner_track_drift",
            "message": "Brief уехал в C-style IO/cstring, хотя нужен базовый beginner char[] track.",
            "suggestedRepair": "Верни brief к базовым операциям char[] через cin/cout и ручные циклы.",
        })
        checks.append({"name": "beginner-track-alignment", "status": "failed", "details": generation_prompt[:120]})
    else:
        checks.append({"name": "beginner-track-alignment", "status": "passed" if beginner_track else "warning", "details": "ok" if beginner_track else "n/a"})

    if source_text and source_text.strip().startswith('{\"type\"'):
        findings.append({
            "severity": "medium", "code": "brief.source_text_polluted",
            "message": "sourceText выглядит как подмешанный reference/doc fragment, а не user intent.",
            "suggestedRepair": "Верни в sourceText исходный пользовательский запрос или его чистую нормализацию.",
        })
        checks.append({"name": "sourceText-origin", "status": "warning", "details": "reference-fragment"})
    else:
        checks.append({"name": "sourceText-origin", "status": "passed", "details": "user-intent" if source_text else "missing"})

    skill_low = target_skill.casefold()
    prompt_tokens = [x for x in re.split(r"[^\wа-яА-Я]+", generation_prompt.lower()) if len(x) >= 4]
    token_set = set(prompt_tokens)
    anchor_keywords = {"char", "array", "strlen", "strcpy", "strcat", "symbol", "string", "ввод", "вывод"}
    if target_skill and not any(tok in skill_low for tok in token_set if tok in anchor_keywords):
        findings.append({
            "severity": "medium", "code": "brief.skill_prompt_mismatch",
            "message": "targetSkill и generationPrompt слабо согласованы.",
            "suggestedRepair": "Сделай targetSkill и generationPrompt про одну и ту же операцию.",
        })
        checks.append({"name": "skill-prompt-alignment", "status": "warning", "details": target_skill})
    else:
        checks.append({"name": "skill-prompt-alignment", "status": "passed", "details": target_skill or "missing"})

    if notes and len(notes) > 280:
        checks.append({"name": "notes-size", "status": "warning", "details": str(len(notes))})
    else:
        checks.append({"name": "notes-size", "status": "passed", "details": str(len(notes))})

    status = "passed"
    if any(f["severity"] == "high" for f in findings):
        status = "failed"
    elif findings or any(c.get("status") == "warning" for c in checks):
        status = "needs-review"

    score = max(
        0.0,
        1.0
        - 0.22 * len([f for f in findings if f["severity"] == "high"])
        - 0.08 * len([f for f in findings if f["severity"] != "high"])
        - 0.03 * len([c for c in checks if c.get("status") == "warning"]),
    )
    return {
        "status": status,
        "score": round(score, 3),
        "summary": "Brief review completed",
        "checks": checks,
        "findings": findings,
        "titleHint": title_hint,
    }


# ── Batch-context review ─────────────────────────────
# BUG-FIX: original code called ``jaccard(set_a, set_b)`` without importing it.

def run_batch_context_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
    refs = payload.get("batchPeerDrafts") if isinstance(payload.get("batchPeerDrafts"), list) else []
    item_ctx = payload.get("batchItemContext") if isinstance(payload.get("batchItemContext"), dict) else {}
    title = normalize_text(draft.get("title"))
    description = normalize_text(draft.get("description"))
    findings: List[Dict[str, Any]] = []
    checks: List[Dict[str, Any]] = []
    overlaps: List[Dict[str, Any]] = []
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
        findings.append({
            "severity": "high", "code": "batch_context.duplicate",
            "message": "Draft слишком похож на соседнюю задачу набора.",
            "suggestedRepair": "Измени микроцель, пример и тестовое ядро.",
            "confidence": overlaps[0]["score"],
        })
    elif len(overlaps) >= 2:
        findings.append({
            "severity": "medium", "code": "batch_context.cluster_overlap",
            "message": "Draft частично пересекается с несколькими соседними задачами набора.",
            "suggestedRepair": "Усиль отличие по навыку, формату или ограничениям.",
            "confidence": overlaps[0]["score"],
        })
    checks.append({
        "name": "peerOverlap",
        "status": "failed" if any(f["severity"] == "high" for f in findings) else ("warning" if findings else "passed"),
        "details": json.dumps(overlaps[:3], ensure_ascii=False),
    })

    target_skill = normalize_text(item_ctx.get("TargetSkill") or item_ctx.get("targetSkill"))
    difficulty_target = safe_int(item_ctx.get("DifficultyTarget") or item_ctx.get("difficultyTarget"), 0)
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
    return {
        "status": status,
        "score": round(max(0.0, score), 3),
        "summary": "Batch context review completed.",
        "checks": checks,
        "findings": findings,
        "topOverlaps": overlaps[:5],
        "batchItemContext": item_ctx,
    }
