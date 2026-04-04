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

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_DESCRIPTION_LEN
from log import log
from text_utils import normalize_text, truncate_text, safe_int, unique_string_list
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
        + (retry_note + "\n" if retry_note else "")
        + "\n"
        + f"Batch payload:\n{_prompt_json(compact_payload)}"
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
    extra = (
        "policyPack должен быть внутренне согласованным: "
        "одно и то же нельзя одновременно помещать в required/enforced и forbidden."
    )
    if detect_beginner_char_array_track(payload):
        extra += (
            " Для beginner C++ char[] track generationHints должны уводить "
            "в базовые ручные операции и не форсировать cstring/fgets без явного запроса."
        )
    return (
        "Ты — TaskForge AI reference pack builder. Верни только валидный JSON без markdown.\n\n"
        "Нужно собрать compact reference pack для одной будущей задачи: "
        "stylePack, policyPack, negativePack, exemplarPack, signals, generationHints.\n"
        "Используй brief, briefReview, courseProfile, antiPatternMemory, "
        "historicalPlannerPriors и historicalSlotPriors.\n"
        "Negative pack должен явно перечислять, чего НЕ надо повторять из слабых исторических паттернов.\n"
        f"{extra}\n"
        "Формат JSON: {\"stylePack\":{...},\"policyPack\":{...},\"negativePack\":{...},"
        "\"exemplarPack\":{...},\"signals\":{...},\"generationHints\":{...},"
        "\"decisionLog\":[{\"stage\":\"reference_pack_build\",\"message\":\"...\"}]}.\n\n"
        f"Reference pack payload:\n{_prompt_json(compact_payload)}"
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
