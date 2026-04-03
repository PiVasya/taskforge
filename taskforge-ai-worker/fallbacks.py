"""Fallback result builders — invoked when Ollama is unavailable or when
a quick deterministic answer is acceptable.

BUG-FIX: ``fallback_result`` for ``assignment_brief_generate`` had the key
``decisionLog`` set twice in the same dict literal.  Python silently keeps
only the *last* value, so the first entry (with historical-priors note) was
always lost.  Merged into a single list.

BUG-FIX: ``int()`` calls replaced with ``safe_int()`` throughout.
"""

import json
import re
import time
from typing import Any, Dict, List

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_DESCRIPTION_LEN
from log import log
from text_utils import normalize_text, truncate_text, has_html_markup, safe_int, unique_string_list
from payload import (
    compact_reference_assignments,
    compact_historical_planner_priors,
    compact_historical_slot_priors,
    extract_historical_skill_biases,
)
from validators import run_self_check, attach_self_check
from reviews import (
    run_structural_review,
    fallback_pedagogy_review,
    run_style_review,
    run_similarity_review,
    run_runtime_review,
    run_brief_review,
)


# ── Fallback: course profile ─────────────────────────

def fallback_course_profile(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    refs = compact_reference_assignments(payload)
    skills: List[str] = []
    langs: Dict[str, int] = {}
    types: Dict[str, int] = {}
    for r in refs:
        title = normalize_text(r.get("title"))
        if title:
            skills.append(title[:80])
        for lang in (r.get("allowedLanguagesCsv") or "").split(","):
            lang = normalize_text(lang)
            if lang:
                langs[lang] = langs.get(lang, 0) + 1
        t = normalize_text(r.get("type"))
        if t:
            types[t] = types.get(t, 0) + 1
    avg_hidden = round(sum(safe_int(r.get("hiddenTestsCount"), 0) for r in refs) / len(refs), 2) if refs else 0
    return {
        "courseProfile": {
            "dominantSkills": skills[:8],
            "difficultyDistribution": {"approximate": True},
            "styleProfile": {"descriptionLength": "mixed", "htmlPreferred": True, "tone": "teaching"},
            "policyProfile": {"allowedLanguages": langs, "assignmentTypes": types},
            "testProfile": {
                "referenceCount": len(refs), "publicExamplesPerTask": "1-2",
                "hiddenTests": "usually-present", "hiddenTestsAvg": avg_hidden,
            },
            "assignmentOntology": {"coreSkillSeeds": skills[:12], "assignmentTypes": list(types.keys())},
            "exemplarSignals": {"goodPatterns": ["html-description", "examples", "hidden-tests"], "referenceCount": len(refs)},
            "negativePatterns": ["слишком широкая задача", "дословный дубль title", "слабые edge cases"],
        },
        "summary": f"Построен fallback course profile по {len(refs)} referenceAssignments.",
        "decisionSummary": {"profileSource": "fallback", "referenceCount": len(refs)},
    }


# ── Fallback: gap analysis ───────────────────────────

def fallback_gap_analysis(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    prompt = normalize_text(payload.get("prompt"))
    refs = compact_reference_assignments(payload)
    covered = [normalize_text(r.get("title")) for r in refs if normalize_text(r.get("title"))][:8]
    tokens = [x for x in re.split(r"[^\wа-яА-Я]+", prompt) if len(x) > 3]
    missing: List[str] = []
    for t in tokens:
        low = t.lower()
        if not any(low in c.lower() for c in covered):
            missing.append(t)
    seen: set = set()
    missing_unique: List[str] = []
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
        "decisionSummary": {"gapSource": "fallback", "missingTopicsCount": len(missing_unique)},
    }


# ── Fallback: plan tasks ─────────────────────────────

def build_fallback_plan_tasks(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    count = max(1, safe_int(payload.get("count"), 1))
    prompt = normalize_text(payload.get("prompt"))
    words = [x for x in re.split(r"[^\wа-яА-Я]+", prompt) if len(x) >= 4]
    unique_words: List[str] = []
    seen: set = set()
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
    tasks: List[Dict[str, Any]] = []
    base_difficulty = max(1, min(5, safe_int(payload.get("difficulty"), 2)))
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


# ── Fallback: brief repair ───────────────────────────

def _fallback_brief_repair(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    review = payload.get("briefReview") if isinstance(payload.get("briefReview"), dict) else {}
    scorecard = payload.get("scorecard") if isinstance(payload.get("scorecard"), dict) else {}
    repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
    review_results = payload.get("reviewResults") if isinstance(payload.get("reviewResults"), list) else []
    target_skill = brief.get("targetSkill") or payload.get("targetSkill") or "task"
    summary = brief.get("summary") or brief.get("generationPrompt") or ""
    route = normalize_text(repair_plan.get("primaryRoute") or "brief") or "brief"
    route_notes: List[str] = []
    if route == "brief":
        route_notes.append("Смести микроцель, чтобы задача не дублировала соседние drafts и referenceAssignments.")
    if route == "description":
        route_notes.append("Сузь brief до одной ясной учебной цели и более конкретного IO-контракта.")
    if route == "tests":
        route_notes.append("Предусмотри edge cases и более сильную тестовую идею уже на уровне brief.")
    findings_notes: List[str] = []
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
        "difficultyTarget": safe_int(brief.get("difficultyTarget"), 2),
        "targetSkill": str(target_skill),
        "decisionLog": [{
            "stage": "brief_repair",
            "message": f"Brief repaired after review: {normalize_text(review.get('summary'))}",
            "route": route,
            "scoreBand": scorecard.get("band"),
        }],
    }


# ── Unified fallback dispatcher ──────────────────────

def fallback_result(job: Dict[str, Any]) -> Dict[str, Any]:
    from payload import parse_payload  # late import to avoid circular

    log("building fallback result", {"jobId": job.get("id"), "type": job.get("type")})
    payload = parse_payload(job)
    t = (job.get("type") or "").lower().strip()

    # ── generation ────────────────────────────────────
    if t.startswith("assignment_generate"):
        assignment_type = str(payload.get("assignmentType") or "math").strip().lower()
        if assignment_type == "code-test":
            draft = {
                "assignmentType": "code-test",
                "title": "AI fallback: сумма чисел от 1 до n",
                "description": (
                    "<p>По данному целому числу <strong>n</strong> требуется вычислить сумму "
                    "всех целых чисел от 1 до n включительно.</p>"
                    "<p><strong>Входные данные:</strong> одно целое число n.</p>"
                    "<p><strong>Выходные данные:</strong> одно число — искомая сумма.</p>"
                    "<p><strong>Ограничения:</strong> 1 ≤ n ≤ 10^6.</p>"
                ),
                "courseId": job.get("courseId"),
                "difficulty": 2,
                "rating": 1,
                "tags": "ai,fallback,code",
                "allowedLanguages": ["python", "cpp", "csharp"],
                "publicTests": [
                    {"input": "2\n", "expectedOutput": "3"},
                    {"input": "5\n", "expectedOutput": "15"},
                ],
                "hiddenTests": [
                    {"input": "1\n", "expectedOutput": "1"},
                    {"input": "3\n", "expectedOutput": "6"},
                    {"input": "10\n", "expectedOutput": "55"},
                    {"input": "100\n", "expectedOutput": "5050"},
                    {"input": "1000\n", "expectedOutput": "500500"},
                ],
                "referenceSolutionPython": (
                    "import sys\n\n"
                    "def solve(data: str) -> str:\n"
                    "    n = int(data.strip())\n"
                    "    return str(n * (n + 1) // 2)\n\n"
                    "if __name__ == '__main__':\n"
                    "    print(solve(sys.stdin.read()))"
                ),
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
                "difficulty": 2, "rating": 1, "tags": "ai,fallback,test",
                "settings": {"maxAttempts": 3, "passPercent": 60, "shuffleQuestions": True, "shuffleAnswers": True, "allowReview": True},
                "questions": [
                    {"type": "text", "prompt": "Введите ok", "acceptedAnswers": ["ok"], "trim": True, "caseSensitive": False},
                    {"type": "single-choice", "prompt": "Сколько будет 2+2?", "options": [{"key": "a", "text": "4"}, {"key": "b", "text": "5"}], "correctOptionKeys": ["a"]},
                    {"type": "fill", "prompt": "Продолжите последовательность: 2, 4, 6, __", "acceptedAnswers": ["8"]},
                    {"type": "single-choice", "prompt": "Какая буква первая в алфавите?", "options": [{"key": "a", "text": "A"}, {"key": "b", "text": "B"}], "correctOptionKeys": ["a"]},
                    {"type": "text", "prompt": "Введите слово test", "acceptedAnswers": ["test"]},
                ],
            }
            return attach_self_check({"draft": draft}, draft, run_self_check(draft))
        # math (default)
        draft = {
            "assignmentType": "math",
            "title": "AI fallback: простая математическая задача",
            "description": "<p>Решите предложенную задачу и введите ответы в блоки.</p>",
            "courseId": job.get("courseId"),
            "difficulty": 2, "rating": 1, "tags": "ai,fallback,math",
            "settings": {"maxAttempts": 3, "passPercent": 60, "shuffleBlocks": False, "allowReview": True},
            "blocks": [
                {"blockType": "info", "title": "Условие", "prompt": "Найдите значение выражения 2 + 2.", "points": 0},
                {"blockType": "number", "title": "Ответ", "prompt": "Введите ответ", "acceptedAnswers": ["4"], "points": 1},
            ],
        }
        return attach_self_check({"draft": draft}, draft, run_self_check(draft))

    # ── profiling/gap ─────────────────────────────────
    if t == "assignment_course_profile_build":
        return fallback_course_profile(payload, job)
    if t == "assignment_gap_analysis":
        return fallback_gap_analysis(payload, job)

    # ── batch plan / replan ───────────────────────────
    if t in {"assignment_batch_plan", "assignment_batch_replan"}:
        count = max(1, safe_int(payload.get("count"), 1))
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
            "plan": {"tasks": tasks},
        }

    # ── reference pack ────────────────────────────────
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

    # ── batch review ──────────────────────────────────
    if t == "assignment_batch_review":
        return {
            "status": "passed", "score": 0.72,
            "summary": "Fallback batch review built.",
            "checks": [{"name": "coherence", "status": "passed", "details": "fallback"}],
            "findings": [], "progression": {"status": "ok"}, "coverage": {"status": "ok"},
            "decisionSummary": {"mode": "fallback"},
        }

    # ── brief generate ────────────────────────────────
    # BUG-FIX: merged duplicate ``decisionLog`` keys into one list
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
            "difficultyTarget": safe_int(task.get("difficultyTarget"), 2),
            "targetSkill": str(skill),
            # BUG-FIX: was two separate ``decisionLog`` keys — only last survived
            "decisionLog": [
                {"stage": "brief_generate", "message": "Fallback brief создан с учётом historical planner priors."},
                {"stage": "brief_generate", "message": f"Brief создан для навыка {skill}"},
            ],
        }

    # ── brief review ──────────────────────────────────
    if t == "assignment_brief_review":
        return run_brief_review(payload, job)

    # ── brief repair ──────────────────────────────────
    if t == "assignment_brief_repair":
        return _fallback_brief_repair(payload, job)

    # ── validate draft ────────────────────────────────
    if t == "assignment_validate_draft":
        draft = payload.get("draft") if isinstance(payload.get("draft"), dict) else {}
        validation = run_self_check(draft)
        validation["draftId"] = payload.get("draftId") or job.get("targetEntityId")
        return validation

    # ── per-stage reviews (deterministic) ─────────────
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

    # ── draft repair ──────────────────────────────────
    if t == "assignment_repair":
        from repair import fallback_repair_result
        return fallback_repair_result(payload, job)

    # ── misc ──────────────────────────────────────────
    if t == "assignment_analyze_existing":
        return {
            "assignmentId": job.get("targetEntityId"), "kind": "quality-audit",
            "summary": "Fallback-анализ: модель недоступна.",
            "suggestions": ["Проверь ясность формулировки.", "Проверь баланс сложности."],
        }
    if t == "submission_review":
        return {
            "assignmentId": job.get("targetEntityId"), "verdict": "needs-review",
            "score": 0.5, "summary": "Fallback-review: нужна ручная проверка.",
            "signals": [{"code": "fallback_review", "weight": 0.5, "note": "model unavailable"}],
        }
    if t == "user_risk_review":
        return {
            "userId": job.get("targetEntityId"), "riskLevel": "medium",
            "score": 0.4, "summary": "Fallback-risk-report: выполните ручную проверку.",
            "signals": [{"code": "fallback_risk", "weight": 0.4, "note": "model unavailable"}],
        }
    return {"summary": "Нейросеть недоступна, job завершён fallback-ответом."}
