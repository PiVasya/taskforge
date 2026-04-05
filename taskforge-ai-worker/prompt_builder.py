"""Prompt construction for every job type.

BUG-FIX: ``build_job_specific_instructions`` previously returned a *dict*
for several job types (batch_plan, reference_pack_build, etc.).  Since
that function is only consumed as a *string* inside ``build_prompt`` /
``build_repair_prompt``, the dict was silently repr()'d into the prompt —
producing garbage.  Now it always returns ``str``.

BUG-FIX: the ``assignment_brief_review`` branch referenced an undefined
local ``job``; removed — brief-review is dispatched before we ever reach
the generic prompt path.
"""

import json
import re
from typing import Any, Dict, List

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_DESCRIPTION_LEN, MAX_HIDDEN_TESTS
from log import log
from text_utils import normalize_text, truncate_text, safe_int, unique_string_list, strip_html_to_text, summarize_description
from payload import (
    compact_reference_assignments,
    compact_historical_planner_priors,
    compact_historical_slot_priors,
    compact_payload_for_stage,
    detect_beginner_char_array_track,
    extract_historical_skill_biases,
    files_text,
)


def _prompt_json(value: Dict[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _collect_reference_buckets(payload: Dict[str, Any]) -> Dict[str, List[Dict[str, Any]]]:
    refs = compact_reference_assignments(payload, limit=10, description_len=140, include_cases=True)
    if not refs:
        return {"styleAnchors": [], "difficultyAnchors": [], "topicAnchors": [], "negativeAnchors": []}
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    target_skill = normalize_text(task.get("targetSkill") or task.get("TargetSkill") or brief.get("targetSkill") or brief.get("titleHint") or "")
    difficulty_target = safe_int(task.get("difficultyTarget") or task.get("DifficultyTarget") or brief.get("difficultyTarget") or payload.get("difficulty"), 2)
    style_anchors = refs[:3]
    difficulty_anchors = [r for r in refs if safe_int(r.get("difficulty"), difficulty_target) == difficulty_target][:3] or refs[:2]
    def _topic_score(item: Dict[str, Any]) -> int:
        hay = normalize_text(f"{item.get('title') or ''} {item.get('descriptionSummary') or ''} {item.get('tags') or ''}").lower()
        if not target_skill:
            return 0
        return sum(1 for token in re.split(r"[^\wа-яА-Я]+", target_skill.lower()) if len(token) >= 3 and token in hay)
    topic_anchors = sorted(refs, key=_topic_score, reverse=True)[:3]
    negative_anchors = refs[-2:] if len(refs) >= 2 else refs[:1]
    return {"styleAnchors": style_anchors, "difficultyAnchors": difficulty_anchors, "topicAnchors": topic_anchors, "negativeAnchors": negative_anchors}


def _extract_course_phrase_bank(payload: Dict[str, Any], limit: int = 10) -> Dict[str, List[str]]:
    refs = payload.get("referenceAssignments") if isinstance(payload.get("referenceAssignments"), list) else []
    intro, input_lines, output_lines, constraints = [], [], [], []
    for item in refs[:10]:
        if not isinstance(item, dict):
            continue
        desc = strip_html_to_text(item.get("description") or item.get("Description") or "")
        parts = [x.strip() for x in re.split(r"\n+", desc) if x.strip()]
        if parts:
            intro.append(parts[0][:140])
        for part in parts[1:8]:
            low = part.lower()
            if ("вход" in low or "input" in low) and len(input_lines) < limit:
                input_lines.append(part[:140])
            if ("выход" in low or "output" in low) and len(output_lines) < limit:
                output_lines.append(part[:140])
            if ("огранич" in low or "constraint" in low) and len(constraints) < limit:
                constraints.append(part[:140])
    return {
        "introPhrases": unique_string_list(intro, limit),
        "inputPhrases": unique_string_list(input_lines, limit),
        "outputPhrases": unique_string_list(output_lines, limit),
        "constraintPhrases": unique_string_list(constraints, limit),
    }


def _compact_peer_context(payload: Dict[str, Any], limit: int = 6) -> List[Dict[str, Any]]:
    peers = payload.get("batchPeerItems") if isinstance(payload.get("batchPeerItems"), list) else []
    compact_peers = []
    for peer in peers[:limit]:
        if not isinstance(peer, dict):
            continue
        compact_peers.append({
            "index": peer.get("index") or peer.get("Index"),
            "targetSkill": truncate_text(peer.get("targetSkill") or peer.get("TargetSkill"), 140),
            "microGoal": truncate_text(peer.get("microGoal") or peer.get("MicroGoal"), 180),
            "status": normalize_text(peer.get("status") or peer.get("Status")),
        })
    return compact_peers


# ── Generation requirements (code-test / test / math) ────────

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
        "description пиши как обычный человекочитаемый текст без HTML-тегов; секции разделяй пустыми строками.",
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
            "Не придумывай security-ограничения вроде Process.Start, __import__, os.system и т.п.: платформенная защита добавляется отдельно. requiredCalls/forbiddenCalls заполняй только если это явно требуется учебной постановкой.",
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


# ── Job-type specific instructions (always returns str) ───────

def build_job_specific_instructions(job_type: str, payload: Dict[str, Any]) -> str:
    """Return a *string* instruction block for the given *job_type*.

    Previously this function returned a ``dict`` for several job types,
    which was silently stringified into the LLM prompt producing garbage.
    Now every branch returns a human-readable instruction string.
    """
    t = (job_type or "").lower().strip()

    if t.startswith("assignment_generate"):
        return build_generation_requirements(payload)

    if t == "assignment_course_profile_build":
        return (
            "Построй профиль курса по referenceAssignments. "
            "Верни JSON: {\"courseProfile\":{...},\"summary\":\"...\",\"decisionSummary\":{...}}."
        )

    if t == "assignment_gap_analysis":
        return (
            "Проанализируй пробелы курса. "
            "Верни JSON: {\"gapAnalysis\":{...},\"coverage\":{...},\"summary\":\"...\",\"decisionSummary\":{...}}."
        )

    if t in {"assignment_batch_plan", "assignment_batch_replan"}:
        count = max(1, safe_int(payload.get("count"), 1))
        return (
            f"Построй план пакета из {count} задач. "
            "Верни JSON: {\"canonicalRequest\":{...},\"coverage\":{...},\"summary\":\"...\","
            "\"decisionSummary\":{...},\"plan\":{\"tasks\":[...]}}."
        )

    if t == "assignment_reference_pack_build":
        return (
            "Собери compact reference pack для одной будущей задачи. "
            "Верни JSON: {\"stylePack\":{...},\"policyPack\":{...},\"negativePack\":{...},"
            "\"exemplarPack\":{...},\"signals\":{...},\"generationHints\":{...},\"decisionLog\":[...]}."
        )

    if t == "assignment_batch_review":
        return (
            "Оцени пакет задач как учебную систему. "
            "Верни JSON: {\"status\":\"...\",\"score\":0.0,\"summary\":\"...\",\"checks\":[...],\"findings\":[...]}."
        )

    if t == "assignment_brief_generate":
        task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
        skill = (
            task.get("TargetSkill") or task.get("targetSkill")
            or f"task-{task.get('Index') or task.get('index') or 1}"
        )
        return (
            f"Напиши brief для одной задачи (targetSkill={skill}). "
            "Верни JSON: {\"titleHint\":\"...\",\"summary\":\"...\",\"generationPrompt\":\"...\","
            "\"sourceText\":\"...\",\"notes\":\"...\",\"difficultyTarget\":2,\"targetSkill\":\"...\","
            "\"decisionLog\":[...]}."
        )

    # NOTE: assignment_brief_review is dispatched before reaching generic
    # prompt path, so no branch here (was a bug: referenced undefined `job`).

    if t == "assignment_brief_repair":
        return (
            "Исправь brief по findings и reviewResults. "
            "Верни JSON со всеми полями brief, обновив generationPrompt и notes."
        )

    if t == "assignment_validate_draft":
        return (
            "Проверь существующий draft максимально строго. "
            "Верни только JSON вида: "
            "{\"draftId\":\"...\",\"status\":\"passed|failed|needs-review\","
            "\"summary\":\"...\",\"score\":0.0,"
            "\"checks\":[{\"name\":\"...\",\"status\":\"passed|failed|warning\",\"details\":\"...\"}]}"
        )

    if t == "assignment_analyze_existing":
        return (
            "Проанализируй существующее задание и верни JSON: "
            "{\"assignmentId\":\"...\",\"kind\":\"quality-audit\","
            "\"summary\":\"...\",\"suggestions\":[\"...\",\"...\"]}"
        )

    if t == "assignment_repair":
        return (
            "Исправь существующий draft по findings и reviewResults. "
            "Верни только JSON вида {\"draft\": {...}, \"repairSummary\": \"...\"}. "
            "Не придумывай новую тему без причины: сохрани ядро задания, "
            "но исправь формулировку, тесты, solution и policy там, где review нашёл проблемы."
        )

    if t == "submission_review":
        return (
            "Верни JSON review попытки: "
            "{\"assignmentId\":\"...\",\"userId\":\"...\",\"sourceType\":\"code|test|math|image\","
            "\"sourceAttemptId\":\"...\",\"verdict\":\"ok|needs-review|suspicious\","
            "\"score\":0.0,\"summary\":\"...\","
            "\"signals\":[{\"code\":\"...\",\"weight\":0.2,\"note\":\"...\"}]}"
        )

    if t == "user_risk_review":
        return (
            "Верни JSON risk-review пользователя: "
            "{\"userId\":\"...\",\"riskLevel\":\"low|medium|high\","
            "\"score\":0.0,\"summary\":\"...\","
            "\"signals\":[{\"code\":\"...\",\"weight\":0.2,\"note\":\"...\"}]}"
        )

    return "Верни только валидный JSON по задаче."


# ── Generic / repair prompts ─────────────────────────────────

def build_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    payload_for_prompt = dict(payload)
    payload_for_prompt["count"] = min(safe_int(payload.get("count"), 1), 1)
    payload_for_prompt["referenceAssignments"] = compact_reference_assignments(payload)
    prompt_payload = json.dumps(payload_for_prompt, ensure_ascii=False, indent=2)
    return (
        "Ты — TaskForge AI. Возвращай только валидный JSON без markdown.\n\n"
        f"Тип job: {job.get('type')}\n"
        f"Target entity type: {job.get('targetEntityType') or '-'}\n"
        f"Target entity id: {job.get('targetEntityId') or '-'}\n"
        f"Course id: {job.get('courseId') or '-'}\n\n"
        "Изучи referenceAssignments как примеры стиля и структуры, "
        "но не копируй формулировки и тесты дословно.\n"
        f"{build_job_specific_instructions(job.get('type') or '', payload)}\n\n"
        "referenceAssignments уже сжаты и отсортированы backend'ом по релевантности. "
        "Если их нет, всё равно сгенерируй полноценный draft по qualityGates.\n\n"
        f"Payload:\n{prompt_payload}\n\n"
        f"Files:\n{files_text(job)}"
    )


def build_repair_prompt(
    job: Dict[str, Any],
    payload: Dict[str, Any],
    bad_result: Dict[str, Any],
    validation: Dict[str, Any],
    attempt_no: int,
) -> str:
    draft = bad_result.get("draft") if isinstance(bad_result.get("draft"), dict) else {}
    compact_payload = dict(payload)
    compact_payload["referenceAssignments"] = compact_reference_assignments(payload)
    return (
        "Ты — TaskForge AI. Сейчас нужно исправить неудачный draft. "
        "Верни только валидный JSON вида {\"draft\": {...}} без markdown.\n\n"
        f"Тип job: {job.get('type')}\n"
        f"Попытка исправления: {attempt_no}\n\n"
        f"Требования:\n{build_job_specific_instructions(job.get('type') or '', payload)}\n\n"
        f"Изначальный payload:\n{_prompt_json(compact_payload)}\n\n"
        f"Плохой draft, который нужно переписать:\n{json.dumps(draft, ensure_ascii=False, indent=2)}\n\n"
        f"Ошибки self-check:\n{json.dumps(validation, ensure_ascii=False, indent=2)}\n\n"
        "Исправь все замечания, усили условие, добавь недостающие тесты и верни полностью новый готовый draft."
    )


# ── Stage-specific prompts ───────────────────────────────────

def build_course_profile_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage(job.get("type") or "", payload)
    return (
        "Ты — TaskForge AI request normalizer and course profiler. Верни только один валидный JSON-объект без markdown и без пояснений.\n\n"
        "Твоя задача не писать длинний анализ, а подготовить короткий usable digest для следующих стадий. "
        "Нужен компактный, строгий, практический output.\n\n"
        "Верни JSON строго этой формы:\n"
        "{\n"
        "  \"canonicalRequest\": {\n"
        "    \"domain\": \"...\",\n"
        "    \"assignmentType\": \"code-test|math|test\",\n"
        "    \"mode\": \"topic-pack|...\",\n"
        "    \"count\": 2,\n"
        "    \"difficulty\": 3,\n"
        "    \"mustInclude\": [\"...\"],\n"
        "    \"avoid\": [\"generic titles\", \"duplicate topics\"],\n"
        "    \"sourcePrompt\": \"...\"\n"
        "  },\n"
        "  \"courseDigest\": {\n"
        "    \"referenceCount\": 0,\n"
        "    \"assignmentTypes\": [\"...\"],\n"
        "    \"languages\": [\"...\"],\n"
        "    \"recentReferenceTitles\": [\"...\"],\n"
        "    \"teachingStyle\": [\"...\"]\n"
        "  },\n"
        "  \"courseProfile\": {\n"
        "    \"dominantSkills\": [\"...\"],\n"
        "    \"negativePatterns\": [\"...\"],\n"
        "    \"styleProfile\": {\"tone\": \"...\", \"htmlPreferred\": true},\n"
        "    \"policyProfile\": {\"allowedLanguages\": [\"...\"], \"constraints\": [\"...\"]},\n"
        "    \"assignmentOntology\": {\"topicBuckets\": [\"...\"], \"difficultyBand\": \"...\"}\n"
        "  },\n"
        "  \"summary\": \"Очень короткое summary 1-2 предложения\",\n"
        "  \"decisionSummary\": {\"confidence\": \"low|medium|high\", \"source\": \"llm-course-profile\"}\n"
        "}\n\n"
        "Правила:\n"
        "- Не пиши длинные rationale.\n"
        "- Не пересказывай referenceAssignments по одному.\n"
        "- Сведи курс в короткий digest, пригодный для planner stage.\n"
        "- canonicalRequest должен нормализовать исходный запрос пользователя, а не копировать его дословно.\n\n"
        f"Payload:\n{_prompt_json(compact_payload)}"
    )


def build_gap_analysis_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage(job.get("type") or "", payload)
    return (
        "Ты — TaskForge AI gap analyst. Верни только один валидный JSON-объект без markdown и текста вокруг.\n\n"
        "Нужно сравнить canonicalRequest с коротким digest курса и определить, что уже покрыто, а чего реально не хватает для нового batch. "
        "Ответ должен быть коротким и operational.\n\n"
        "Верни JSON строго этой формы:\n"
        "{\n"
        "  \"gapAnalysis\": {\n"
        "    \"coveredTopics\": [\"...\"],\n"
        "    \"missingTopics\": [\"...\"],\n"
        "    \"weakCoverageTopics\": [\"...\"],\n"
        "    \"duplicateClusters\": [\"...\"],\n"
        "    \"recommendedFocus\": [\"...\"],\n"
        "    \"curriculumRisks\": [\"...\"]\n"
        "  },\n"
        "  \"coverage\": {\"matchedReferenceCount\": 0, \"coverageBand\": \"low|medium|high\"},\n"
        "  \"summary\": \"Очень короткое summary 1-2 предложения\",\n"
        "  \"decisionSummary\": {\"confidence\": \"low|medium|high\", \"source\": \"llm-gap-analysis\"}\n"
        "}\n\n"
        "Правила:\n"
        "- missingTopics и recommendedFocus должны быть короткими и пригодными для planner.\n"
        "- Не повторяй generic слова вроде 'задания', 'придумай', 'сложное'.\n"
        "- Не придумывай новые домены, если запрос явно про матрицы.\n"
        "- Не пиши больших блоков текста.\n\n"
        f"Payload:\n{_prompt_json(compact_payload)}"
    )


def build_batch_plan_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    request_kind = "replan" if (job.get("type") or "").lower().strip() == "assignment_batch_replan" else "plan"
    compact_payload = compact_payload_for_stage(job.get("type") or "", payload)
    compact_mode = normalize_text(payload.get("__compactMode")).lower()
    retry_note = "Работай в ultra-compact mode." if compact_mode == "ultra" else ("Работай в compact mode." if compact_mode else "")
    prompt_low = normalize_text(payload.get("prompt")).lower()
    easy_note = "Для этого запроса нужны именно простые базовые matrix-задачи, а не advanced operations." if any(token in prompt_low for token in ["прост", "easy", "beginner", "базов", "вводн"]) else ""
    return (
        "Ты — TaskForge AI planner. Верни только один валидный JSON-объект без markdown и без пояснений.\n\n"
        f"Режим: {request_kind}. Нужно спланировать batch slot-ы, а не писать сами задания.\n"
        "Каждый slot должен быть одной чёткой учебной целью. План должен быть разнообразным, без generic тем и без дублей.\n\n"
        "Верни JSON строго этой формы:\n"
        "{\n"
        "  \"canonicalRequest\": {\"domain\": \"...\", \"count\": 2, \"difficulty\": 3, \"mustInclude\": [\"...\"], \"avoid\": [\"...\"]},\n"
        "  \"coverage\": {\"coverageBand\": \"low|medium|high\", \"noveltyGoal\": \"...\"},\n"
        "  \"summary\": \"Очень короткое summary 1-2 предложения\",\n"
        "  \"decisionSummary\": {\"confidence\": \"low|medium|high\", \"source\": \"llm-batch-plan\"},\n"
        "  \"plan\": {\n"
        "    \"tasks\": [\n"
        "      {\n"
        "        \"index\": 1,\n"
        "        \"titleHint\": \"...\",\n"
        "        \"targetSkill\": \"...\",\n"
        "        \"primarySkill\": \"...\",\n"
        "        \"microGoal\": \"...\",\n"
        "        \"uniqueAngle\": \"...\",\n"
        "        \"difficultyTarget\": 3,\n"
        "        \"mustInclude\": [\"...\"],\n"
        "        \"antiDuplicateHints\": [\"...\"],\n"
        "        \"whyItExists\": \"...\",\n"
        "        \"decisionLog\": [{\"stage\": \"batch_plan\", \"message\": \"...\"}]\n"
        "      }\n"
        "    ]\n"
        "  }\n"
        "}\n\n"
        "Правила:\n"
        "- Количество tasks должно строго совпадать с count.\n"
        "- targetSkill не может быть generic: запрещены 'Придумай', 'задания', 'task', 'advanced task'.\n"
        "- Каждый task должен отличаться по primarySkill или uniqueAngle.\n"
        "- Для matrix запроса все tasks должны быть действительно про матрицы.\n"
        "- Не пиши длинные описания.\n"
        + (easy_note + "\n" if easy_note else "")
        + (retry_note + "\n" if retry_note else "")
        + "\n"
        + f"Batch payload:\n{_prompt_json(compact_payload)}"
    )


def build_stage_schema_repair_prompt(stage: str, payload: Dict[str, Any], bad_result: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage(payload.get("requestType") or stage, payload)
    bad_json = _prompt_json(bad_result)
    if stage == "gap_analysis":
        expected = (
            '{"gapAnalysis":{"coveredTopics":["..."],"missingTopics":["..."],"weakCoverageTopics":["..."],"duplicateClusters":["..."],"recommendedFocus":["..."],"curriculumRisks":["..."]},'
            '"coverage":{"matchedReferenceCount":0,"coverageBand":"low|medium|high"},"summary":"...","decisionSummary":{"confidence":"low|medium|high","source":"llm-gap-analysis-repair"}}'
        )
    elif stage == "batch_plan":
        expected = (
            '{"canonicalRequest":{"domain":"matrix","count":2,"difficulty":3,"mustInclude":["..."],"avoid":["..."]},'
            '"coverage":{"coverageBand":"low|medium|high","noveltyGoal":"..."},"summary":"...","decisionSummary":{"confidence":"low|medium|high","source":"llm-batch-plan-repair"},'
            '"plan":{"tasks":[{"index":1,"titleHint":"...","targetSkill":"...","primarySkill":"...","microGoal":"...","uniqueAngle":"...","difficultyTarget":3,"mustInclude":["..."],"antiDuplicateHints":["..."],"whyItExists":"...","decisionLog":[{"stage":"batch_plan","message":"..."}]}]}}'
        )
    elif stage == "draft_generate":
        expected = (
            '{"draft":{"assignmentType":"code-test","title":"...","description":"Постановка задачи...\n\nВходные данные...\n\nВыходные данные...","allowedLanguages":["python","cpp","csharp"],"publicTests":[{"input":"...","expectedOutput":"..."}],"hiddenTests":[{"input":"...","expectedOutput":"..."}],"referenceSolutionPython":"..."},"summary":"...","decisionSummary":{"confidence":"low|medium|high","source":"llm-draft-generate-repair"}}'
        )
    else:
        expected = (
            '{"canonicalRequest":{"domain":"...","count":2,"difficulty":3,"mustInclude":["..."],"avoid":["..."]},'
            '"courseDigest":{"languages":["cpp"],"teachingStyle":["structured-statement"],"referenceCount":0},'
            '"courseProfile":{"dominantSkills":["..."],"difficultyDistribution":{},"styleProfile":{},"policyProfile":{},"negativePatterns":["..."]},'
            '"summary":"...","decisionSummary":{"confidence":"low|medium|high","source":"llm-course-profile-repair"}}'
        )
    return (
        "Ты — TaskForge AI schema repair. Верни только один валидный JSON-объект без markdown и без пояснений.\n\n"
        f"Стадия: {stage}. Предыдущий ответ имел неверную схему. Нужно вернуть ответ СТРОГО в ожидаемом формате.\n"
        f"Ожидаемая схема JSON:\n{expected}\n\n"
        "Если в предыдущем ответе отсутствуют поля, дострой их по payload, но обязательно верни все required top-level ключи.\n\n"
        f"Payload:\n{_prompt_json(compact_payload)}\n\n"
        f"Неверный ответ:\n{bad_json}"
    )


def build_brief_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage(job.get("type") or "", payload)
    extra_rules = [
        "generationPrompt должен быть понятным, узким и не смешивать много учебных целей.",
        "sourceText должен отражать исходный пользовательский intent, а не случайный reference fragment.",
        "Не создавай взаимоисключающие требования по policy.",
    ]
    if detect_beginner_char_array_track(payload):
        extra_rules.extend([
            "Это beginner C++ char[] task. Делай одну маленькую цель и базовый IO через cin/cout.",
            "Не предлагай fgets/scanf/printf/cstring-функции, если их нет в явном user intent.",
            "Для beginner track предпочитай ручные операции с char[]: цикл по символам, поиск длины вручную, замена символа, сравнение символов.",
        ])
    return (
        "Ты — TaskForge AI brief writer. Верни только валидный JSON без markdown.\n\n"
        "Нужно написать идеальный brief для одной будущей задачи, а не сам draft задания.\n"
        "Используй historicalPlannerPriors, historicalSlotPriors, institutionalMemory и antiPatternMemory "
        "как ограничения: усиливай удачные patterns и не повторяй исторически слабые.\n"
        "Формат JSON: {\"titleHint\":\"...\",\"summary\":\"...\",\"generationPrompt\":\"...\","
        "\"sourceText\":\"...\",\"notes\":\"...\",\"difficultyTarget\":2,\"targetSkill\":\"...\","
        "\"decisionLog\":[{\"stage\":\"brief_generate\",\"message\":\"...\"}]}.\n"
        + "\n".join(extra_rules) + "\n\n"
        + f"Brief payload:\n{_prompt_json(compact_payload)}"
    )


def build_brief_repair_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage(job.get("type") or "", payload)
    extra_rules = [
        "Обнови generationPrompt, sourceText и notes так, чтобы они устранили все замечания review.",
        "Сохрани targetSkill и difficultyTarget, если review не требует их изменения.",
        "Не расширяй brief — одна задача = одна учебная цель.",
    ]
    if detect_beginner_char_array_track(payload):
        extra_rules.extend([
            "Это beginner C++ char[] task. Не уводи brief в cstring/scanf/fgets.",
            "Верни brief к базовым ручным операциям char[] через cin/cout.",
        ])
    return (
        "Ты — TaskForge AI brief repair agent. Верни только валидный JSON без markdown.\n\n"
        "Нужно исправить brief по результатам brief review. Не генерируй draft — только brief.\n"
        "Используй findings и reviewResults для понимания, что именно нужно починить.\n"
        "Формат JSON: {\"titleHint\":\"...\",\"summary\":\"...\",\"generationPrompt\":\"...\","
        "\"sourceText\":\"...\",\"notes\":\"...\",\"difficultyTarget\":2,\"targetSkill\":\"...\","
        "\"decisionLog\":[{\"stage\":\"brief_repair\",\"message\":\"...\"}]}.\n"
        + "\n".join(extra_rules) + "\n\n"
        + f"Brief repair payload:\n{_prompt_json(compact_payload)}"
    )

def build_reference_pack_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage(job.get("type") or "", payload)
    anchor_buckets = _collect_reference_buckets(payload)
    phrase_bank = _extract_course_phrase_bank(payload)
    extra = (
        "policyPack должен быть внутренне согласованным: "
        "одно и то же нельзя одновременно помещать в required/enforced и forbidden."
    )
    if detect_beginner_char_array_track(payload):
        extra += (
            " Для beginner C++ char[] track generationHints должны уводить "
            "в базовые ручные операции и не форсировать cstring/fgets без явного запроса."
        )
    reference_payload = dict(compact_payload)
    reference_payload["anchorBuckets"] = anchor_buckets
    reference_payload["coursePhraseBank"] = phrase_bank
    return (
        "Ты — TaskForge AI reference pack builder. Верни только валидный JSON без markdown.\n\n"
        "Нужно собрать сильный course-native reference pack для одной будущей задачи: stylePack, policyPack, negativePack, exemplarPack, signals, generationHints.\n"
        "Собирай pack не вообще по теме, а по ролям: style anchors, difficulty anchors, topic anchors, negative anchors.\n"
        "Выдели style fingerprint курса: title patterns, phrase bank, section order, test style, допустимые ограничения, anti-patterns.\n"
        "Negative pack должен явно перечислять, чего НЕ надо повторять из слабых исторических паттернов и ближайших reference assignments.\n"
        f"{extra}\n"
        "Формат JSON: {\"stylePack\":{...},\"policyPack\":{...},\"negativePack\":{...},\"exemplarPack\":{...},\"signals\":{...},\"generationHints\":{...},\"decisionLog\":[{\"stage\":\"reference_pack_build\",\"message\":\"...\"}]}.\n"
        "В stylePack желательно вернуть: titleFingerprint, descriptionFingerprint, testFingerprint, phraseBank, sectionOrder, toneRules.\n"
        "В exemplarPack желательно вернуть: styleAnchors, difficultyAnchors, topicAnchors, negativeAnchors.\n"
        "В generationHints желательно вернуть: styleContract, bannedOverlapIds, noveltyTargets, titleDos, titleDonts.\n\n"
        f"Reference pack payload:\n{_prompt_json(reference_payload)}"
    )


def _compact_title_examples(payload: Dict[str, Any], limit: int = 12) -> List[Dict[str, str]]:
    refs = compact_reference_assignments(payload, limit=limit, description_len=120, include_cases=False)
    examples: List[Dict[str, str]] = []
    for ref in refs:
        title = normalize_text(ref.get("Title") or ref.get("title"))
        desc = summarize_description(ref.get("Description") or ref.get("description"), 120)
        if title:
            examples.append({"title": title, "description": desc})
    return examples[:limit]


def _compact_course_style_examples(payload: Dict[str, Any], limit: int = 8) -> List[Dict[str, Any]]:
    refs = compact_reference_assignments(payload, limit=limit, description_len=160, include_cases=True)
    examples: List[Dict[str, Any]] = []
    for ref in refs:
        title = normalize_text(ref.get("title") or ref.get("Title"))
        desc = summarize_description(ref.get("descriptionSummary") or ref.get("description") or ref.get("Description"), 160)
        if not title and not desc:
            continue
        examples.append({
            "title": title,
            "description": desc,
            "difficulty": ref.get("difficulty"),
            "tags": normalize_text(ref.get("tags")),
            "publicCases": ref.get("publicCases")[:2] if isinstance(ref.get("publicCases"), list) else [],
        })
    return examples[:limit]


def build_draft_course_style_analysis_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    compact_payload = {
        "courseId": payload.get("courseId"),
        "assignmentType": normalize_text(payload.get("assignmentType") or "code-test") or "code-test",
        "prompt": truncate_text(payload.get("prompt"), 220),
        "brief": {
            "summary": truncate_text(brief.get("summary"), 240),
            "targetSkill": truncate_text(brief.get("targetSkill"), 160),
            "titleHint": truncate_text(brief.get("titleHint"), 120),
        },
        "task": {
            "targetSkill": truncate_text(task.get("targetSkill") or task.get("TargetSkill"), 160),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 240),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
        },
        "courseExamples": _compact_course_style_examples(payload, limit=8),
        "titleExamples": _compact_title_examples(payload, limit=14),
    }
    return (
        "Ты — TaskForge AI course style analyst. Верни только JSON без markdown.\n\n"
        "Сейчас не нужно писать задачу. Нужно проанализировать существующие задания курса и вывести style digest для следующей генерации.\n"
        "Определи: как обычно формулируется условие, какие секции обязательны, насколько подробны ограничения, как выглядят тесты и как обычно называются задания.\n"
        "Не придумывай новую задачу и не копируй готовые title/description дословно.\n"
        "Верни JSON формата: {\"courseStyle\":{...},\"titleStyle\":{...},\"antiPatterns\":[...],\"positivePatterns\":[...],\"summary\":\"...\"}.\n\n"
        "Требования к полям:\n"
        "- courseStyle.descriptionSections: массив строк.\n"
        "- courseStyle.descriptionTone: коротко опиши стиль формулировок курса.\n"
        "- courseStyle.testStyle: коротко опиши типичный набор тестов.\n"
        "- titleStyle.pattern: коротко опиши стиль названий курса.\n"
        "- titleStyle.examples: 3-6 коротких примеров названий из курса.\n"
        "- antiPatterns: чего нельзя делать при генерации, чтобы не выбиться из курса.\n"
        "- positivePatterns: что наоборот обязательно сохранить.\n\n"
        f"Course style payload:\n{_prompt_json(compact_payload)}"
    )


def build_draft_generation_spec_prompt(job: Dict[str, Any], payload: Dict[str, Any], style_analysis: Dict[str, Any]) -> str:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    compact_peers = _compact_peer_context(payload, limit=6)
    compact_payload = {
        "prompt": truncate_text(payload.get("prompt"), 220),
        "brief": {
            "summary": truncate_text(brief.get("summary"), 260),
            "generationPrompt": truncate_text(brief.get("generationPrompt"), 360),
            "targetSkill": truncate_text(brief.get("targetSkill"), 160),
            "titleHint": truncate_text(brief.get("titleHint"), 140),
        },
        "task": {
            "index": task.get("index") or task.get("Index"),
            "targetSkill": truncate_text(task.get("targetSkill") or task.get("TargetSkill"), 160),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 220),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
            "antiDuplicateHints": task.get("antiDuplicateHints") or task.get("AntiDuplicateHints") or [],
        },
        "styleAnalysis": style_analysis,
        "peerItems": compact_peers,
        "coursePhraseBank": _extract_course_phrase_bank(payload, limit=8),
        "anchorBuckets": _collect_reference_buckets(payload),
    }
    return (
        "Ты — TaskForge AI generation planner. Верни только JSON без markdown.\n\n"
        "Сейчас не нужно писать готовое задание. Нужно решить, что именно должно быть создано для этого slot.\n"
        "Учитывай style digest курса, brief, microGoal и соседние slot-ы пакета, чтобы новая задача не дублировала их и оставалась в стиле курса.\n"
        "Верни JSON формата: {\"generationSpec\":{...},\"summary\":\"...\"}.\n"
        "В generationSpec должны быть поля:\n"
        "- exactTask: одна короткая фраза, что именно надо сделать в задаче.\n"
        "- ioContract: короткое описание формата ввода/вывода.\n"
        "- constraintsFocus: какие ограничения надо подчеркнуть.\n"
        "- titleDirection: каким по смыслу должно быть название.\n"
        "- distinctFromPeers: почему эта задача не совпадает с соседними.\n"
        "- keepStyle: 3-6 коротких правил курса, которые обязательно сохранить.\n"
        "- avoid: 3-6 коротких ошибок, которые нельзя допустить.\n"
        "- noveltyPlan: чем задача будет отличаться от ближайших topic/negative anchors.\n"
        "- titleDos/titleDonts: короткие правила для названия.\n\n"
        f"Generation spec payload:\n{_prompt_json(compact_payload)}"
    )


def build_draft_content_plan_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    style_analysis = payload.get("styleAnalysis") if isinstance(payload.get("styleAnalysis"), dict) else {}
    generation_spec = payload.get("generationSpec") if isinstance(payload.get("generationSpec"), dict) else {}
    reference_pack = payload.get("referencePack") if isinstance(payload.get("referencePack"), dict) else {}
    compact_payload = {
        "brief": {
            "summary": truncate_text(brief.get("summary"), 240),
            "generationPrompt": truncate_text(brief.get("generationPrompt"), 300),
            "targetSkill": truncate_text(brief.get("targetSkill"), 120),
            "titleHint": truncate_text(brief.get("titleHint"), 120),
        },
        "task": {
            "targetSkill": truncate_text(task.get("targetSkill") or task.get("TargetSkill"), 160),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 220),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
        },
        "styleAnalysis": style_analysis,
        "generationSpec": generation_spec,
        "anchorBuckets": _collect_reference_buckets(payload),
        "coursePhraseBank": _extract_course_phrase_bank(payload, limit=8),
        "styleContract": (reference_pack.get("generationHints") or {}).get("styleContract") if isinstance(reference_pack.get("generationHints"), dict) else None,
        "peerItems": _compact_peer_context(payload, limit=6),
    }
    return (
        "Ты — TaskForge AI draft content planner. Верни только JSON без markdown.\n\n"
        "Не пиши ещё полное условие. Сначала составь content plan для будущего draft.\n"
        "План должен удерживать стиль курса, уникальность относительно соседних items и конкретную учебную цель.\n"
        "Верни JSON: {\"contentPlan\":{...},\"summary\":\"...\"}.\n"
        "В contentPlan должны быть поля: pedagogicalGoal, noveltyHook, inputModel, outputModel, constraintsPlan, sectionPlan, publicTestPlan, hiddenTestPlan, titleShape, bannedOverlaps, coursePhrasingRules.\n\n"
        f"Draft content plan payload:\n{_prompt_json(compact_payload)}"
    )


def build_draft_body_generate_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    reference_pack = payload.get("referencePack") if isinstance(payload.get("referencePack"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
    quality_gates = payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {}
    style_analysis = payload.get("styleAnalysis") if isinstance(payload.get("styleAnalysis"), dict) else {}
    generation_spec = payload.get("generationSpec") if isinstance(payload.get("generationSpec"), dict) else {}
    content_plan = payload.get("contentPlan") if isinstance(payload.get("contentPlan"), dict) else {}
    compact_payload = {
        "assignmentType": normalize_text(payload.get("assignmentType") or "code-test") or "code-test",
        "courseId": payload.get("courseId"),
        "batchId": payload.get("batchId"),
        "batchItemId": payload.get("batchItemId"),
        "difficulty": safe_int(payload.get("difficulty"), safe_int(brief.get("difficultyTarget"), 2)),
        "titleHint": truncate_text(payload.get("titleHint") or brief.get("titleHint") or task.get("targetSkill"), 160),
        "prompt": truncate_text(payload.get("prompt") or brief.get("generationPrompt"), 500),
        "sourceText": truncate_text(payload.get("sourceText") or brief.get("sourceText") or brief.get("summary"), 500),
        "notes": truncate_text(payload.get("notes") or brief.get("notes"), 320),
        "brief": {
            "summary": truncate_text(brief.get("summary"), 260),
            "generationPrompt": truncate_text(brief.get("generationPrompt"), 700),
            "sourceText": truncate_text(brief.get("sourceText"), 400),
            "difficultyTarget": brief.get("difficultyTarget"),
            "targetSkill": truncate_text(brief.get("targetSkill"), 160),
            "titleHint": truncate_text(brief.get("titleHint"), 160),
        },
        "task": {
            "targetSkill": truncate_text(task.get("targetSkill") or task.get("TargetSkill"), 160),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 260),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
        },
        "qualityGates": quality_gates,
        "referencePack": {
            "stylePack": reference_pack.get("stylePack") if isinstance(reference_pack.get("stylePack"), dict) else {},
            "negativePack": reference_pack.get("negativePack") if isinstance(reference_pack.get("negativePack"), dict) else reference_pack.get("negativePack"),
            "signals": reference_pack.get("signals") if isinstance(reference_pack.get("signals"), dict) else {},
            "generationHints": reference_pack.get("generationHints") if isinstance(reference_pack.get("generationHints"), dict) else {},
            "exemplarPack": reference_pack.get("exemplarPack") if isinstance(reference_pack.get("exemplarPack"), (dict, list)) else reference_pack.get("exemplarPack"),
        },
        "styleAnalysis": style_analysis,
        "generationSpec": generation_spec,
        "contentPlan": content_plan,
        "anchorBuckets": _collect_reference_buckets(payload),
        "coursePhraseBank": _extract_course_phrase_bank(payload, limit=8),
        "referenceAssignments": compact_reference_assignments(payload, limit=6, description_len=150, include_cases=True),
    }
    min_hidden = quality_gates.get("minHiddenTests", MIN_HIDDEN_TESTS)
    max_hidden = max(min_hidden, min(MAX_HIDDEN_TESTS, max(min_hidden, 4)))
    return (
        "Ты — TaskForge AI draft body generator. Верни только один валидный JSON без markdown.\n\n"
        "Нужно сгенерировать только тело задания и тесты в стиле курса. Название пока НЕ придумывай: поставь в draft.title точную строку __PENDING_TITLE__.\n"
        "Сначала изучи course style analysis, generation spec, content plan, referenceAssignments и exemplarPack. Только после этого пиши draft.\n"
        "Подражай стилю условий и тестов курса, но не копируй текст, title и тесты дословно.\n"
        "Верни JSON формата:\n"
        '{"draft":{"assignmentType":"code-test","title":"__PENDING_TITLE__","description":"Полное условие без HTML","allowedLanguages":["python","cpp","csharp"],"publicTests":[{"input":"...","expectedOutput":"..."}],"hiddenTests":[{"input":"...","expectedOutput":"..."}],"referenceSolutionPython":"...","requiredCalls":[],"forbiddenCalls":[],"meta":{"generationSource":"llm-body"}},"summary":"...","decisionSummary":{"confidence":"low|medium|high","source":"llm-draft-body"}}\n\n'
        "Правила:\n"
        "- description должен быть только обычным текстом, без HTML, без TipTap JSON, без markdown.\n"
        "- description должен выглядеть как условие из этого курса: суть задачи, входные данные, выходные данные, ограничения, примечание.\n"
        "- Строго следуй generationSpec.exactTask и generationSpec.ioContract.\n"
        "- Сначала выполни generationSpec.distinctFromPeers и contentPlan.noveltyHook: новая задача должна заметно отличаться от соседних slot-ов и negative anchors.\n"
        f"- Сгенерируй минимум {quality_gates.get('minPublicTests', MIN_PUBLIC_TESTS)} publicTests и от {min_hidden} до {max_hidden} hiddenTests, не больше {max_hidden}.\n"
        "- Скрытые тесты делай компактными, но покрывающими крайние случаи.\n"
        "- Не уходи в другую микроцель: строго соблюдай targetSkill, microGoal и contentPlan.pedagogicalGoal.\n"
        "- Соблюдай contentPlan.sectionPlan и coursePhraseBank, но не копируй фразы дословно.\n"
        "- Не используй чужие title из referenceAssignments.\n\n"
        f"Draft body payload:\n{_prompt_json(compact_payload)}"
    )


def build_draft_title_generate_prompt(job: Dict[str, Any], payload: Dict[str, Any], draft: Dict[str, Any]) -> str:
    examples = _compact_title_examples(payload, limit=14)
    style_analysis = payload.get("styleAnalysis") if isinstance(payload.get("styleAnalysis"), dict) else {}
    generation_spec = payload.get("generationSpec") if isinstance(payload.get("generationSpec"), dict) else {}
    body_preview = {
        "titleHint": truncate_text(payload.get("titleHint") or (payload.get("brief") or {}).get("titleHint"), 160),
        "targetSkill": truncate_text(((payload.get("task") or {}).get("targetSkill") or (payload.get("brief") or {}).get("targetSkill")), 180),
        "microGoal": truncate_text(((payload.get("task") or {}).get("microGoal") or (payload.get("brief") or {}).get("summary")), 220),
        "description": truncate_text(strip_html_to_text(draft.get("description") or ""), 700),
    }
    return (
        "Ты — TaskForge AI title generator. Верни только JSON без markdown.\n\n"
        "Нужно придумать ТОЛЬКО название задания по уже готовому условию.\n"
        "Смотри на примеры названий из курса, style analysis и generation spec. Подражай стилю, но не копируй существующее название дословно.\n"
        "Не используй HTML. Не придумывай номер задания, если ты не уверен. Не используй служебные заглушки.\n"
        "Верни JSON: {\"title\":\"...\",\"summary\":\"...\",\"decisionSummary\":{\"source\":\"llm-title\",\"confidence\":\"low|medium|high\"}}\n\n"
        f"Course title examples:\n{json.dumps(examples, ensure_ascii=False)}\n\n"
        f"Style analysis:\n{json.dumps(style_analysis, ensure_ascii=False)}\n\n"
        f"Generation spec:\n{json.dumps(generation_spec, ensure_ascii=False)}\n\n"
        f"Course phrase bank:\n{json.dumps(_extract_course_phrase_bank(payload, limit=6), ensure_ascii=False)}\n\n"
        f"Draft body:\n{json.dumps(body_preview, ensure_ascii=False)}"
    )


def build_draft_title_repair_prompt(job: Dict[str, Any], payload: Dict[str, Any], draft: Dict[str, Any], bad_title: str) -> str:
    return (
        "Ты — TaskForge AI title repair agent. Верни только JSON без markdown.\n\n"
        "Нужно починить только название задания. Не меняй условие, тесты и решение.\n"
        "Запрещено возвращать служебные слова вроде revised, draft, pending, final, version, task.\n"
        "Верни JSON: {\"title\":\"...\",\"summary\":\"...\",\"decisionSummary\":{\"source\":\"llm-title-repair\",\"confidence\":\"low|medium|high\"}}\n\n"
        f"Bad title: {json.dumps(bad_title, ensure_ascii=False)}\n\n"
        f"Course title examples:\n{json.dumps(_compact_title_examples(payload, limit=12), ensure_ascii=False)}\n\n"
        f"Style analysis:\n{json.dumps(payload.get('styleAnalysis') if isinstance(payload.get('styleAnalysis'), dict) else {}, ensure_ascii=False)}\n\n"
        f"Draft body:\n{json.dumps({'description': truncate_text(strip_html_to_text(draft.get('description') or ''), 600), 'targetSkill': ((payload.get('task') or {}).get('targetSkill') or (payload.get('brief') or {}).get('targetSkill'))}, ensure_ascii=False)}"
    )


def build_draft_generate_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
    reference_pack = payload.get("referencePack") if isinstance(payload.get("referencePack"), dict) else {}
    target_schema = payload.get("targetSchema") if isinstance(payload.get("targetSchema"), dict) else {}
    quality_gates = payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {}
    task = payload.get("task") if isinstance(payload.get("task"), dict) else {}

    compact_payload = {
        "requestType": normalize_text(payload.get("requestType") or "assignment_generate_from_text"),
        "assignmentType": normalize_text(payload.get("assignmentType") or "code-test") or "code-test",
        "courseId": payload.get("courseId"),
        "batchId": payload.get("batchId"),
        "batchItemId": payload.get("batchItemId"),
        "difficulty": safe_int(payload.get("difficulty"), safe_int(brief.get("difficultyTarget"), 3)),
        "titleHint": truncate_text(payload.get("titleHint") or brief.get("titleHint") or task.get("targetSkill"), 160),
        "prompt": truncate_text(payload.get("prompt") or brief.get("generationPrompt"), 500),
        "sourceText": truncate_text(payload.get("sourceText") or brief.get("sourceText") or brief.get("summary"), 500),
        "notes": truncate_text(payload.get("notes") or brief.get("notes"), 320),
        "brief": {
            "titleHint": truncate_text(brief.get("titleHint"), 160),
            "summary": truncate_text(brief.get("summary"), 260),
            "generationPrompt": truncate_text(brief.get("generationPrompt"), 700),
            "sourceText": truncate_text(brief.get("sourceText"), 400),
            "difficultyTarget": brief.get("difficultyTarget"),
            "targetSkill": truncate_text(brief.get("targetSkill"), 160),
        },
        "task": {
            "targetSkill": truncate_text(task.get("targetSkill") or task.get("TargetSkill"), 160),
            "microGoal": truncate_text(task.get("microGoal") or task.get("MicroGoal"), 220),
            "difficultyTarget": task.get("difficultyTarget") or task.get("DifficultyTarget"),
        },
        "qualityGates": quality_gates,
        "targetSchema": target_schema,
        "referencePack": {
            "stylePack": reference_pack.get("stylePack") if isinstance(reference_pack.get("stylePack"), dict) else {},
            "policyPack": reference_pack.get("policyPack") if isinstance(reference_pack.get("policyPack"), dict) else {},
            "negativePack": reference_pack.get("negativePack") if isinstance(reference_pack.get("negativePack"), dict) else reference_pack.get("negativePack"),
            "signals": reference_pack.get("signals") if isinstance(reference_pack.get("signals"), dict) else {},
            "generationHints": reference_pack.get("generationHints") if isinstance(reference_pack.get("generationHints"), dict) else {},
            "exemplarPack": reference_pack.get("exemplarPack") if isinstance(reference_pack.get("exemplarPack"), (dict, list)) else reference_pack.get("exemplarPack"),
        },
        "referenceAssignments": compact_reference_assignments(payload, limit=3, description_len=100, include_cases=True),
    }

    return (
        "Ты — TaskForge AI draft generator. Верни только один валидный JSON-объект без markdown и без пояснений.\n\n"
        "Нужно создать полноценный publishable draft для одной задачи. Ответ обязан иметь top-level ключ draft.\n"
        "Не возвращай пустой объект {}, не возвращай только sourceText, не возвращай заготовки без tests или description.\n"
        "Если referencePack частично пустой, всё равно собери полноценный draft по brief, qualityGates и targetSchema.\n\n"
        "Формат ответа строго такой:\n"
        "{\n"
        "  \"draft\": {\n"
        "    \"assignmentType\": \"code-test|test|math\",\n"
        "    \"title\": \"...\",\n"
        "    \"description\": \"Постановка задачи...\n\nВходные данные...\n\nВыходные данные...\",\n"
        "    \"allowedLanguages\": [\"python\",\"cpp\",\"csharp\"],\n"
        "    \"publicTests\": [{\"input\":\"...\",\"expectedOutput\":\"...\"}],\n"
        "    \"hiddenTests\": [{\"input\":\"...\",\"expectedOutput\":\"...\"}],\n"
        "    \"referenceSolutionPython\": \"...\",\n"
        "    \"requiredCalls\": [\"...\"],\n"
        "    \"forbiddenCalls\": [\"...\"],\n"
        "    \"meta\": {\"generationSource\": \"llm\"}\n"
        "  },\n"
        "  \"summary\": \"1-2 коротких предложения\",\n"
        "  \"decisionSummary\": {\"confidence\": \"low|medium|high\", \"source\": \"llm-draft-generate\"}\n"
        "}\n\n"
        "Правила:\n"
        "- description обязан быть полноценным текстовым условием с блоками problem/input/output/constraints/notes, но без HTML-тегов.\n"
        "- Для code-test обязательно: title, description, allowedLanguages, publicTests, hiddenTests, referenceSolutionPython.\n"
        "- referenceSolutionPython должен проходить все сгенерированные tests.\n"
        "- Задача должна соответствовать titleHint, targetSkill и microGoal, а не уходить в другой домен.\n"
        "- Не копируй referenceAssignments дословно.\n\n"
        f"Draft payload:\n{_prompt_json(compact_payload)}"
    )


def build_batch_review_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    return (
        "Ты — TaskForge AI batch coherence reviewer. Верни только JSON без markdown.\n\n"
        "Оцени пакет задач как учебную систему: progression, coverage, redundancy, variety, risks.\n"
        "Верни status, score, summary, checks, findings, progression, coverage, decisionSummary.\n\n"
        f"Payload:\n{json.dumps(payload, ensure_ascii=False, indent=2)}"
    )


def build_student_journey_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    return (
        "Ты — TaskForge AI student journey simulator. Верни только JSON без markdown.\n\n"
        "Симулируй проход студента по batch items по порядку. "
        "Оцени мостики между задачами, резкость усложнения, потерю контекста и нехватку промежуточных шагов.\n"
        "Верни status, score, summary, checks, findings, transitions, journeySteps, decisionSummary.\n\n"
        f"Payload:\n{json.dumps(payload, ensure_ascii=False, indent=2)}"
    )
