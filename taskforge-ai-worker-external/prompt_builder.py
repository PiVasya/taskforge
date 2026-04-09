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
    infer_allowed_languages,
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


def _pedagogy_appendix(compact_payload: Dict[str, Any]) -> str:
    batch_memory = compact_payload.get("batchMemory") if isinstance(compact_payload.get("batchMemory"), dict) else {}
    learner = batch_memory.get("learnerProfile") if isinstance(batch_memory.get("learnerProfile"), dict) else {}
    pedagogy = batch_memory.get("pedagogy") if isinstance(batch_memory.get("pedagogy"), dict) else {}
    task = compact_payload.get("task") if isinstance(compact_payload.get("task"), dict) else {}
    constraints = batch_memory.get("constraints") if isinstance(batch_memory.get("constraints"), dict) else {}
    task_format = normalize_text(task.get("taskFormat") or task.get("learningMode")).lower()
    lines: List[str] = []
    if batch_memory.get("placementPlan"):
        lines.append("- В batchMemory уже есть placementPlan из чата/аудита курса: не игнорируй его и не придумывай тему с нуля.")
    if bool(pedagogy.get("preferGuidedWalkthroughs")) or task_format == "guided-walkthrough":
        lines.extend([
            "- Для этого slot предпочитай guided walkthrough: описание должно вести ученика по маленьким шагам, а не бросать сразу в сухую формулировку.",
            "- Разрешён формат-путеводитель: «Шаг 1 ... Шаг 2 ... Шаг 3 ...», при этом задача всё равно должна оставаться полноценным заданием с входом, выходом и тестами.",
            "- Сначала скажи, что нужно написать, затем что ввести, затем какой результат должен получиться.",
        ])
    if bool(learner.get("explainLikeChild")) or normalize_text(learner.get("audience")).lower() in {"young-beginners", "kids"}:
        lines.extend([
            "- Пиши как для очень маленьких новичков: только очень простой русский, короткие предложения и один новый смысл за раз.",
            "- Запрещён академический тон, абстрактные формулировки и слова, которые ученик не поймёт без объяснения.",
            "- Каждую новую функцию или конструкцию объясняй через конкретное действие и маленький пример результата.",
        ])
    must_stay_before = unique_string_list(constraints.get("mustStayBeforeConcepts"), 6)
    avoid = unique_string_list(constraints.get("avoidConcepts"), 6)
    if must_stay_before:
        lines.append(f"- Не уезжай дальше по курсу: эта генерация должна оставаться до тем {', '.join(must_stay_before)}.")
    if avoid:
        lines.append(f"- Не используй и не вводи темы {', '.join(avoid)}, если это не требуется напрямую.")
    return "\n".join(lines) + ("\n" if lines else "")


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
            f"Всего тестов должно быть не меньше {quality.get('minTotalTests', max(MIN_PUBLIC_TESTS + MIN_HIDDEN_TESTS, 5))}.",
            f"description должен быть не короче {quality.get('minDescriptionLength', MIN_DESCRIPTION_LEN)} символов.",
            "В description обязательно раскрой: суть задачи, формат входных данных, формат выходных данных, ограничения, хотя бы одну заметку или пояснение.",
            "referenceSolutionPython должен быть полностью рабочим, детерминированным, читать stdin и печатать только ответ.",
            "Сгенерируй edge cases: минимальные значения, типичные значения, пограничные случаи.",
            "Количество publicTests и hiddenTests выбирай осознанно под задачу, а не по шаблону.",
            "Предпочтительно publicTests делать больше, чем hiddenTests, чтобы студент видел больше примеров.",
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


def build_chat_turn_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    compact_payload = {
        "sessionId": payload.get("sessionId"),
        "sessionTitle": payload.get("sessionTitle"),
        "courseId": payload.get("courseId"),
        "selectedCourse": payload.get("selectedCourse") if isinstance(payload.get("selectedCourse"), dict) else None,
        "memory": payload.get("memory") if isinstance(payload.get("memory"), dict) else {},
        "conversation": payload.get("conversation")[-16:] if isinstance(payload.get("conversation"), list) else [],
        "recentAttachments": payload.get("recentAttachments")[-10:] if isinstance(payload.get("recentAttachments"), list) else [],
        "recentAssignments": payload.get("recentAssignments")[:12] if isinstance(payload.get("recentAssignments"), list) else [],
        "recentDrafts": payload.get("recentDrafts")[:12] if isinstance(payload.get("recentDrafts"), list) else [],
        "recentBatches": payload.get("recentBatches")[:8] if isinstance(payload.get("recentBatches"), list) else [],
        "recentJobs": payload.get("recentJobs")[:12] if isinstance(payload.get("recentJobs"), list) else [],
        "recentUsers": payload.get("recentUsers")[:16] if isinstance(payload.get("recentUsers"), list) else [],
        "recentAttempts": payload.get("recentAttempts")[:16] if isinstance(payload.get("recentAttempts"), list) else [],
        "availableCourses": payload.get("availableCourses")[:40] if isinstance(payload.get("availableCourses"), list) else [],
        "availableActions": payload.get("availableActions") if isinstance(payload.get("availableActions"), list) else [],
        "defaults": payload.get("defaults") if isinstance(payload.get("defaults"), dict) else {},
    }
    return (
        "Ты — TaskForge AI chat orchestrator. Верни только один валидный JSON-объект без markdown и без пояснений вокруг JSON.\\n\\n"
        "Твоя задача: ответить пользователю по-русски и, если данных уже достаточно, выбрать одно или несколько доступных действий TaskForge. "
        "Если данных не хватает — actions должен быть пустым массивом, а assistantMessage должен кратко запросить недостающие параметры.\\n\\n"
        "memory — это долговременная память всей сессии: прошлые цели пользователя, вложения, уже выполненные действия и найденные сущности. "
        "Если пользователь пишет 'продолжай', 'сделай ещё', 'начинай' или подобный короткий follow-up, сперва опирайся на memory и последние toolResults, а не проси заново весь контекст.\\n\\n"
        "Когда пользователь просит создать пакет заданий на несколько элементов, обычно подходит queue_generate_batch. Если он уточняет педагогический режим вроде «первоклассники», «очень простым языком», «нужны пошаговые путеводители» — сохрани это в reason/arguments как важную часть генерации, не теряй эти требования. "
        "Если пользователь просит сначала изучить курс, найти пробелы, резкие вводы новых функций или придумать мостики до новой темы, сначала используй analyze_course_progression. "
        "Если после аудита нужно посмотреть конкретные существующие задания, названия, соседние элементы курса или место вставки вокруг anchor — используй inspect_course_assignments. "
        "Если после аудита нужно собрать явный список вставок, количества задач, title hints и afterAssignmentId — используй prepare_bridge_plan. Если пользователь пишет короткое «продолжай/делай дальше» и нужно выбрать следующий шаг по памяти агента автоматически — используй advance_agent_stage. "
        "Если у тебя уже есть готовый план мостиков в memory и пользователь просит добавить мостики или подводящие задания по найденному плану — подходит queue_generate_bridge_batch. "
        "Если пользователь хочет несколько заданий, но не указал количество явно, не подставляй count молча из defaults: сначала задай короткий уточняющий вопрос про количество и не запускай action. "
        "Когда пользователь просит сгенерировать задание(я) из текста — queue_generate_from_text. "
        "Когда пользователь явно просит использовать прикреплённый файл — queue_generate_from_file. "
        "Когда пользователь просит проверить/провалидировать draft — queue_validate_draft. "
        "Когда пользователь просит анализ уже существующего задания — queue_analyze_assignment. "
        "Когда пользователь просит review попытки — queue_review_submission. "
        "Когда пользователь просит review пользователя — queue_review_user.\\n\\n"
        "Опасные действия approve_draft, reject_draft, publish_draft разрешены только если пользователь явно и недвусмысленно попросил это сделать. "
        "Для них обязательно передавай confirmed=true. Если явного подтверждения нет — не выполняй действие.\\n\\n"
        "Не выдумывай id. Используй только те courseId, draftId, assignmentId, batchId, userId и sourceAttemptId, которые уже есть в payload. "
        "Если курс не ясен — не угадывай, а попроси пользователя выбрать. Обычно достаточно максимум 1-2 actions за ход. "
        "Если memory уже содержит ясный контекст и параметров хватает, можно сразу переходить к генерации или следующему действию.\\n\\n"
        "Если можешь улучшить UX, можешь дополнительно вернуть sessionTitle — короткое новое название чата.\\n\\n"
        "Верни JSON строго вида:\\n"
        "{\\n"
        "  \\\"assistantMessage\\\": \\\"...\\\",\\n"
        "  \\\"sessionTitle\\\": \\\"...\\\",\\n"
        "  \\\"actions\\\": []\\n"
        "}\\n"
        "или\\n"
        "{\\n"
        "  \\\"assistantMessage\\\": \\\"...\\\",\\n"
        "  \\\"sessionTitle\\\": \\\"...\\\",\\n"
        "  \\\"actions\\\": [\\n"
        "    {\\n"
        "      \\\"name\\\": \\\"queue_generate_batch|analyze_course_progression|inspect_course_assignments|prepare_bridge_plan|show_bridge_plan|revise_bridge_plan|advance_agent_stage|queue_generate_bridge_batch|queue_generate_from_text|queue_generate_from_file|queue_validate_draft|approve_draft|reject_draft|publish_draft|queue_analyze_assignment|queue_review_submission|queue_review_user\\\",\\n"
        "      \\\"reason\\\": \\\"...\\\",\\n"
        "      \\\"arguments\\\": { ... }\\n"
        "    }\\n"
        "  ]\\n"
        "}\\n\\n"
        "Для совместимости можно дополнительно вернуть action как первый элемент actions, но основной формат — именно actions.\\n\\n"
        f"Payload:\\n{_prompt_json(compact_payload)}\\n\\n"
        f"Files:\\n{files_text(job)}"
    )



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
        "Каждый slot должен быть одной чёткой учебной целью. План должен быть разнообразным, без generic тем и без дублей.\n"
        "Для каждого slot выбери anchor в курсе: после какого существующего задания его лучше вставить.\n\n"
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
        "        \"placementAfterAssignmentId\": \"guid или null\",\n"
        "        \"placementAfterTitle\": \"... или null\",\n"
        "        \"placementReason\": \"...\",\n"
        "        \"taskFormat\": \"guided-walkthrough|exercise\",\n"
        "        \"learningMode\": \"guided-walkthrough|exercise\",\n"
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
        "- placementAfterAssignmentId — это GUID существующего задания из referenceAssignments/current course order, после которого лучше вставить новый slot; не используй sort index.\n"
        "- Если подходящего anchor нет, верни placementAfterAssignmentId = null и коротко объясни это в placementReason.\n"
        "- placementAfterTitle должен совпадать с названием выбранного anchor или быть null.\n"
        "- Если в batchMemory есть placementPlan, строй слоты вокруг него: после каких заданий вставлять, чему они учат и почему именно там.\n"
        "- Если learnerProfile/pedagogy указывает на guided walkthrough или very simple audience, часть slot-ов в начале новой темы делай taskFormat=guided-walkthrough.\n"
        "- guided-walkthrough — это не конспект и не лекция, а очень простая пошаговая учебная задача перед обычными упражнениями.\n"
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
            '"plan":{"tasks":[{"index":1,"titleHint":"...","targetSkill":"...","primarySkill":"...","microGoal":"...","uniqueAngle":"...","difficultyTarget":3,"mustInclude":["..."],"antiDuplicateHints":["..."],"whyItExists":"...","placementAfterAssignmentId":"guid или null","placementAfterTitle":"...","placementReason":"...","taskFormat":"guided-walkthrough|exercise","learningMode":"guided-walkthrough|exercise","decisionLog":[{"stage":"batch_plan","message":"..."}]}]}}'
        )
    elif stage == "draft_generate":
        assignment_type = normalize_text(payload.get("assignmentType") or "code-test") or "code-test"
        expected = _draft_response_format(payload, assignment_type, include_pending_title=False, min_public=MIN_PUBLIC_TESTS, min_hidden=MIN_HIDDEN_TESTS)
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


def _supported_code_languages(payload: Dict[str, Any]) -> List[str]:
    langs = infer_allowed_languages(payload)
    langs = unique_string_list(langs, 10)
    if langs:
        return langs
    schema = payload.get("targetSchema") if isinstance(payload.get("targetSchema"), dict) else {}
    schema_langs = schema.get("allowedLanguages") if isinstance(schema.get("allowedLanguages"), list) else []
    schema_langs = unique_string_list(schema_langs, 10)
    if schema_langs:
        return schema_langs
    return ["cpp", "csharp", "python", "javascript", "java", "pascal"]


def _schema_version(payload: Dict[str, Any]) -> str:
    raw = normalize_text(payload.get("schemaVersion"))
    return raw or "draft-v2"


def _draft_response_format(payload: Dict[str, Any], assignment_type: str, include_pending_title: bool = False, min_public: int = MIN_PUBLIC_TESTS, min_hidden: int = MIN_HIDDEN_TESTS) -> str:
    title = "__PENDING_TITLE__" if include_pending_title else "..."
    top = {
        "schemaVersion": _schema_version(payload),
        "summary": "...",
        "decisionSummary": {"confidence": "low|medium|high", "source": "llm-draft-body" if include_pending_title else "llm-draft-generate"},
    }
    if assignment_type == "test":
        top["draft"] = {
            "assignmentType": "test",
            "title": title,
            "description": "Полное условие без HTML",
            "settings": {
                "maxAttempts": 1,
                "passPercent": 60,
                "shuffleQuestions": True,
                "shuffleAnswers": True,
                "allowReview": True,
                "attemptTimeLimitsSeconds": [],
            },
            "questions": [
                {"type": "single-choice", "prompt": "...", "options": [{"key": "a", "text": "..."}, {"key": "b", "text": "..."}], "correctOptionKeys": ["a"]},
                {"type": "text", "prompt": "...", "acceptedAnswers": ["..."], "caseSensitive": False, "trim": True},
            ],
            "placementAfterAssignmentId": "guid или null",
            "placementAfterTitle": "...",
            "placementReason": "...",
            "meta": {"generationSource": "llm-body" if include_pending_title else "llm"},
        }
        return json.dumps(top, ensure_ascii=False)
    if assignment_type == "math":
        top["draft"] = {
            "assignmentType": "math",
            "title": title,
            "description": "Полное условие без HTML",
            "settings": {
                "maxAttempts": 1,
                "passPercent": 60,
                "shuffleBlocks": False,
                "allowReview": True,
                "attemptTimeLimitsSeconds": [],
            },
            "blocks": [
                {"kind": "info", "prompt": "...", "promptContentJson": None, "score": 0, "isRequired": True},
                {"kind": "number", "prompt": "...", "promptContentJson": None, "score": 1, "isRequired": True, "acceptedAnswers": ["4"], "caseSensitive": False, "trim": True, "numericTolerance": 0},
            ],
            "placementAfterAssignmentId": "guid или null",
            "placementAfterTitle": "...",
            "placementReason": "...",
            "meta": {"generationSource": "llm-body" if include_pending_title else "llm"},
        }
        return json.dumps(top, ensure_ascii=False)
    top["draft"] = {
        "assignmentType": "code-test",
        "title": title,
        "description": "Полное условие без HTML",
        "allowedLanguages": _supported_code_languages(payload),
        "publicTests": [{"input": "...", "expectedOutput": "..."} for _ in range(max(1, min_public))],
        "hiddenTests": [{"input": "...", "expectedOutput": "..."} for _ in range(max(1, min_hidden))],
        "referenceSolutionPython": "...",
        "requiredCalls": [],
        "forbiddenCalls": [],
        "placementAfterAssignmentId": "guid или null",
        "placementAfterTitle": "...",
        "placementReason": "...",
        "meta": {"generationSource": "llm-body" if include_pending_title else "llm"},
    }
    return json.dumps(top, ensure_ascii=False)


def _draft_type_rules(payload: Dict[str, Any], assignment_type: str, quality_gates: Dict[str, Any], body_mode: bool = False) -> str:
    if assignment_type == "test":
        return (
            "- Для test обязательны: title, description, settings, questions.\n"
            f"- Нужно минимум {quality_gates.get('minQuestions', 5)} вопросов.\n"
            "- Разрешены только канонические поля editor-контракта: type, prompt, options, correctOptionKeys, acceptedAnswers, caseSensitive, trim.\n"
            "- Запрещены legacy-алиасы questionType, title, correctKeys, correct, answers, correctAnswers.\n"
            "- Для single-choice/multi-choice нужны минимум 2 options и валидные correctOptionKeys.\n"
            "- Для fill/text обязательны acceptedAnswers, caseSensitive и trim.\n"
            "- Не добавляй allowedLanguages, publicTests, hiddenTests и referenceSolutionPython в test draft.\n"
        )
    if assignment_type == "math":
        return (
            "- Для math обязательны: title, description, settings, blocks.\n"
            f"- Нужно минимум {quality_gates.get('minBlocks', 2)} блока и хотя бы один answer block.\n"
            "- Используй только канонические поля: kind, prompt, promptContentJson, score, isRequired, acceptedAnswers, numericTolerance, orderItems, matchLeftItems, matchRightItems, matchPairs.\n"
            "- Запрещены legacy-алиасы blockType, title, points, promptContent, answers, correctAnswers, items, steps, leftItems, rightItems, pairs.\n"
            "- Не добавляй allowedLanguages, publicTests, hiddenTests и referenceSolutionPython в math draft.\n"
        )
    return (
        "- Для code-test обязательны: title, description, publicTests, hiddenTests, referenceSolutionPython.\n"
        "- allowedLanguages обязателен, если курс/контекст уже ограничивает языки.\n"
        f"- Разрешённые языки для этой генерации: {', '.join(_supported_code_languages(payload))}. Если контекст курса сужает список, не добавляй другие языки.\n"
        f"- Нужно минимум {quality_gates.get('minPublicTests', MIN_PUBLIC_TESTS)} publicTests, минимум {quality_gates.get('minHiddenTests', MIN_HIDDEN_TESTS)} hiddenTests и всего не меньше {quality_gates.get('minTotalTests', max(MIN_PUBLIC_TESTS + MIN_HIDDEN_TESTS, 5))} тестов.\n"
        "- Предпочтительно делать publicTests больше, чем hiddenTests, если это не вредит качеству покрытия.\n"
        "- Используй только root-level requiredCalls и forbiddenCalls. Не вкладывай их в codePolicy.\n"
    )


def _build_code_test_body_prompt(compact_payload: Dict[str, Any], response_format: str, rules: str) -> str:
    return f"""Ты — TaskForge AI code-test draft body generator. Верни только один валидный JSON без markdown.

Нужно сгенерировать только тело code-test задачи в стиле курса. Название пока НЕ придумывай: поставь в draft.title точную строку __PENDING_TITLE__.
Сначала изучи course style analysis, generation spec, content plan, referenceAssignments и exemplarPack. Только после этого пиши draft.
Подражай стилю курса, но не копируй текст, title и данные дословно.
Верни JSON формата:
{response_format}

Правила:
- description должен быть только обычным текстом, без HTML, без TipTap JSON, без markdown.
- description должен выглядеть как условие из этого курса и сохранять course-native стиль.
- Строго следуй generationSpec.exactTask и generationSpec.ioContract, если они заданы.
- Сначала выполни generationSpec.distinctFromPeers и contentPlan.noveltyHook: новая задача должна заметно отличаться от соседних slot-ов и negative anchors.
{rules}{_pedagogy_appendix(compact_payload)}- Не уходи в другую микроцель: строго соблюдай targetSkill, microGoal и contentPlan.pedagogicalGoal.
- Соблюдай contentPlan.sectionPlan и coursePhraseBank, но не копируй фразы дословно.
- Не используй чужие title из referenceAssignments.
- referenceSolutionPython обязан проходить все publicTests и hiddenTests без подгонки expectedOutput.
- Сохрани placementAfterAssignmentId/placementAfterTitle/placementReason: новая задача должна помнить, после какого существующего задания её лучше вставить в курсе.

Draft body payload:
{_prompt_json(compact_payload)}"""


def _build_test_body_prompt(compact_payload: Dict[str, Any], response_format: str, rules: str) -> str:
    return f"""Ты — TaskForge AI test draft body generator. Верни только один валидный JSON без markdown.

Нужно сгенерировать test-задание в editor-native контракте. Название пока НЕ придумывай: поставь в draft.title точную строку __PENDING_TITLE__.
Сначала изучи style analysis, generation spec, content plan, referenceAssignments и exemplarPack.
Верни JSON формата:
{response_format}

Правила:
- Это не code-test. Не добавляй поля из программирования, языки или тест-кейсы.
- description должен быть только обычным текстом, без HTML, без TipTap JSON, без markdown.
- Каждый вопрос должен быть педагогически осмысленным, без дублей и без пустых заглушек.
{rules}- Сохраняй course-native терминологию и уровень сложности.
- Настройки settings должны быть полными и каноническими.

Draft body payload:
{_prompt_json(compact_payload)}"""


def _build_math_body_prompt(compact_payload: Dict[str, Any], response_format: str, rules: str) -> str:
    return f"""Ты — TaskForge AI math draft body generator. Верни только один валидный JSON без markdown.

Нужно сгенерировать math-задание в editor-native контракте. Название пока НЕ придумывай: поставь в draft.title точную строку __PENDING_TITLE__.
Сначала изучи style analysis, generation spec, content plan, referenceAssignments и exemplarPack.
Верни JSON формата:
{response_format}

Правила:
- Это не code-test. Не добавляй allowedLanguages, publicTests, hiddenTests и referenceSolutionPython.
- description должен быть только обычным текстом, без HTML, без TipTap JSON, без markdown.
- Блоки должны образовывать педагогическую последовательность и не быть случайным набором.
{rules}- Настройки settings и блоки должны быть полными и каноническими.

Draft body payload:
{_prompt_json(compact_payload)}"""


def _build_code_test_generate_prompt(compact_payload: Dict[str, Any], response_format: str, rules: str) -> str:
    return f"""Ты — TaskForge AI code-test draft generator. Верни только один валидный JSON-объект без markdown и без пояснений.

Нужно создать полноценный publishable draft для одной code-test задачи. Ответ обязан иметь top-level ключи schemaVersion и draft.
Не возвращай пустой объект {{}}, не возвращай только sourceText, не возвращай заготовки без обязательных полей.
Если referencePack частично пустой, всё равно собери полноценный draft по brief, qualityGates и targetSchema.

Формат ответа строго такой:
{response_format}

Правила:
- description обязан быть полноценным текстовым условием без HTML-тегов.
{rules}{_pedagogy_appendix(compact_payload)}- Задача должна соответствовать titleHint, targetSkill и microGoal, а не уходить в другой домен.
- Не копируй referenceAssignments дословно.
- referenceSolutionPython должен быть детерминированным и совместимым со всеми test cases без подгонки expectedOutput.
- Сохрани placementAfterAssignmentId/placementAfterTitle/placementReason: выбери существующий anchor из referenceAssignments или верни null.

Draft payload:
{_prompt_json(compact_payload)}"""


def _build_test_generate_prompt(compact_payload: Dict[str, Any], response_format: str, rules: str) -> str:
    return f"""Ты — TaskForge AI test draft generator. Верни только один валидный JSON-объект без markdown и без пояснений.

Нужно создать полноценный publishable draft для одной test-задачи. Ответ обязан иметь top-level ключи schemaVersion и draft.
Это не code-test: не добавляй языки, referenceSolutionPython, publicTests и hiddenTests.

Формат ответа строго такой:
{response_format}

Правила:
- description обязан быть полноценным текстовым условием без HTML-тегов.
{rules}- Не копируй referenceAssignments дословно.
- Вопросы должны быть разнообразными и валидными для editor-контракта.

Draft payload:
{_prompt_json(compact_payload)}"""


def _build_math_generate_prompt(compact_payload: Dict[str, Any], response_format: str, rules: str) -> str:
    return f"""Ты — TaskForge AI math draft generator. Верни только один валидный JSON-объект без markdown и без пояснений.

Нужно создать полноценный publishable draft для одной math-задачи. Ответ обязан иметь top-level ключи schemaVersion и draft.
Это не code-test: не добавляй языки, referenceSolutionPython, publicTests и hiddenTests.

Формат ответа строго такой:
{response_format}

Правила:
- description обязан быть полноценным текстовым условием без HTML-тегов.
{rules}- Не копируй referenceAssignments дословно.
- Блоки должны быть каноническими и пригодными для прямой публикации.

Draft payload:
{_prompt_json(compact_payload)}"""




def build_draft_body_generate_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    assignment_type = str(payload.get("assignmentType") or "code-test").strip().lower()
    compact_payload = compact_payload_for_stage("draft_body_generate", payload)
    response_format = _draft_response_format(payload, assignment_type, include_pending_title=True)
    quality_gates = payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {}
    rules = _draft_type_rules(payload, assignment_type, quality_gates, body_mode=True)
    if assignment_type == "test":
        return _build_test_body_prompt(compact_payload, response_format, rules)
    if assignment_type == "math":
        return _build_math_body_prompt(compact_payload, response_format, rules)
    return _build_code_test_body_prompt(compact_payload, response_format, rules)


def build_draft_generate_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    assignment_type = str(payload.get("assignmentType") or "code-test").strip().lower()
    compact_payload = compact_payload_for_stage("draft_generate", payload)
    response_format = _draft_response_format(payload, assignment_type, include_pending_title=False)
    quality_gates = payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {}
    rules = _draft_type_rules(payload, assignment_type, quality_gates, body_mode=False)
    if assignment_type == "test":
        return _build_test_generate_prompt(compact_payload, response_format, rules)
    if assignment_type == "math":
        return _build_math_generate_prompt(compact_payload, response_format, rules)
    return _build_code_test_generate_prompt(compact_payload, response_format, rules)


def build_draft_title_generate_prompt(job: Dict[str, Any], payload: Dict[str, Any], draft: Dict[str, Any]) -> str:
    compact_payload = compact_payload_for_stage("draft_title_generate", payload)
    draft_brief = {
        "assignmentType": draft.get("assignmentType") or payload.get("assignmentType"),
        "description": truncate_text(draft.get("description") or "", 2400),
        "targetSkill": payload.get("targetSkill") or (payload.get("brief") or {}).get("targetSkill") if isinstance(payload.get("brief"), dict) else payload.get("targetSkill"),
        "microGoal": payload.get("microGoal") or (payload.get("brief") or {}).get("summary") if isinstance(payload.get("brief"), dict) else payload.get("microGoal"),
    }
    title_style = compact_payload.get("titleStyle") if isinstance(compact_payload.get("titleStyle"), dict) else {}
    pattern = truncate_text(title_style.get("pattern") or "Короткое course-native название", 160)
    examples = [truncate_text(x, 64) for x in list(title_style.get("examples") or []) if truncate_text(x, 64)][:6]
    examples_block = "; ".join(examples) if examples else "нет явных примеров"
    return (
        'Ты — TaskForge AI title generator. Верни только JSON без markdown вида {"title":"..."}.\n\n'
        'Придумай короткий, человеческий, course-native title для задания. '
        'Ориентируйся на стиль курса: обычно это 2-4 слова, без служебных слов и без учебникового пафоса. '
        'Не используй заглушки, не копируй дословно titles из referenceAssignments, '
        "не пиши слишком общие названия вроде 'Новая задача' или 'Задание по теме', "
        "избегай хвостов вроде 'с префиксом', 'с фиксированным форматом', 'статистика', 'версия', 'draft'.\n\n"
        f"Style pattern: {pattern}\n"
        f"Style examples: {examples_block}\n\n"
        f"Payload:\n{_prompt_json(compact_payload)}\n\n"
        f"Draft summary:\n{_prompt_json(draft_brief)}"
    )


def build_draft_title_repair_prompt(job: Dict[str, Any], payload: Dict[str, Any], draft: Dict[str, Any], bad_title: str) -> str:
    compact_payload = compact_payload_for_stage("draft_title_repair", payload)
    draft_brief = {
        "assignmentType": draft.get("assignmentType") or payload.get("assignmentType"),
        "description": truncate_text(draft.get("description") or "", 2400),
        "currentBadTitle": bad_title,
    }
    title_style = compact_payload.get("titleStyle") if isinstance(compact_payload.get("titleStyle"), dict) else {}
    examples = [truncate_text(x, 64) for x in list(title_style.get("examples") or []) if truncate_text(x, 64)][:6]
    examples_block = "; ".join(examples) if examples else "нет явных примеров"
    return (
        'Ты — TaskForge AI title repair generator. Верни только JSON без markdown вида {"title":"..."}.\n\n'
        'Текущий title плохой: слишком общий, служебный, пустой или не в стиле курса. '
        'Исправь title так, чтобы он был коротким, естественным и отражал суть задания. '
        "Цель: 2-4 слова, нейтрально, без 'задача на', 'с префиксом', 'фиксированный формат', 'статистика', 'draft'.\n\n"
        f"Style examples: {examples_block}\n\n"
        f"Payload:\n{_prompt_json(compact_payload)}\n\n"
        f"Draft summary:\n{_prompt_json(draft_brief)}"
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
