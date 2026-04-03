"""Batch-level pipeline stages: batch review, student journey,
publish preparation, planner feedback.

BUG-FIX: ``summarize_gate_status`` was used in ``run_batch_publish_prepare``
but defined 200 lines later in the original monolith.  Now imported up-front
from ``validators``.

BUG-FIX: ``int()`` calls on ``overallScore`` / ``difficultyTarget`` replaced
with ``safe_int()`` to survive float-strings and ``None``.

BUG-FIX: ``run_batch_review`` used ``isinstance(…, int)`` for ``overallScore``
which silently skips float scores.  Changed to ``safe_int`` + explicit guard.
"""

import time
from typing import Any, Dict, List

from text_utils import normalize_text, safe_int
from validators import summarize_status, summarize_gate_status, build_findings_from_checks


# ── Batch coherence review ────────────────────────────

def run_batch_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    drafts = payload.get("batchPeerDrafts") if isinstance(payload.get("batchPeerDrafts"), list) else []
    batch_items = payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []
    titles: List[str] = []
    dup = 0
    difficulty_jumps = 0
    repeated_skills = 0
    weak_items = 0
    previous_difficulty = None
    seen_skills: set = set()

    for item in batch_items:
        if not isinstance(item, dict):
            continue
        diff = safe_int(item.get("difficultyTarget") or item.get("DifficultyTarget"), None)  # type: ignore[arg-type]
        if diff is not None:
            if previous_difficulty is not None and abs(diff - previous_difficulty) > 1:
                difficulty_jumps += 1
            previous_difficulty = diff
        skill = normalize_text(item.get("targetSkill") or item.get("TargetSkill"))
        if skill:
            if skill.lower() in seen_skills:
                repeated_skills += 1
            seen_skills.add(skill.lower())
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        overall = safe_int(scorecard.get("overallScore"), None)  # type: ignore[arg-type]
        if overall is not None and overall < 65:
            weak_items += 1

    for d in drafts:
        if isinstance(d, dict):
            t = normalize_text(d.get("title"))
            if t in titles and t:
                dup += 1
            titles.append(t)

    findings: List[Dict[str, Any]] = []
    checks: List[Dict[str, Any]] = []
    if dup:
        findings.append({
            "severity": "medium", "code": "batch.duplicate_titles",
            "message": "В batch есть повторяющиеся или слишком похожие заголовки.",
            "suggestedRepair": "Развести микроцели и названия задач.",
            "confidence": min(0.95, 0.4 + dup * 0.1),
        })
    if difficulty_jumps:
        findings.append({
            "severity": "medium" if difficulty_jumps == 1 else "high",
            "code": "batch.difficulty_jumps",
            "message": "В наборе есть резкие скачки сложности между соседними слотами.",
            "suggestedRepair": "Сгладить progression и переставить/переписать отдельные задачи.",
            "confidence": min(0.95, 0.5 + difficulty_jumps * 0.1),
        })
    if repeated_skills >= 2:
        findings.append({
            "severity": "medium", "code": "batch.skill_redundancy",
            "message": "Несколько batch items повторяют одну и ту же микроцель или skill-anchor.",
            "suggestedRepair": "Сместить targetSkill и microGoal у соседних items.",
            "confidence": 0.74,
        })
    if weak_items:
        findings.append({
            "severity": "high" if weak_items >= 2 else "medium",
            "code": "batch.weak_items",
            "message": "В batch есть слабые items по scorecard aggregation.",
            "suggestedRepair": "Сначала вылечить слабые items, потом повторить batch review.",
            "confidence": min(0.97, 0.55 + weak_items * 0.1),
        })

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
        "decisionSummary": {
            "duplicateTitles": dup, "draftCount": len(drafts),
            "difficultyJumps": difficulty_jumps, "weakItems": weak_items,
            "repeatedSkills": repeated_skills,
        },
    }


# ── Student journey review ────────────────────────────

def run_student_journey_review(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    batch_items = sorted(
        [x for x in (payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []) if isinstance(x, dict)],
        key=lambda x: safe_int(x.get("index") or x.get("Index"), 0),
    )
    findings: List[Dict[str, Any]] = []
    checks: List[Dict[str, Any]] = []
    transitions: List[Dict[str, Any]] = []
    abrupt = 0
    missing_bridges = 0
    repeated_adjacent = 0
    weak_prereq = 0

    for idx, item in enumerate(batch_items):
        if idx == 0:
            continue
        prev = batch_items[idx - 1]
        prev_diff = safe_int(prev.get("difficultyTarget") or prev.get("DifficultyTarget"), 0)
        current_diff = safe_int(item.get("difficultyTarget") or item.get("DifficultyTarget"), 0)
        prev_skill = normalize_text(prev.get("targetSkill") or prev.get("TargetSkill"))
        current_skill = normalize_text(item.get("targetSkill") or item.get("TargetSkill"))
        prev_scorecard = prev.get("scorecard") if isinstance(prev.get("scorecard"), dict) else {}
        prev_score = safe_int(prev_scorecard.get("overallScore"), None)  # type: ignore[arg-type]
        delta = current_diff - prev_diff
        risk = "ok"
        reasons: List[str] = []

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
            "fromIndex": safe_int(prev.get("index") or prev.get("Index"), idx),
            "toIndex": safe_int(item.get("index") or item.get("Index"), idx + 1),
            "fromSkill": prev_skill,
            "toSkill": current_skill,
            "difficultyDelta": delta,
            "risk": risk,
            "reasons": reasons,
        })

    if abrupt:
        findings.append({
            "severity": "high" if abrupt >= 2 else "medium",
            "code": "journey.difficulty_jump",
            "message": "У студента есть резкие скачки сложности между соседними задачами.",
            "suggestedRepair": "Добавить bridge-item или переписать соседние briefs/difficulty targets.",
            "confidence": min(0.97, 0.52 + abrupt * 0.1),
        })
    if weak_prereq:
        findings.append({
            "severity": "medium", "code": "journey.weak_prerequisite",
            "message": "Следующая задача опирается на слабую предыдущую опору по scorecard.",
            "suggestedRepair": "Сначала вылечить слабый item или упростить следующий slot.",
            "confidence": min(0.95, 0.48 + weak_prereq * 0.08),
        })
    if repeated_adjacent >= 2:
        findings.append({
            "severity": "medium", "code": "journey.repeated_anchor",
            "message": "Соседние задачи слишком долго держатся за одинаковый skill-anchor.",
            "suggestedRepair": "Развести соседние microGoals и добавить variation step.",
            "confidence": 0.73,
        })
    if missing_bridges:
        findings.append({
            "severity": "medium", "code": "journey.missing_bridge",
            "message": "В batch не хватает промежуточных мостиков между этапами обучения.",
            "suggestedRepair": "Вернуть planner/brief layer и вставить bridging item или сгладить progression.",
            "confidence": min(0.92, 0.45 + missing_bridges * 0.08),
        })

    checks.append({"name": "transition-bridges", "status": "failed" if missing_bridges >= 2 else ("warning" if missing_bridges else "passed"), "details": f"missingBridges={missing_bridges}"})
    checks.append({"name": "difficulty-progression", "status": "failed" if abrupt >= 2 else ("warning" if abrupt else "passed"), "details": f"abruptTransitions={abrupt}"})
    checks.append({"name": "prerequisite-health", "status": "warning" if weak_prereq else "passed", "details": f"weakPrerequisites={weak_prereq}"})
    checks.append({"name": "adjacent-variety", "status": "warning" if repeated_adjacent else "passed", "details": f"repeatedAdjacent={repeated_adjacent}"})

    status = summarize_status(checks)
    score = max(0.05, 0.96 - abrupt * 0.12 - weak_prereq * 0.08 - repeated_adjacent * 0.04 - missing_bridges * 0.06)
    journey_steps: List[Dict[str, Any]] = []
    for item in batch_items[:12]:
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        journey_steps.append({
            "index": safe_int(item.get("index") or item.get("Index"), 0),
            "targetSkill": normalize_text(item.get("targetSkill") or item.get("TargetSkill")),
            "difficultyTarget": safe_int(item.get("difficultyTarget") or item.get("DifficultyTarget"), 0),
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
        "decisionSummary": {
            "abruptTransitions": abrupt, "missingBridges": missing_bridges,
            "weakPrerequisites": weak_prereq, "repeatedAdjacent": repeated_adjacent,
        },
    }


# ── Batch publish preparation ─────────────────────────

def run_batch_publish_prepare(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    batch_items = [x for x in (payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []) if isinstance(x, dict)]
    batch_review = payload.get("batchReview") if isinstance(payload.get("batchReview"), dict) else {}
    student_journey = payload.get("studentJourney") if isinstance(payload.get("studentJourney"), dict) else {}

    item_scores: List[int] = []
    weak_items: List[Dict[str, Any]] = []
    for item in batch_items:
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        raw = scorecard.get("overallScore")
        if raw is not None:
            overall = safe_int(raw, None)  # type: ignore[arg-type]
            if overall is not None:
                item_scores.append(overall)
                if overall < 65:
                    weak_items.append({
                        "index": item.get("index") or item.get("Index"),
                        "targetSkill": normalize_text(item.get("targetSkill") or item.get("TargetSkill")),
                        "overallScore": overall,
                    })

    avg_score = round(sum(item_scores) / len(item_scores)) if item_scores else 0
    batch_review_score = safe_int(round(float(batch_review.get("score") or 0) * 100), 0)
    student_journey_score = safe_int(round(float(student_journey.get("score") or 0) * 100), 0)
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
    findings: List[Dict[str, Any]] = []
    if weak_items:
        findings.append({
            "severity": "high" if len(weak_items) >= 2 else "medium",
            "code": "publish.weak_items",
            "message": "Не все items проходят минимальный quality floor для публикации.",
            "suggestedRepair": "Сначала закрыть слабые items и только потом публиковать batch.",
            "confidence": min(0.98, 0.5 + len(weak_items) * 0.1),
        })
    if batch_review_score < 75:
        findings.append({
            "severity": "medium", "code": "publish.batch_coherence",
            "message": "Batch coherence ещё недостаточно сильный для уверенной публикации.",
            "suggestedRepair": "Повторить batch planner / brief routing для проблемных slots.",
            "confidence": 0.76,
        })
    if student_journey_score < 75:
        findings.append({
            "severity": "medium", "code": "publish.student_journey",
            "message": "Student journey всё ещё несёт риски пропусков или резких переходов.",
            "suggestedRepair": "Сгладить progression и вставить bridge-steps между задачами.",
            "confidence": 0.74,
        })

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
            "readyCandidateIndexes": [
                safe_int(x.get("index") or x.get("Index"), 0)
                for x in batch_items
                if isinstance(x, dict) and safe_int((x.get("scorecard") if isinstance(x.get("scorecard"), dict) else {}).get("overallScore"), 0) >= 80
            ],
            "blockedCandidateIndexes": [
                safe_int(x.get("index") or x.get("Index"), 0)
                for x in batch_items
                if isinstance(x, dict) and safe_int((x.get("scorecard") if isinstance(x.get("scorecard"), dict) else {}).get("overallScore"), 0) < 80
            ],
        },
        "decisionSummary": {
            "readiness": readiness, "highFindings": high_findings,
            "weakItems": len(weak_items), "itemsCount": len(batch_items),
        },
    }


# ── Planner feedback ─────────────────────────────────

def run_batch_planner_feedback(payload: Dict[str, Any], job: Dict[str, Any]) -> Dict[str, Any]:
    batch_items = sorted(
        [x for x in (payload.get("batchItems") if isinstance(payload.get("batchItems"), list) else []) if isinstance(x, dict)],
        key=lambda x: safe_int(x.get("index") or x.get("Index"), 0),
    )
    batch_review = payload.get("batchReview") if isinstance(payload.get("batchReview"), dict) else {}
    student_journey = payload.get("studentJourney") if isinstance(payload.get("studentJourney"), dict) else {}
    publication_audit = payload.get("publicationAudit") if isinstance(payload.get("publicationAudit"), dict) else {}
    publication_decision = publication_audit.get("publicationDecision") if isinstance(publication_audit.get("publicationDecision"), dict) else {}
    readiness = normalize_text(publication_decision.get("readiness") or "partial") or "partial"

    slot_recommendations: List[Dict[str, Any]] = []
    anti_patterns: List[Dict[str, Any]] = []
    planner_adjustments: List[Dict[str, Any]] = []
    auto_brief_repair_candidates = 0
    replan_suggested = False

    for item in batch_items:
        scorecard = item.get("scorecard") if isinstance(item.get("scorecard"), dict) else {}
        overall = safe_int(scorecard.get("overallScore"), None)  # type: ignore[arg-type]
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
                "index": safe_int(item.get("index") or item.get("Index"), 0),
                "targetSkill": normalize_text(item.get("targetSkill") or item.get("TargetSkill")),
                "overallScore": overall,
                "primaryRoute": primary_route,
                "targetAction": recommendation,
                "reason": f"score={overall}, route={primary_route}",
            })
            if "brief" in recommendation:
                auto_brief_repair_candidates += 1

    transitions = [
        x for x in (student_journey.get("transitions") if isinstance(student_journey.get("transitions"), list) else [])
        if isinstance(x, dict)
    ]
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
    findings: List[Dict[str, Any]] = []
    if auto_brief_repair_candidates:
        findings.append({
            "severity": "medium" if auto_brief_repair_candidates == 1 else "high",
            "code": "planner.auto_brief_repair",
            "message": "Часть slots лучше отправить назад в brief layer, а не пытаться чинить только draft-слой.",
            "suggestedRepair": "Запусти brief repair для слабых items с planner feedback context.",
            "confidence": min(0.98, 0.55 + auto_brief_repair_candidates * 0.08),
        })
    if replan_suggested:
        findings.append({
            "severity": "medium", "code": "planner.replan_signal",
            "message": "Batch-level progression signals подсказывают, что planner стоит переоценить на уровне связок между tasks.",
            "suggestedRepair": "Вернуть batch в planner/brief layer и сгладить risky transitions.",
            "confidence": 0.78,
        })

    score = max(
        0.05,
        min(
            0.99,
            (float(publication_audit.get("score") or 0.0) * 0.55)
            + (float(student_journey.get("score") or 0.0) * 0.20)
            + (0.20 if auto_brief_repair_candidates == 0 else 0.08)
            + (0.04 if not replan_suggested else 0.0),
        ),
    )
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
