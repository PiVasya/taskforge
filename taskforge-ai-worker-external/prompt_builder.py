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

from config import MIN_PUBLIC_TESTS, MIN_HIDDEN_TESTS, MIN_TOTAL_TESTS, MIN_DESCRIPTION_LEN, MAX_HIDDEN_TESTS
from log import log
from text_utils import normalize_text, truncate_text, safe_int, unique_string_list, strip_html_to_text, summarize_description
from scenario_router import detect_scenario_profile, scenario_prompt_appendix, scenario_requires_explicit_if
from ladder_style import ladder_style_appendix
from ladder_style import ladder_style_appendix
from scenario_policy import scenario_should_bypass_blueprint
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
    agent_state = batch_memory.get("agentState") if isinstance(batch_memory.get("agentState"), dict) else {}
    anchor_context = compact_payload.get("anchorContext") if isinstance(compact_payload.get("anchorContext"), dict) else {}
    task_format = normalize_text(task.get("taskFormat") or task.get("learningMode")).lower()
    lines: List[str] = []
    if batch_memory.get("placementPlan"):
        lines.append("- В batchMemory уже есть placementPlan из чата/аудита курса: не игнорируй его и не придумывай тему с нуля.")
    if agent_state.get("userIntentSummary"):
        lines.append(f"- Каноническая цель агента: {truncate_text(agent_state.get('userIntentSummary'), 180)}. Сохраняй именно этот учебный замысел до конца генерации.")
    if batch_memory.get("latestIntentKind"):
        lines.append(f"- Последний явный режим запроса пользователя: {truncate_text(batch_memory.get('latestIntentKind'), 80)}.")
    if batch_memory.get("latestExplicitInstruction"):
        lines.append(f"- Последняя явная инструкция пользователя: {truncate_text(batch_memory.get('latestExplicitInstruction'), 220)}.")
    if batch_memory.get("latestTeachingScript"):
        lines.append("- В batchMemory уже есть почти готовый teaching-script. Не сворачивай его в общую педагогическую цель и не теряй объяснения по строкам кода.")
    if agent_state.get("nextSuggestedAction"):
        lines.append(f"- Следующий шаг агента по state: {truncate_text(agent_state.get('nextSuggestedAction'), 140)}.")
    if agent_state.get("placementCandidates"):
        lines.append("- В agentState уже есть placementCandidates: сначала проверь их, и только потом изобретай новую точку вставки.")
    if anchor_context.get("anchorTitle"):
        lines.append(f"- У тебя есть anchorContext: новая задача должна логично идти после «{anchor_context.get('anchorTitle')}» и учитывать соседние задания вокруг этого места курса.")
    possible_duplicates = anchor_context.get("possibleDuplicates") if isinstance(anchor_context.get("possibleDuplicates"), list) else []
    if possible_duplicates:
        dup_titles = [truncate_text((item or {}).get("title"), 72) for item in possible_duplicates if isinstance(item, dict) and truncate_text((item or {}).get("title"), 72)]
        if dup_titles:
            lines.append(f"- Особенно не дублируй существующие задания: {'; '.join(dup_titles[:4])}.")
    duplicate_signature_hints = anchor_context.get("duplicateSignatureHints") if isinstance(anchor_context.get("duplicateSignatureHints"), list) else []
    if duplicate_signature_hints:
        dup_sig_titles = [truncate_text((item or {}).get("title"), 72) for item in duplicate_signature_hints if isinstance(item, dict) and truncate_text((item or {}).get("title"), 72)]
        if dup_sig_titles:
            lines.append(f"- Signature-based duplicate hints тоже указывают на риск повтора: {'; '.join(dup_sig_titles[:3])}. Сделай задачу заметно отличимой по формулировке, входу/выходу и тестам.")
    duplicate_clusters = anchor_context.get("duplicateClustersPreview") if isinstance(anchor_context.get("duplicateClustersPreview"), list) else []
    if duplicate_clusters:
        cluster_titles = []
        for cluster in duplicate_clusters[:2]:
            if not isinstance(cluster, dict):
                continue
            for member in (cluster.get("members") if isinstance(cluster.get("members"), list) else [])[:2]:
                if isinstance(member, dict) and truncate_text(member.get("title"), 72):
                    cluster_titles.append(truncate_text(member.get("title"), 72))
        if cluster_titles:
            lines.append(f"- Duplicate clusters показывают группы похожих задач рядом: {'; '.join(cluster_titles[:4])}. Не попадай в этот же кластер и не копируй его структуру.")
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


def _instruction_strictness(payload: Dict[str, Any]) -> int:
    candidates = [
        payload.get("instructionStrictness"),
        ((payload.get("memory") or {}) if isinstance(payload.get("memory"), dict) else {}).get("instructionStrictness"),
        ((payload.get("batchMemory") or {}) if isinstance(payload.get("batchMemory"), dict) else {}).get("instructionStrictness"),
    ]
    for raw in candidates:
        try:
            if raw is None or raw == "":
                continue
            return max(0, min(100, int(raw)))
        except Exception:
            continue
    return 55


def _instruction_text_sources(payload: Dict[str, Any]) -> List[str]:
    texts: List[str] = []
    for value in [
        payload.get("prompt"),
        payload.get("sourceText"),
        payload.get("notes"),
        payload.get("userInstructionSnapshot"),
        payload.get("teachingScript"),
    ]:
        norm = normalize_text(value)
        if norm:
            texts.append(norm)
    for container_key in ("memory", "batchMemory"):
        container = payload.get(container_key) if isinstance(payload.get(container_key), dict) else {}
        for value in [container.get("latestExplicitInstruction"), container.get("latestTeachingScript")]:
            norm = normalize_text(value)
            if norm:
                texts.append(norm)
    return texts


def _extract_instruction_contract(payload: Dict[str, Any]) -> Dict[str, Any]:
    texts = _instruction_text_sources(payload)
    joined = "\n".join(texts)
    low = joined.lower()
    exact: List[str] = []
    forbidden: List[str] = []

    def _push_unique(bucket: List[str], value: str, limit: int = 8) -> None:
        item = normalize_text(value).strip("`'\" ")
        if not item or len(item) < 2:
            return
        item = truncate_text(item, 160)
        if item.casefold() in {x.casefold() for x in bucket}:
            return
        if len(bucket) < limit:
            bucket.append(item)

    for match in re.findall(r"`([^`]{2,160})`", joined):
        _push_unique(exact, match)
    for match in re.findall(r"[«\"]([^\n\"]{2,160})[»\"]", joined):
        if any(ch in match for ch in "#;<>:{}()[]"):
            _push_unique(exact, match)
    for text_block in texts:
        for raw_line in text_block.splitlines():
            line = raw_line.strip()
            if not line:
                continue
            candidate = re.sub(r"^(шаг\s*\d+[:.)-]?|напиши(?:те)?[:\s-]*|введите[:\s-]*|сделай(?:те)?[:\s-]*)", "", line, flags=re.IGNORECASE).strip()
            if 2 <= len(candidate) <= 160 and any(token in candidate for token in ["#", ";", "<", ">", "{", "}", "(", ")", "::"]):
                _push_unique(exact, candidate)
    forbid_patterns = [
        r"(?:без|не надо|не нужно|не использовать|не используй|не добавляй|не пиши|убери|исключи)\s+([^\n\.,!?:;]{1,80})",
        r"([^\n\.,!?:;]{1,80})\s+не надо",
        r"([^\n\.,!?:;]{1,80})\s+ненадо",
    ]
    for pattern in forbid_patterns:
        for match in re.findall(pattern, joined, flags=re.IGNORECASE):
            _push_unique(forbidden, match, limit=10)
    preserve_order = any(token in low for token in ["по шагам", "шаг за шагом", "в том же порядке", "тот же порядок", "1 в 1", "один в один", "строго по примеру", "буквально", "именно так"])
    lock_scope = any(token in low for token in ["именно", "строго", "точно", "ровно", "без новых", "не добавляй нового", "не вводи новую тему"]) or preserve_order
    return {
        "exactSnippets": exact[:8],
        "forbiddenSnippets": forbidden[:10],
        "preserveOrder": preserve_order,
        "lockScope": lock_scope,
    }


def _instruction_fidelity_appendix(payload: Dict[str, Any]) -> str:
    strictness = _instruction_strictness(payload)
    contract = _extract_instruction_contract(payload)
    lines = [f"- Текущая строгость следования пользовательской инструкции: {strictness}/100."]
    if strictness <= 20:
        lines.append("- Свободный режим: можно смелее интерпретировать intent, предлагать улучшения и не держаться буквально за форму примеров.")
    elif strictness <= 69:
        lines.append("- Сбалансированный режим: сохраняй основную мысль пользователя, но можешь аккуратно упрощать, нормализовать и улучшать формулировки.")
    else:
        lines.append("- Строгий режим: приоритет №1 — не потерять пользовательскую мысль, не расширить scope и не подменить задачу своей интерпретацией.")
        lines.append("- Если пользователь дал пример, эталон, teaching-script или список шагов, считай это контрактом, а не просто вдохновением.")
        lines.append("- Не вводи новые учебные сущности, новые ограничения, новый формат IO или новый pedagogical scope без прямого запроса пользователя.")
        lines.append("- Когда есть выбор между креативностью и буквальным следованием инструкции, выбирай буквальное следование инструкции.")
        if contract.get("lockScope"):
            lines.append("- В этом запросе есть маркеры буквального следования. Не сдвигай микроцель и не уезжай в соседнюю тему.")
        if contract.get("preserveOrder"):
            lines.append("- Сохрани порядок шагов/примеров пользователя. Не переставляй их местами без жёсткой необходимости.")
        exact = contract.get("exactSnippets") if isinstance(contract.get("exactSnippets"), list) else []
        forbidden = contract.get("forbiddenSnippets") if isinstance(contract.get("forbiddenSnippets"), list) else []
        if exact:
            lines.append("- Сохрани следующие явные пользовательские фрагменты дословно там, где они уместны: " + "; ".join(exact[:6]))
        if forbidden:
            lines.append("- Не нарушай явно заданные пользователем запреты/исключения: " + "; ".join(forbidden[:6]))
    return "\n".join(lines) + "\n"


def _style_exemplar_appendix(payload: Dict[str, Any]) -> str:
    anchor_context = payload.get("anchorContext") if isinstance(payload.get("anchorContext"), dict) else {}
    exemplars = anchor_context.get("styleExemplarAssignments") if isinstance(anchor_context.get("styleExemplarAssignments"), list) else []
    exact_requested = bool(anchor_context.get("exactStyleRequested"))
    approved = payload.get("approvedBlueprint") if isinstance(payload.get("approvedBlueprint"), dict) else {}
    if not exemplars and not exact_requested and not approved:
        return ""
    lines: List[str] = []
    if exact_requested:
        lines.append("- Пользователь просит максимально близко повторить стиль уже существующего задания. Не усредняй стиль по всему курсу, а ориентируйся на конкретный эталон.")
    if approved:
        blueprint_title = truncate_text(approved.get("title"), 80)
        if blueprint_title:
            lines.append(f"- Одобренный blueprint «{blueprint_title}» — это жёсткий контракт. Нельзя превращать его в более абстрактное или более взрослое условие.")
        if truncate_text(approved.get("fullCondition"), 260):
            lines.append("- approvedBlueprint.fullCondition — это канонический skeleton будущего условия. Сохрани его ритм, scaffold и ключевые фразы, а не пересказывай своими словами в общем виде.")
        must_keep = approved.get("mustKeep") if isinstance(approved.get("mustKeep"), list) else []
        avoid = approved.get("avoid") if isinstance(approved.get("avoid"), list) else []
        if must_keep:
            lines.append("- Обязательно сохранить: " + "; ".join(str(item) for item in must_keep[:6]) + ".")
        if avoid:
            lines.append("- Нельзя добавлять: " + "; ".join(str(item) for item in avoid[:6]) + ".")
    if exemplars:
        titles = [truncate_text((item or {}).get("title"), 80) for item in exemplars if isinstance(item, dict) and truncate_text((item or {}).get("title"), 80)]
        if titles:
            lines.append("- Стилевые эталоны из курса: " + "; ".join(titles[:4]) + ".")
        snippets = [truncate_text((item or {}).get("descriptionSummary"), 120) for item in exemplars if isinstance(item, dict) and truncate_text((item or {}).get("descriptionSummary"), 120)]
        if snippets:
            lines.append("- По этим эталонам видно, как выглядит подача: " + " | ".join(snippets[:2]) + ".")
        lines.append("- Сохрани у эталона тон, порядок подачи, формат коротких шагов и уровень подробности. Меняй только учебную сущность, которую попросил пользователь.")
        lines.append("- Если эталон выглядит как пошаговая обучалка, повтори scaffold почти дословно: короткое вступление, затем «Следуй шагам», затем простые пояснения без лишней теории.")
        lines.append("- Для такого эталона обычно нужен warm title, затем дружелюбная первая фраза, отдельная строка «Следуй шагам:», 3-6 коротких шагов, маленькие пояснения в скобках и финальная фраза про запуск/видимый результат.")
    if exact_requested or approved:
        lines.append("- Запрещены авторские комментарии-паразиты вроде «это самый простой способ», «это база для», «цель — показать», «покажи, что», если их нет в эталоне.")
        lines.append("- Не превращай шаги в сухой конспект. Для beginner-style заданий держи дружелюбное вступление, отдельную строку «Следуй шагам:» и нумерованные короткие шаги.")
        lines.append("- Не заменяй конкретный scaffold на обезличенные секции «Условие / Входные данные / Выходные данные», если эталон и approved blueprint построены иначе.")
        lines.append("- Не пиши мета-команды вида «объясни, что ...» внутри условия. Вместо этого само условие должно уже содержать короткое человеческое пояснение, как в эталоне.")
        lines.append("- Не используй фразы «компьютер не знает», «на низком уровне», «самый простой способ», если пользователь не просил такого тона отдельно.")
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
            f"description должен быть не короче {quality.get('minDescriptionLength', MIN_DESCRIPTION_LEN)} символов.",
            "В description обязательно раскрой: суть задачи, формат входных данных, формат выходных данных, ограничения, хотя бы одну заметку или пояснение.",
            "referenceSolutionPython должен быть полностью рабочим, детерминированным, читать stdin и печатать только ответ.",
            "Сгенерируй edge cases: минимальные значения, типичные значения, пограничные случаи.",
            "Количество publicTests и hiddenTests выбирай динамически под конкретную задачу, а не по шаблону.",
            "Не делай все тесты однотипными и не копируй один и тот же IO-контракт между соседними вариантами.",
            "Предпочтительно publicTests делать больше, чем hiddenTests, если это не конфликтует с учебной целью.",
            "Если задача требует ограничений по коду, добавь forbiddenCalls и/или requiredCalls как массивы строк.",
        ])
        if 'minPublicTests' in quality:
            lines.append(f"Соблюдай нижнюю границу publicTests из qualityGates: {quality.get('minPublicTests')}.")
        if 'minHiddenTests' in quality:
            lines.append(f"Соблюдай нижнюю границу hiddenTests из qualityGates: {quality.get('minHiddenTests')}.")
        if 'minTotalTests' in quality:
            lines.append(f"Суммарное количество тестов не должно быть меньше {quality.get('minTotalTests')}.")
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
        return """Проанализируй существующее задание как единицу курса и верни ПОЛЕЗНЫЙ persisted overview для будущего course-agent. 
Пиши ТОЛЬКО по-русски: summary, importanceReasons, courseValue и suggestions должны быть на русском языке без английских вставок. 
Калибруй важность относительно ВСЕГО курса, а не в вакууме: isImportant=true ставь только если задание действительно вводит новую тему, является guided-intro, bridge, milestone, assessment или ключевой опорной точкой. Обычным drill/reference не завышай важность. 
importanceScore используй осознанно: 0.2-0.45 = слабая опорная ценность, 0.5-0.69 = полезное, но не ключевое, 0.7-0.84 = заметно важное, 0.85-0.95 = реально опорное задание курса. Не ставь почти всем заданиям 0.75+ и isImportant=true. 
Если задание уже само является пошаговой обучалкой для новичка, это guided-intro, а не 'пробел' курса. Если это просто вариация уже введённого паттерна, чаще всего это skill-drill или reference. 
Верни JSON вида: {"assignmentId":"...","kind":"course-overview","summary":"...","overview":{"isImportant":true,"importanceScore":0.0,"importanceReasons":["..."],"pedagogicalRole":"guided-intro|bridge|skill-drill|milestone|assessment|reference","teachingStyle":"step-by-step|theory-first|practice-first|mixed","studentStage":"absolute-beginner|beginner|intermediate|advanced","conceptsIntroduced":["..."],"conceptsReinforced":["..."],"prerequisites":["..."],"surfaceSignals":["cout","cin"],"courseValue":"..."},"suggestions":["...","..."]}"""

    if t == "assignment_repair":
        route_directive = _repair_route_directive(payload)
        return (
            "Исправь существующий draft по findings и reviewResults. "
            "Верни только JSON вида {\"draft\": {...}, \"repairSummary\": \"...\"}. "
            "Не придумывай новую тему без причины: сохрани ядро задания, "
            "но исправь формулировку, тесты, solution и policy там, где review нашёл проблемы.\n"
            f"Route-aware repair directive: {route_directive}"
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


def _compact_overview_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    reasons = value.get("importanceReasons") if isinstance(value.get("importanceReasons"), list) else []
    concepts_intro = value.get("conceptsIntroduced") if isinstance(value.get("conceptsIntroduced"), list) else []
    concepts_reinf = value.get("conceptsReinforced") if isinstance(value.get("conceptsReinforced"), list) else []
    signals = value.get("signals") if isinstance(value.get("signals"), list) else (value.get("surfaceSignals") if isinstance(value.get("surfaceSignals"), list) else [])
    return {
        "isImportant": bool(value.get("isImportant")),
        "importanceScore": value.get("importanceScore"),
        "pedagogicalRole": truncate_text(value.get("pedagogicalRole"), 40),
        "teachingStyle": truncate_text(value.get("teachingStyle"), 40),
        "studentStage": truncate_text(value.get("studentStage"), 40),
        "importanceReasons": [truncate_text(x, 90) for x in reasons[:2] if normalize_text(x)],
        "conceptsIntroduced": [truncate_text(x, 48) for x in concepts_intro[:4] if normalize_text(x)],
        "conceptsReinforced": [truncate_text(x, 48) for x in concepts_reinf[:3] if normalize_text(x)],
        "signals": [truncate_text(x, 32) for x in signals[:4] if normalize_text(x)],
        "courseValue": truncate_text(value.get("courseValue"), 120),
        "summary": truncate_text(value.get("summary"), 140),
    }


def _compact_assignment_like_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    item = {
        "id": value.get("id"),
        "courseId": value.get("courseId"),
        "sort": value.get("sort"),
        "title": truncate_text(value.get("title"), 100),
        "type": truncate_text(value.get("type"), 32),
        "difficulty": value.get("difficulty"),
        "updatedAtUtc": value.get("updatedAtUtc"),
    }
    overview = _compact_overview_for_chat(value.get("latestAiOverview") if isinstance(value.get("latestAiOverview"), dict) else value.get("aiOverview") if isinstance(value.get("aiOverview"), dict) else value.get("AiOverview") if isinstance(value.get("AiOverview"), dict) else None)
    if overview:
        item["overview"] = overview
    return item


def _compact_draft_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    return {
        "id": value.get("id"),
        "courseId": value.get("courseId"),
        "batchId": value.get("batchId"),
        "title": truncate_text(value.get("title"), 100),
        "assignmentType": truncate_text(value.get("assignmentType"), 32),
        "status": truncate_text(value.get("status"), 32),
        "updatedAtUtc": value.get("updatedAtUtc"),
    }


def _compact_job_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    return {
        "id": value.get("id"),
        "type": truncate_text(value.get("type"), 48),
        "status": truncate_text(value.get("status"), 24),
        "stageCode": truncate_text(value.get("stageCode"), 40),
        "priority": value.get("priority"),
        "targetEntityType": truncate_text(value.get("targetEntityType"), 32),
        "targetEntityId": value.get("targetEntityId"),
        "createdAtUtc": value.get("createdAtUtc"),
        "completedAtUtc": value.get("completedAtUtc"),
        "errorText": truncate_text(value.get("errorText"), 120),
    }


def _compact_tool_result_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    item = {
        "status": truncate_text(value.get("status"), 18),
        "summary": truncate_text(value.get("summary"), 220),
        "batchId": value.get("batchId"),
        "draftId": value.get("draftId"),
        "assignmentId": value.get("assignmentId"),
        "courseId": value.get("courseId"),
    }
    if value.get("requiresConfirmation"):
        item["requiresConfirmation"] = True
    return item


def _compact_conversation_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    return {
        "role": truncate_text(value.get("role"), 12),
        "content": truncate_text(value.get("content"), 240 if normalize_text((value or {}).get("__compactMode")).lower() == "ultra" else 420),
        "status": truncate_text(value.get("status"), 24),
        "createdAtUtc": value.get("createdAtUtc"),
        "toolCalls": [
            {
                "name": truncate_text(x.get("name"), 48),
                "reason": truncate_text(x.get("reason"), 160),
            }
            for x in ((value.get("toolCalls") if isinstance(value.get("toolCalls"), list) else [])[:2]) if isinstance(x, dict)
        ],
        "toolResults": [
            _compact_tool_result_for_chat(x)
            for x in ((value.get("toolResults") if isinstance(value.get("toolResults"), list) else [])[:2]) if isinstance(x, dict)
        ],
    }


def _compact_blueprint_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    proposals = value.get("proposals") if isinstance(value.get("proposals"), list) else []
    compact_props = []
    for item in proposals[:4]:
        if not isinstance(item, dict):
            continue
        public_tests = item.get("publicTests") if isinstance(item.get("publicTests"), list) else []
        hidden_tests = item.get("hiddenTests") if isinstance(item.get("hiddenTests"), list) else []
        compact_props.append({
            "id": item.get("id"),
            "title": truncate_text(item.get("title"), 90),
            "conditionPreview": truncate_text(item.get("conditionPreview"), 220),
            "fullCondition": truncate_text(item.get("fullCondition"), 420),
            "assignmentType": truncate_text(item.get("assignmentType"), 24),
            "difficulty": item.get("difficulty"),
            "placementAfterTitle": truncate_text(item.get("placementAfterTitle"), 80),
            "placementReason": truncate_text(item.get("placementReason"), 140),
            "publicTests": [{"input": truncate_text(x.get("input"), 60), "expectedOutput": truncate_text(x.get("expectedOutput"), 80)} for x in public_tests[:3] if isinstance(x, dict)],
            "hiddenTests": [{"input": truncate_text(x.get("input"), 40), "expectedOutput": truncate_text(x.get("expectedOutput"), 60)} for x in hidden_tests[:2] if isinstance(x, dict)],
        })
    return {
        "revision": value.get("revision"),
        "approvedForDraft": bool(value.get("approvedForDraft")),
        "proposalCount": len(proposals),
        "proposals": compact_props,
    }


def _compact_agent_state_for_chat(value: Any) -> Dict[str, Any] | None:
    if not isinstance(value, dict):
        return None
    evidence = value.get("evidenceLedger") if isinstance(value.get("evidenceLedger"), list) else []
    risks = value.get("riskFlags") if isinstance(value.get("riskFlags"), list) else []
    open_questions = value.get("openQuestions") if isinstance(value.get("openQuestions"), list) else []
    decision_candidates = value.get("decisionCandidates") if isinstance(value.get("decisionCandidates"), list) else []
    plan_steps = value.get("planSteps") if isinstance(value.get("planSteps"), list) else []
    placement = value.get("placementCandidates") if isinstance(value.get("placementCandidates"), list) else []
    return {
        "objectiveKind": truncate_text(value.get("objectiveKind"), 40),
        "objectiveSummary": truncate_text(value.get("objectiveSummary"), 180),
        "currentStage": truncate_text(value.get("currentStage"), 40),
        "stageSummary": truncate_text(value.get("stageSummary"), 180),
        "latestIntentKind": truncate_text(value.get("latestIntentKind"), 32),
        "nextSuggestedAction": truncate_text(value.get("nextSuggestedAction"), 48),
        "confidencePercent": value.get("confidencePercent"),
        "confidenceReason": truncate_text(value.get("confidenceReason"), 120),
        "blockerSummary": truncate_text(value.get("blockerSummary"), 120),
        "needsClarification": bool(value.get("needsClarification")),
        "autonomyMode": truncate_text(value.get("autonomyMode"), 24),
        "evidenceLedger": [truncate_text(x, 120) for x in evidence[:6] if normalize_text(x)],
        "riskFlags": [truncate_text(x, 100) for x in risks[:4] if normalize_text(x)],
        "openQuestions": [truncate_text(x, 100) for x in open_questions[:4] if normalize_text(x)],
        "decisionCandidates": [
            {
                "name": truncate_text(x.get("name"), 48),
                "status": truncate_text(x.get("status"), 16),
                "why": truncate_text(x.get("why"), 120),
            }
            for x in decision_candidates[:5] if isinstance(x, dict)
        ],
        "planSteps": [
            {
                "key": truncate_text(x.get("key"), 24),
                "status": truncate_text(x.get("status"), 16),
                "title": truncate_text(x.get("title"), 70),
                "recommendedAction": truncate_text(x.get("recommendedAction"), 48),
                "successSignal": truncate_text(x.get("successSignal"), 100),
            }
            for x in plan_steps[:5] if isinstance(x, dict)
        ],
        "placementCandidates": [
            {
                "afterAssignmentId": x.get("afterAssignmentId"),
                "afterAssignmentTitle": truncate_text(x.get("afterAssignmentTitle"), 80),
                "why": truncate_text(x.get("why"), 100),
            }
            for x in placement[:4] if isinstance(x, dict)
        ],
    }


def _compact_memory_for_chat(value: Any) -> Dict[str, Any]:
    if not isinstance(value, dict):
        return {}
    facts = value.get("facts") if isinstance(value.get("facts"), list) else []
    goals = value.get("recentGoals") if isinstance(value.get("recentGoals"), list) else []
    actions = value.get("recentActions") if isinstance(value.get("recentActions"), list) else []
    memory = {
        "summary": truncate_text(value.get("summary"), 260),
        "latestIntentKind": truncate_text(value.get("latestIntentKind"), 32),
        "latestExplicitInstruction": truncate_text(value.get("latestExplicitInstruction"), 220),
        "latestTeachingScript": truncate_text(value.get("latestTeachingScript"), 260),
        "preferAutonomousCompletion": bool(value.get("preferAutonomousCompletion")),
        "suppressBridgePlanLoop": bool(value.get("suppressBridgePlanLoop")),
        "facts": [truncate_text(x, 120) for x in facts[:6] if normalize_text(x)],
        "recentGoals": [truncate_text(x, 120) for x in goals[-5:] if normalize_text(x)],
        "recentActions": [truncate_text(x, 48) for x in actions[-6:] if normalize_text(x)],
    }
    if isinstance(value.get("lastCourseAudit"), dict):
        audit = value.get("lastCourseAudit")
        memory["lastCourseAudit"] = {
            "summary": truncate_text(audit.get("summary"), 220),
            "courseId": audit.get("courseId"),
            "findingsCount": len(audit.get("findings") or []) if isinstance(audit.get("findings"), list) else None,
            "styleHints": [truncate_text(x, 60) for x in (audit.get("styleHints") if isinstance(audit.get("styleHints"), list) else [])[:4] if normalize_text(x)],
        }
    if isinstance(value.get("lastCourseInspection"), dict):
        inspection = value.get("lastCourseInspection")
        observations = inspection.get("observations") if isinstance(inspection.get("observations"), list) else []
        inspected = inspection.get("inspectedAssignments") if isinstance(inspection.get("inspectedAssignments"), list) else []
        memory["lastCourseInspection"] = {
            "summary": truncate_text(inspection.get("summary"), 220),
            "courseId": inspection.get("courseId"),
            "observations": [truncate_text(x, 120) for x in observations[:5] if normalize_text(x)],
            "inspectedAssignments": [_compact_assignment_like_for_chat(x) for x in inspected[:6] if isinstance(x, dict)],
        }
    if isinstance(value.get("lastBridgePlan"), dict):
        plan = value.get("lastBridgePlan")
        items = plan.get("items") if isinstance(plan.get("items"), list) else []
        memory["lastBridgePlan"] = {
            "summary": truncate_text(plan.get("summary"), 220),
            "status": truncate_text(plan.get("status"), 24),
            "itemCount": len(items),
            "items": [
                {
                    "index": x.get("index"),
                    "titleHint": truncate_text(x.get("titleHint"), 80),
                    "afterAssignmentTitle": truncate_text(x.get("afterAssignmentTitle"), 80),
                    "why": truncate_text(x.get("why"), 120),
                    "confirmed": bool(x.get("confirmed")),
                    "rejected": bool(x.get("rejected")),
                }
                for x in items[:4] if isinstance(x, dict)
            ],
        }
    return memory


def build_chat_turn_prompt(job: Dict[str, Any], payload: Dict[str, Any]) -> str:
    raw_memory = payload.get("memory") if isinstance(payload.get("memory"), dict) else {}
    raw_blueprint = payload.get("currentDraftBlueprint") if isinstance(payload.get("currentDraftBlueprint"), dict) else (raw_memory.get("currentDraftBlueprint") if isinstance(raw_memory.get("currentDraftBlueprint"), dict) else None)
    compact_mode = normalize_text(payload.get("__compactMode")).lower()
    ultra = compact_mode == "ultra"
    compact = compact_mode in {"compact", "ultra"}
    conversation_limit = 4 if ultra else (6 if compact else 8)
    recent_assignments_limit = 3 if ultra else (4 if compact else 6)
    landmarks_limit = 3 if ultra else (4 if compact else 6)
    drafts_limit = 3 if ultra else (4 if compact else 6)
    jobs_limit = 4 if ultra else (6 if compact else 8)
    users_limit = 5 if ultra else (6 if compact else 8)
    attempts_limit = 5 if ultra else (6 if compact else 8)
    courses_limit = 8 if ultra else 12
    actions_limit = 10 if ultra else 16
    compact_payload = {
        "sessionId": payload.get("sessionId"),
        "sessionTitle": truncate_text(payload.get("sessionTitle"), 90 if compact else 120),
        "courseId": payload.get("courseId"),
        "selectedCourse": {
            "id": payload.get("selectedCourse", {}).get("id"),
            "title": truncate_text(payload.get("selectedCourse", {}).get("title"), 100),
            "assignmentCount": payload.get("selectedCourse", {}).get("assignmentCount"),
        } if isinstance(payload.get("selectedCourse"), dict) else None,
        "memory": _compact_memory_for_chat(raw_memory),
        "currentDraftBlueprint": _compact_blueprint_for_chat(raw_blueprint),
        "agentState": _compact_agent_state_for_chat(raw_memory.get("agentState") if isinstance(raw_memory.get("agentState"), dict) else {}),
        "conversation": [_compact_conversation_for_chat(x) for x in (payload.get("conversation")[-conversation_limit:] if isinstance(payload.get("conversation"), list) else []) if isinstance(x, dict)],
        "recentAttachments": [
            {
                "originalName": truncate_text(x.get("originalName"), 80),
                "mimeType": truncate_text(x.get("mimeType"), 40),
                "sizeBytes": x.get("sizeBytes"),
                "hasTextExcerpt": bool(x.get("hasTextExcerpt")),
            }
            for x in ((payload.get("recentAttachments")[-6:] if isinstance(payload.get("recentAttachments"), list) else [])) if isinstance(x, dict)
        ],
        "recentAssignments": [_compact_assignment_like_for_chat(x) for x in ((payload.get("recentAssignments")[:recent_assignments_limit] if isinstance(payload.get("recentAssignments"), list) else [])) if isinstance(x, dict)],
        "courseOverviewCoverage": payload.get("courseOverviewCoverage") if isinstance(payload.get("courseOverviewCoverage"), dict) else None,
        "landmarkAssignments": [_compact_assignment_like_for_chat(x) for x in ((payload.get("landmarkAssignments")[:landmarks_limit] if isinstance(payload.get("landmarkAssignments"), list) else [])) if isinstance(x, dict)],
        "autoOverviewBootstrap": payload.get("autoOverviewBootstrap") if isinstance(payload.get("autoOverviewBootstrap"), dict) else None,
        "recentDrafts": [_compact_draft_for_chat(x) for x in ((payload.get("recentDrafts")[:drafts_limit] if isinstance(payload.get("recentDrafts"), list) else [])) if isinstance(x, dict)],
        "sessionRecentDrafts": [_compact_draft_for_chat(x) for x in ((payload.get("sessionRecentDrafts")[:drafts_limit] if isinstance(payload.get("sessionRecentDrafts"), list) else [])) if isinstance(x, dict)],
        "recentBatches": [
            {
                "id": x.get("id"),
                "courseId": x.get("courseId"),
                "assignmentType": truncate_text(x.get("assignmentType"), 24),
                "mode": truncate_text(x.get("mode"), 24),
                "requestedCount": x.get("requestedCount"),
                "status": truncate_text(x.get("status"), 24),
                "currentStage": truncate_text(x.get("currentStage"), 48),
                "updatedAtUtc": x.get("updatedAtUtc"),
                "prompt": truncate_text(x.get("prompt"), 160),
            }
            for x in ((payload.get("recentBatches")[:(3 if compact else 4)] if isinstance(payload.get("recentBatches"), list) else [])) if isinstance(x, dict)
        ],
        "recentJobs": [_compact_job_for_chat(x) for x in ((payload.get("recentJobs")[:jobs_limit] if isinstance(payload.get("recentJobs"), list) else [])) if isinstance(x, dict)],
        "sessionRecentJobs": [_compact_job_for_chat(x) for x in ((payload.get("sessionRecentJobs")[:jobs_limit] if isinstance(payload.get("sessionRecentJobs"), list) else [])) if isinstance(x, dict)],
        "recentUsers": [
            {
                "id": x.get("id"),
                "displayName": truncate_text(x.get("displayName") or x.get("email"), 60),
                "role": truncate_text(x.get("role"), 24),
                "lastLoginAtUtc": x.get("lastLoginAtUtc"),
            }
            for x in ((payload.get("recentUsers")[:users_limit] if isinstance(payload.get("recentUsers"), list) else [])) if isinstance(x, dict)
        ],
        "recentAttempts": [
            {
                "sourceType": truncate_text(x.get("sourceType"), 16),
                "assignmentId": x.get("assignmentId"),
                "assignmentTitle": truncate_text(x.get("assignmentTitle"), 90),
                "scorePercent": x.get("scorePercent"),
                "passed": x.get("passed"),
                "submittedAtUtc": x.get("submittedAtUtc"),
            }
            for x in ((payload.get("recentAttempts")[:attempts_limit] if isinstance(payload.get("recentAttempts"), list) else [])) if isinstance(x, dict)
        ],
        "availableCourses": [
            {
                "id": x.get("id"),
                "title": truncate_text(x.get("title"), 80),
            }
            for x in ((payload.get("availableCourses")[:courses_limit] if isinstance(payload.get("availableCourses"), list) else [])) if isinstance(x, dict)
        ],
        "availableActions": [
            {
                "name": truncate_text(x.get("name"), 48),
                "requiredArguments": x.get("requiredArguments")[:4] if isinstance(x.get("requiredArguments"), list) else [],
                "optionalArguments": x.get("optionalArguments")[:5] if isinstance(x.get("optionalArguments"), list) else [],
            }
            for x in (((payload.get("availableActions")[:actions_limit] if isinstance(payload.get("availableActions"), list) else []))) if isinstance(x, dict)
        ],
        "defaults": payload.get("defaults") if isinstance(payload.get("defaults"), dict) else {},
    }
    # Dynamic directives based on conversation state to prevent planning loops
    _dynamic = []
    _conv = compact_payload.get("conversation") or []
    _mem = compact_payload.get("memory") or {}
    _prefer_autonomy = bool(_mem.get("preferAutonomousCompletion"))
    _plan_action_names = {"analyze_course_progression", "inspect_course_assignments", "prepare_bridge_plan", "show_bridge_plan", "revise_bridge_plan"}
    _recent_plan_count = 0
    for _msg in _conv[-8:]:
        if isinstance(_msg, dict):
            for _a in (_msg.get("actions") if isinstance(_msg.get("actions"), list) else []):
                if isinstance(_a, dict) and str(_a.get("name") or "") in _plan_action_names:
                    _recent_plan_count += 1
    if _recent_plan_count >= 3:
        _dynamic.append(
            "КРИТИЧНО: В последних ходах уже было >=3 planning-шагов (audit/inspect/plan/show/revise). "
            "Остановись и либо дай пользователю содержательный итог, либо задай один конкретный вопрос. "
            "Не запускай новые planning-действия без ЯВНОЙ НОВОЙ просьбы пользователя в ЭТОМ ходе."
        )
    if isinstance(_mem.get("lastBridgePlan"), dict) and isinstance(_mem.get("lastCourseAudit"), dict):
        _dynamic.append(
            "У тебя УЖЕ есть и аудит курса, и готовый план мостиков в memory. "
            "Но это не означает, что нужно автоматически генерировать. Если пользователь просит показать существующие задания, обсудить проблему или просто уточняет мысль — отвечай по текущему запросу, а не по старому плану."
        )
    _bootstrap = compact_payload.get("autoOverviewBootstrap") if isinstance(compact_payload.get("autoOverviewBootstrap"), dict) else None
    if _bootstrap and int(_bootstrap.get("queuedJobsCount") or 0) > 0:
        _dynamic.append(
            "Для текущего курса уже автоматически поставлены AI-job на assignment overview. "
            "Учитывай это в ответе: можно коротко сказать, что система сама подтягивает обзоры по заданиям в фоне, и не нужно просить пользователя запускать анализ каждого задания вручную."
        )
    if _prefer_autonomy:
        _dynamic.append(
            "КРИТИЧНО: пользователь явно просит автономный проход до удовлетворяющего результата. "
            "Не останавливайся на первом удобном промежуточном ответе, не проси лишнего одобрения и не говори 'посмотри, подходит ли'. "
            "Если в memory уже есть currentDraftBlueprint, но новая инструкция звучит как 'повтори заново', 'сам раскритикуй' или 'исправь полностью', не считай старый blueprint священным: поправь его или замени новым, если он конфликтует с последним запросом. "
            "Blueprint можно использовать как внутреннюю опору, но итоговый assistantMessage должен уже содержать выполненный результат или честное объяснение, чего всё ещё не хватает."
        )
    _scenario = _scenario_profile(compact_payload)
    _skip_blueprint = scenario_should_bypass_blueprint(_scenario, _prefer_autonomy, str((compact_payload.get("conversation") or [{}])[-1].get("content") or "") if isinstance(compact_payload.get("conversation"), list) and compact_payload.get("conversation") else "")
    _blueprint_generation_guidance = (
        "Если пользователь просит создать новое задание или набор задач, сначала собери примерные условия в чате и сохрани их через save_chat_blueprint. Лишь после явного одобрения пользователя переходи к finalize_chat_blueprint. Исключение: если память говорит preferAutonomousCompletion=true и пользователь прямо запретил промежуточные согласования, не застревай на этом UX-этапе — исправляй blueprint сам и иди дальше. "
        "Если пользователь явно говорит «задачи не создавай», «просто наглядно», «сначала покажи схему/лесенку» или просит только концепт без сохранения — не вызывай save_chat_blueprint, finalize_chat_blueprint и prepare_bridge_plan. В таком случае дай короткий человеческий ответ прямо в assistantMessage и оставь actions=[]. "
        if not _skip_blueprint else
        "Для текущего сценария пользователь просит прямой итог без промежуточных вариантов. Не уводи такой запрос в save_chat_blueprint как default UX. Если сценарий генеративный и данных хватает, переходи прямо к queue_generate_from_text и не проси декоративного одобрения. "
    )

    _dynamic_section = ""
    if _dynamic:
        _dynamic_section = "\n\nДинамические директивы (ПРИОРИТЕТНЫЕ):\n" + "\n".join(f"- {d}" for d in _dynamic) + "\n\n"

    # Action-mode instruction based on actionMode payload field
    _action_mode_raw = str(payload.get("actionMode") or "multi").strip().lower()
    if _action_mode_raw in {"single", "mono", "step", "manual"}:
        _action_mode_instruction = (
            "ВАЖНО: Возвращай строго ОДИН action за ход. Никогда не возвращай один и тот же action дважды. "
            "Два разных action в одном ходе допустимы только если это полностью независимые операции, не связанные между собой причинно. "
        )
    else:
        _action_mode_instruction = (
            "ВАЖНО: multi-режим — это один автономный проход агента до состояния, где ответ уже реально удовлетворяет пользовательскому сообщению. "
            "Если для честного ответа нужно сначала найти anchor, потом открыть соседние задания, потом открыть эталон и только после этого собрать новые условия — сделай весь этот цикл сам, а не останавливайся после первого шага. "
            "Разрешены цепочки до 3 действий внутри одного ответа модели, а backend может продолжить ещё несколько внутренних проходов, если после tool-result всё ещё не выполнены completion criteria. "
            "Если пользователь просит 'как первая задача', но у тебя нет подтверждённого эталона первой задачи, сначала открой ранние задания курса и только потом сохраняй blueprint. "
            "Если пользователь явно указал точку вставки ('перед 20 заданием', 'после 7 задания'), все proposals обязаны держать именно этот anchor; нельзя молча переносить их в другое место. "
            "Если пользователь попросил конкретное количество задач, proposals в save_chat_blueprint/revise_chat_blueprint должны совпадать по количеству. "
            "Если запрос звучит как подготовка ДО темы if/else, не вводи явные if/else в ранних bridge-задачах, пока пользователь не попросил обратное. "
            "prepare_bridge_plan нельзя вызывать без свежего analyze_course_progression для того же courseId и focus: сначала audit, потом plan. "
            "Если пользователь пишет 'не продолжай старый план', 'повтори заново' или 'все предыдущие черновики недействительны', старый bridge-plan и старый blueprint нужно считать устаревшими, а не продолжать их по инерции. "
            "Никогда не дублируй один и тот же action. Не считай задачу завершённой, если после inspection всё ещё не открыты нужные соседи, не подтверждён эталон или blueprint не прошёл самопроверку. "
        )
        if _prefer_autonomy:
            _action_mode_instruction += (
                "Поскольку пользователь специально просит самостоятельности, не зависай на UX-этапе 'жду одобрения'. "
                "Если запрос не требует отдельного согласования, сам дойди до финального содержательного ответа в этом же автономном проходе. "
            )

    return (
        "Ты — TaskForge AI chat orchestrator. Верни только один валидный JSON-объект без markdown и без пояснений вокруг JSON.\\n\\n"
        "Твоя задача: вести ЖИВОЙ диалог с пользователем по-русски и использовать действия TaskForge только там, где они действительно помогают ответить на текущий запрос. "
        "Сначала пойми интент текущего сообщения: это может быть обычный разговор, просьба показать существующие задания, просьба найти пробелы, просьба собрать план или просьба сгенерировать новое. Не превращай каждый запрос про курс в bridge-plan workflow. "
        "Если пользователь просит показать, перечислить, вывести или изучить уже существующие задания курса — приоритет у inspect_course_assignments, а не у prepare_bridge_plan/show_bridge_plan. После такого запроса не перескакивай к мостикам без новой явной просьбы пользователя. Если inspect_course_assignments уже вернул AiOverview или в payload есть landmarkAssignments, используй эти поля в reasoning и в итоговом ответе: называй роль задания (guided-intro/bridge/milestone), важность и why-it-matters вместо голых догадок по title. "
        "Если пользователь просто комментирует, сомневается, ругается или формулирует мысль вслух — нормально ответить по-человечески с actions=[] и задать один точный вопрос. "
        "НОВЫЙ ПРИНЦИП ДЛЯ GENERATION: по умолчанию не запускай полноценную генерацию и не делай batch сразу. Сначала предложи 1-3 примерных условия/наброска прямо в чате, сохрани их через save_chat_blueprint и дождись правок или явного одобрения пользователя. Если пользователь присылает правки к уже сохранённым вариантам, обновляй их через revise_chat_blueprint новой revision, а не начинай workflow заново. Только после явной фразы вроде 'одобряю', 'закидывай в черновик', 'делай черновик' используй finalize_chat_blueprint. Если пользователь уже явно просит сразу запускать создание задачи ('всё генерируй', 'не черновик', 'запускай создание задачи') и в памяти есть согласованный blueprint, можно идти в queue_generate_from_text по этому blueprint. Но если пользователь специально требует автономности, не проси декоративного одобрения ради самого одобрения: используй blueprint как внутренний черновик и продолжай сам, пока не соберёшь полноценный ответ. Если currentDraftBlueprint конфликтует с новой жёсткой инструкцией пользователя, сначала исправь или пересобери blueprint, а не защищай старую revision. "
        "Когда сохраняешь blueprint, не ограничивайся абстрактным summary. Внутри blueprint proposals дай читаемый черновик условия: title, conditionPreview и по возможности fullCondition с реальным текстом будущего задания, чтобы пользователь мог править именно условие, а не только идею. "
        "Если пользователь просит несколько задач и при этом явно требует сразу результат без пауз, не уводи запрос в batch. Используй queue_generate_from_text и count, а backend сам создаст несколько отдельных generation job без batch. "
        "Если в memory уже есть currentDraftBlueprint, не придумывай новый workflow с нуля: либо покажи текущие варианты, либо обнови их через revise_chat_blueprint новой revision, либо финализируй их после явного одобрения. При правке по возможности сохраняй id вариантов и меняй только то, о чём попросил пользователь. "
        "assistantMessage — это видимый пользователю финальный ответ за ход. Он должен быть коротким, спокойным и без технической кухни: не перечисляй внутренние шаги, tool names, agent loop, analyze_course_progression, inspect_course_assignments, prepare_bridge_plan, show_bridge_plan, batchId, afterAssignmentId или anchor, если пользователь не просил именно эти детали. Если backend сам продолжит внутренние шаги, не описывай их в assistantMessage. Обычно достаточно 1-4 коротких предложений. "
        "Если данных не хватает — actions должен быть пустым массивом, а assistantMessage должен кратко запросить недостающие параметры.\\n\\n"
        "==== ПРИНЦИП: НИКОГДА НЕ ИМИТИРУЙ РАБОТУ ====\\n"
        "- Если tool вернул ошибку (status='error') — ОСТАНОВИСЬ. Объясни пользователю что пошло не так ЧЕЛОВЕЧЕСКИМ ЯЗЫКОМ (не технические детали, а суть). Спроси что делать дальше.\\n"
        "- НЕ вызывай тот же tool повторно с другими параметрами надеясь что прокатит.\\n"
        "- НЕ переключайся на другой tool чтобы обойти ошибку.\\n"
        "- Если тебе не хватает информации (нет фокуса, нет курса, непонятен запрос) — СПРОСИ ПРЯМО. Не угадывай и не подставляй значения сам.\\n"
        "- Если результат получился плохим (пустой план, 0 findings) — скажи это пользователю честно и спроси уточнение.\\n"
        "- Лучше задать один точный вопрос, чем молча выдать мусор.\\n\\n"
        "==== BATCH-CLARIFICATION ====\\n"
        "Если в recentBatches есть batch со status='needs-clarification' — это означает, что планировщик не смог составить план и остановил работу. "
        "В чате уже есть системное сообщение с описанием проблемы. Когда пользователь отвечает на это сообщение (уточняет тему, фокус, количество и т.д.), "
        "сделай prepare_bridge_plan или queue_generate_from_text заново с учётом нового уточнения. Не пытайся возвращаться к старому batch-пайплайну.\\n\\n"
        "memory — это долговременная память всей сессии: прошлые цели пользователя, вложения, уже выполненные действия и найденные сущности. Используй memory как контекст, но не позволяй старому workflow перетягивать разговор на себя. Новый явный запрос пользователя всегда важнее старого плана. По умолчанию выбирай один самый уместный следующий шаг, а не целую скрытую цепочку. "
        "Если latestExplicitInstruction звучит как reset/rework ('с нуля', 'заново', 'не продолжай старый план', 'не сохраняй промежуточный мусор'), не опирайся на stale nextSuggestedAction и не делай вид, будто старый blueprint всё ещё главный. "
        "Если пользователь пишет 'продолжай', 'сделай ещё', 'начинай' или подобный короткий follow-up, сперва опирайся на memory и последние toolResults, а не проси заново весь контекст.\\n\\n"
        "agentState — это каноническое состояние агента между чатом и pipeline: userIntentSummary, objectiveKind, objectiveSummary, currentStage, stageSummary, subtasks, planSteps, pedagogyMode, nextSuggestedAction, confidencePercent, confidenceReason, selfCritique, blockerSummary, needsClarification, autonomyMode, evidenceLedger, openQuestions, riskFlags, decisionCandidates и placementCandidates. "
        "Если agentState заполнен, используй его как важный source of truth, но не позволяй устаревшему nextSuggestedAction перебивать самый свежий user-message. Новый явный запрос пользователя важнее старой подсказки из памяти. "
        "Смотри не только на currentStage, но и на confidence/openQuestions/riskFlags: если уверенность низкая или есть blockerSummary, сначала закрой blocker или задай один точный вопрос. planSteps — это рабочий мини-план агента на несколько ходов: ориентируйся на current/pending шаги и их recommendedAction, но не выполняй действие слепо, если пользователь уже сменил цель. decisionCandidates — это shortlist правдоподобных следующих действий, а не обязательный приказ. autonomyMode=ask-first означает, что лучше уточнить, чем спешить.\n\n"
        "Если пользователь меняет задачу: сообщает про косяк в уже созданных или опубликованных задачах, просит найти ещё пробелы, скрытые prerequisite-ошибки, слишком раннее использование переменных, cin/cout, #include, using namespace std или main — это новый diagnostic-audit запрос. В таком случае не возвращайся автоматически к старому bridge-plan и не делай вид, что пользователь всё ещё просит показать прежний план.\n\n"
        "Если пользователь просит не просто аудит, а ПОЛНЫЙ проход по курсу (например: 'найди все пробелы', 'сгенерируй задачи на все пробелы', 'предложи решения по всему курсу'), действуй как remediation-agent: сначала discovery через analyze_course_progression, затем verify через inspect_course_assignments, затем propose через prepare_bridge_plan и только потом обсуждай generation. Не перепрыгивай сразу к save_chat_blueprint или queue_generate_from_text, пока не собран опорный remediation-контекст.\n\n"
        "Если пользователь уже сам написал почти готовую обучалку, эталонный код или teaching-script, это сильнее старого плана. Такой текст нельзя растворять в абстрактном microGoal: сохраняй порядок шагов, конкретные строки кода и смысл объяснений. Если после этого пользователь говорит 'всё, делай' или 'сделай саму задачу', приоритет — generation, а не очередной show/revise plan.\n\n"
        "Думай не как router по ключевым словам, а как аккуратный агент: перед действием быстро оцени цель пользователя, уже собранные доказательства, незакрытые вопросы и риск слишком раннего tool call. Если доказательств мало, сужай шаг и не притворяйся, будто всё уже ясно.\n\n"
        "show_bridge_plan и revise_bridge_plan подходят только когда пользователь прямо просит показать, уточнить или поправить план. Не вызывай show_bridge_plan просто потому, что в memory остался старый план мостиков.\n\n"
        "Если пользователь даёт feedback на уже созданные или опубликованные задачи и просит найти похожие педагогические косяки, сначала используй analyze_course_progression; при необходимости затем inspect_course_assignments. Только после нового аудита можно предлагать corrective bridge plan или новую генерацию.\n\nЕсли в памяти уже есть inspection, evidenceLedger или другие подтверждённые наблюдения по реальным заданиям, они важнее старого эвристического аудита: inspection > audit. Если в payload есть landmarkAssignments и courseOverviewCoverage, используй их как высокосигнальный слой контекста: landmarkAssignments показывают опорные задания курса по persisted AI overview, а coverage помогает понять, насколько широко этот слой уже заполнен. При анализе курса сначала смотри на landmarkAssignments, guided-intro/milestone/bridge роли и reasons importance, а уже потом на сырые названия. Не повторяй старый вывод, если inspection уже показал обратное. Когда пользователь пишет 'точно ли', 'посмотри точнее', 'где именно', 'по итогу где' или жалуется, что AI врёт, assistantMessage должен опираться на конкретные просмотренные задания и observations по реальным условиям. Не перечисляй неподтверждённые темы вроде getline/for/if, если их не открывали в inspection. Для итогового diagnostic-ответа можно дать 3-8 коротких строк в формате: что подтвердилось / что не подтвердилось / что осталось проверить.\n\n"
        "Если пользователь явно просит короткий ответ, только итог, без внутренних шагов, без старого плана или без технических деталей — это приоритетное UX-ограничение. В таком случае assistantMessage должен содержать только итог или следующий короткий вопрос, без пересказа процесса.\n\n"
        + _blueprint_generation_guidance
        + "Если пользователь просит сначала изучить курс и перечислить существующие задания — используй inspect_course_assignments и остановись на этом. "
        "Если пользователь просит найти пробелы, слишком резкие вводы новых функций или скрытые prerequisite-ошибки — используй analyze_course_progression. "
        "Если пользователь после аудита хочет посмотреть конкретные существующие задания, названия, соседние элементы курса или место вставки вокруг anchor — используй inspect_course_assignments. "
        "Если пользователь прямо просит собрать план мостиков, список вставок или подводящие задания — используй prepare_bridge_plan. "
        "Если у тебя уже есть готовый план мостиков в memory и пользователь прямо просит сгенерировать мостики по нему — подходит queue_generate_from_text. "
        "advance_agent_stage используй только когда пользователь явно просит продолжить уже начатый pipeline и из memory действительно ясно, какой шаг следующий. "
        "Если пользователь хочет несколько заданий, но не указал количество явно, не подставляй count молча из defaults: сначала задай короткий уточняющий вопрос про количество и не запускай action. "
        "save_chat_blueprint — сохранить 1 или несколько примерных условий из чата для дальнейшего обсуждения. Это default action для generation workflow. revise_chat_blueprint — обновить уже сохранённые условия по новым правкам пользователя без потери текущего workflow. В arguments.proposals передавай максимально конкретный preview: условие, обязательные фрагменты, placement, публичные и скрытые тесты. Количество тестов выбирай по задаче, а не по шаблону. "
        "show_chat_blueprint — показать уже сохранённые примерные условия. "
        "drop_chat_blueprint — сбросить старые варианты, если пользователь просит начать заново. "
        "finalize_chat_blueprint — только после явного одобрения пользователя превратить согласованные условия в полноценные draft-черновики. "
        "Когда пользователь сознательно просит пропустить этап обсуждения и сразу финализировать задачу из текста — queue_generate_from_text. Если пользователь просит поправить уже созданный AI-черновик по новому сообщению — revise_draft_from_chat. Если в payload есть sessionRecentDrafts и пользователь говорит про 'первую/вторую/третью задачу', считай именно эти sessionRecentDrafts главным набором кандидатов для revise_draft_from_chat. "
        "Когда пользователь явно просит использовать прикреплённый файл — queue_generate_from_file. "
        "Когда пользователь просит проверить/провалидировать draft — queue_validate_draft. "
        "Когда пользователь просит анализ уже существующего задания — queue_analyze_assignment. "
        "Когда пользователь просит review попытки — queue_review_submission. "
        "Когда пользователь просит review пользователя — queue_review_user.\\n\\n"
        "==== ПРОСМОТР И УПРАВЛЕНИЕ ПРЯМО В ЧАТЕ ====\\n"
        "show_draft — показать содержимое черновика (условие, решение, тесты) прямо в чате. Используй после генерации или когда пользователь просит 'покажи что получилось', 'покажи задание', 'что сгенерировалось'. monitor_generation_jobs — проверить именно generation/revise jobs этой чат-сессии и подтянуть появившиеся draft-черновики прямо в чат. Используй, когда пользователь ждёт результат генерации или спрашивает, что уже готово. "
        "show_draft_reviews — показать результаты self-check и quality scorecard черновика, если пользователь отдельно просит детали проверки. "
        ""
        ""
        "ВАЖНО: после завершения генерации batch автоматически покажи содержимое первого черновика через show_draft, чтобы пользователю не приходилось просить об этом.\\n\\n"
        "Опасные действия approve_draft, reject_draft, publish_draft, publish_batch, cancel_batch разрешены только если пользователь явно и недвусмысленно попросил это сделать. "
        "Для них обязательно передавай confirmed=true. Если явного подтверждения нет — не выполняй действие.\\n\\n"
        "Не выдумывай id. Используй только те courseId, draftId, assignmentId, batchId, userId и sourceAttemptId, которые уже есть в payload. "
        "Если курс не ясен — не угадывай, а попроси пользователя выбрать. "
        f"{_action_mode_instruction}"
        f"{_instruction_fidelity_appendix(compact_payload)}"
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
        "      \\\"name\\\": \\\"analyze_course_progression|inspect_course_assignments|prepare_bridge_plan|show_bridge_plan|revise_bridge_plan|advance_agent_stage|queue_generate_from_text|queue_generate_from_file|queue_validate_draft|approve_draft|reject_draft|publish_draft|show_draft|show_draft_reviews|cancel_batch|publish_batch|queue_analyze_assignment|queue_review_submission|queue_review_user\\\",\\n"
        "      \\\"reason\\\": \\\"...\\\",\\n"
        "      \\\"arguments\\\": { ... }\\n"
        "    }\\n"
        "  ]\\n"
        "}\\n\\n"
        "Для совместимости можно дополнительно вернуть action как первый элемент actions, но основной формат — именно actions.\\n\\n"
        f"{_dynamic_section}"
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


def _repair_route_directive(payload: Dict[str, Any]) -> str:
    repair_plan = payload.get("repairPlan") if isinstance(payload.get("repairPlan"), dict) else {}
    primary_route = normalize_text(repair_plan.get("primaryRoute") or "general").lower() or "general"
    routes = [normalize_text(x).lower() for x in (repair_plan.get("routes") if isinstance(repair_plan.get("routes"), list) else []) if normalize_text(x)]
    findings = payload.get("reviewResults") if isinstance(payload.get("reviewResults"), list) else []
    hints: List[str] = []
    for review in findings[:4]:
        result = review.get("result") if isinstance(review, dict) else {}
        finding_list = result.get("findings") if isinstance(result, dict) and isinstance(result.get("findings"), list) else []
        for item in finding_list[:2]:
            if isinstance(item, dict):
                msg = normalize_text(item.get("message"))
                if msg:
                    hints.append(msg)
    route_map = {
        "tests": "Сохрани тему, title и основную формулировку. Меняй прежде всего publicTests, hiddenTests и referenceSolutionPython. Не перепридумывай задачу целиком.",
        "solution": "Сохрани title, description и тесты максимально стабильными. Чини главным образом referenceSolutionPython и связанные code-policy поля.",
        "description": "Сохрани учебную цель и shape тестов. Перепиши в первую очередь description/title/summary, устрани неоднозначность и педагогические провалы.",
        "brief": "Сохрани ядро draft, но усили generation intent: title, framing, targetSkill, microGoal и обоснование placement.",
        "policy": "Не меняй тему и общую структуру задачи. Исправь requiredCalls/forbiddenCalls, allowedLanguages и конфликтующие policy constraints.",
        "general": "Исправь проблемы минимально инвазивно: сохрани ядро задания и меняй только действительно проблемные поля.",
    }
    directive = route_map.get(primary_route, route_map["general"])
    extra = []
    if routes:
        extra.append(f"Активные repair routes: {', '.join(routes)}.")
    if hints:
        extra.append("Ключевые review hints: " + "; ".join(hints[:4]))
    extra.append("Стабильные части draft по возможности не трогай без явной необходимости.")
    return directive + "\n" + "\n".join(extra)


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
    route_directive = _repair_route_directive(payload)
    return (
        "Ты — TaskForge AI. Сейчас нужно исправить неудачный draft. "
        "Верни только валидный JSON вида {\"draft\": {...}} без markdown.\n\n"
        f"Тип job: {job.get('type')}\n"
        f"Попытка исправления: {attempt_no}\n\n"
        f"Требования:\n{build_job_specific_instructions(job.get('type') or '', payload)}\n\n"
        f"Route-aware repair directive:\n{route_directive}\n\n"
        f"Instruction fidelity contract:\n{_instruction_fidelity_appendix(compact_payload)}\n"
        f"Изначальный payload:\n{_prompt_json(compact_payload)}\n\n"
        f"Плохой draft, который нужно переписать:\n{json.dumps(draft, ensure_ascii=False, indent=2)}\n\n"
        f"Ошибки self-check:\n{json.dumps(validation, ensure_ascii=False, indent=2)}\n\n"
        "Исправь все замечания, сохрани стабильные поля по максимуму и верни полностью готовый draft."
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
    batch_memory = compact_payload.get("batchMemory") if isinstance(compact_payload.get("batchMemory"), dict) else {}
    agent_state = batch_memory.get("agentState") if isinstance(batch_memory.get("agentState"), dict) else {}
    learner = batch_memory.get("learnerProfile") if isinstance(batch_memory.get("learnerProfile"), dict) else {}
    placement_plan = batch_memory.get("placementPlan") if isinstance(batch_memory.get("placementPlan"), list) else []
    easy_note = "Для этого запроса нужны очень простые базовые задания для новичков: без скачка в advanced topics и без потери педагогического замысла." if any(token in prompt_low for token in ["прост", "easy", "beginner", "базов", "вводн", "нович"]) else ""
    domain_lock_note = ""
    active_constraints = agent_state.get("activeConstraints") if isinstance(agent_state.get("activeConstraints"), list) else []
    if any("цикл" in normalize_text(item).lower() for item in active_constraints):
        domain_lock_note = "Ограничение state: новые slot-ы должны оставаться до темы циклов и не перепрыгивать в продвинутые конструкции."
    elif any("массив" in normalize_text(item).lower() for item in active_constraints):
        domain_lock_note = "Ограничение state: новые slot-ы не должны раньше времени уходить в массивы или другую следующую тему курса."
    placement_note = ""
    if placement_plan:
        placement_note = "Используй placementPlan как главный контур плана: сначала раскрой уже найденные точки вставки, а не придумывай новые абстрактные темы."
    elif agent_state.get("placementCandidates"):
        placement_note = "В agentState уже есть placementCandidates: планируй slot-ы вокруг них и выбирай anchor из этого списка в первую очередь."
    walkthrough_note = ""
    if bool(learner.get("preferGuidedWalkthroughs")) or bool(learner.get("explainLikeChild")):
        walkthrough_note = "Для первых slot-ов предпочитай guided-walkthrough: это должны быть маленькие ступеньки, которые объясняют ровно одно действие за раз."
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
        "- Если в batchMemory.agentState уже есть userIntentSummary/currentStage/placementCandidates, используй это как канонический state агента и не теряй исходный педагогический замысел пользователя.\n"
        "- canonicalRequest.domain и каждый task.targetSkill должны быть согласованы между собой: не дрейфуй в другой домен ради красивой формулировки.\n"
        "- Если пользователь просит мостики/обучалки/подводящие шаги, не превращай slot в обычную олимпиадную задачу.\n"
        "- Для первых slot-ов можно делать guided-walkthrough, но это всё ещё задача с чёткой учебной целью, а не лекция и не конспект.\n"
        "- Если learnerProfile/pedagogy указывает на guided walkthrough или very simple audience, часть slot-ов в начале новой темы делай taskFormat=guided-walkthrough.\n"
        "- guided-walkthrough — это не конспект и не лекция, а очень простая пошаговая учебная задача перед обычными упражнениями.\n"
        "- Не пиши длинные описания.\n"
        "- СТОП если не можешь: если тебе не хватает данных для качественного плана (нет фокуса, неясна тема, "
        "referenceAssignments пустые и нет контекста курса), вместо 'plan.tasks' верни "
        "'\"plan\": {\"error\": \"Причина почему не могу спланировать. Чего не хватает.\", \"tasks\": []}'. "
        "Лучше вернуть ошибку, чем мусорный план.\n"
        + (easy_note + "\n" if easy_note else "")
        + (domain_lock_note + "\n" if domain_lock_note else "")
        + (placement_note + "\n" if placement_note else "")
        + (walkthrough_note + "\n" if walkthrough_note else "")
        + (_pre_if_scaffolding_appendix(compact_payload) if _is_pre_if_scaffolding_request(compact_payload) else "")
        + (_if_onboarding_appendix(compact_payload) if _is_if_onboarding_request(compact_payload) else "")
        + ("Для такого запроса tasks должны образовывать подготовительную лесенку ДО первого if: через сравнения, понятные выборы и видимый результат, но без явного if/else в самих условиях. Не перепрыгивай сразу к ветвлению.\n" if _is_pre_if_scaffolding_request(compact_payload) else "")
        + ("Для такого запроса tasks должны образовывать именно обучающую лесенку по if: первый if, затем if/else, затем ещё 1-2 маленьких шага усложнения. Не планируй абстрактные мостики вроде остатка от деления без if.\n" if _is_if_onboarding_request(compact_payload) else "")
        + (retry_note + "\n" if retry_note else "")
        + "\n"
        + f"Batch payload:\n{_prompt_json(compact_payload)}"
    )


def build_stage_schema_repair_prompt(stage: str, payload: Dict[str, Any], bad_result: Dict[str, Any], schema_errors: List[str] | None = None) -> str:
    if stage in {"draft_generate", "assignment_repair", "repair"} or str(stage or "").startswith("repair_"):
        # Radically compress payload for draft/repair — LLM needs its token budget for the full draft
        _brief = payload.get("brief") if isinstance(payload.get("brief"), dict) else {}
        _task = payload.get("task") if isinstance(payload.get("task"), dict) else {}
        compact_payload = {
            "assignmentType": payload.get("assignmentType") or "code-test",
            "qualityGates": payload.get("qualityGates") if isinstance(payload.get("qualityGates"), dict) else {},
            "brief": {k: _brief[k] for k in ("titleHint", "targetSkill", "summary", "generationPrompt") if _brief.get(k)},
            "task": {k: _task[k] for k in ("targetSkill", "microGoal", "MicroGoal") if _task.get(k)},
        }
    else:
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
    elif stage in {"draft_generate", "assignment_repair", "repair"}:
        assignment_type = normalize_text(payload.get("assignmentType") or "code-test") or "code-test"
        expected = _draft_response_format(payload, assignment_type, include_pending_title=False, min_public=MIN_PUBLIC_TESTS, min_hidden=MIN_HIDDEN_TESTS)
    elif stage == "assistant_chat_turn":
        expected = '{"assistantMessage":"...","sessionTitle":"...","actions":[{"name":"queue_generate_from_text","reason":"...","arguments":{"courseId":"..."}}]}'
    elif stage == "brief":
        expected = '{"titleHint":"...","summary":"...","generationPrompt":"...","sourceText":"...","notes":"...","difficultyTarget":2,"targetSkill":"...","decisionLog":[{"stage":"brief_generate","message":"..."}]}'
    elif stage == "reference_pack":
        expected = '{"stylePack":{"titleStyle":{"examples":["..."]}},"policyPack":{"requiredCalls":["..."]},"exemplarPack":{"titles":["..."]},"generationHints":["..."],"signals":[{"code":"...","weight":0.5}]}'
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
        + "\n".join(extra_rules) + "\n"
        + _instruction_fidelity_appendix(compact_payload) + "\n"
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
        + "\n".join(extra_rules) + "\n"
        + _instruction_fidelity_appendix(compact_payload) + "\n"
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


def _compact_approved_blueprint(payload: Dict[str, Any]) -> Dict[str, Any] | None:
    ctx = payload.get("structuredContext") if isinstance(payload.get("structuredContext"), dict) else {}
    if normalize_text(ctx.get("kind")) != "approved-chat-blueprint":
        return None
    return {
        "kind": "approved-chat-blueprint",
        "title": truncate_text(ctx.get("title"), 120),
        "goal": truncate_text(ctx.get("goal"), 180),
        "fullCondition": truncate_text(ctx.get("fullCondition"), 700),
        "conditionPreview": truncate_text(ctx.get("conditionPreview"), 260),
        "placementAfterAssignmentId": truncate_text(ctx.get("placementAfterAssignmentId") or ctx.get("afterAssignmentId"), 80),
        "placementAfterTitle": truncate_text(ctx.get("placementAfterTitle") or ctx.get("afterAssignmentTitle"), 120),
        "placementReason": truncate_text(ctx.get("placementReason"), 180),
        "mustKeep": unique_string_list(ctx.get("mustKeep"), 10),
        "avoid": unique_string_list(ctx.get("avoid"), 10),
        "publicTests": [
            {
                "input": truncate_text((x or {}).get("input"), 40),
                "expectedOutput": truncate_text((x or {}).get("expectedOutput"), 80),
            }
            for x in list(ctx.get("publicTests") or [])[:6] if isinstance(x, dict)
        ],
    }

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
        "approvedBlueprint": _compact_approved_blueprint(payload),
        "anchorContext": payload.get("anchorContext") if isinstance(payload.get("anchorContext"), dict) else None,
    }
    return (
        "Ты — TaskForge AI course style analyst. Верни только JSON без markdown.\n\n"
        "Сейчас не нужно писать задачу. Нужно проанализировать существующие задания курса и вывести style digest для следующей генерации.\n"
        "Определи: как обычно формулируется условие, какие секции обязательны, насколько подробны ограничения, как выглядят тесты и как обычно называются задания.\n"
        "Не придумывай новую задачу и не копируй готовые title/description дословно.\n"
        "Верни JSON формата: {\"courseStyle\":{...},\"titleStyle\":{...},\"antiPatterns\":[...],\"positivePatterns\":[...],\"summary\":\"...\"}.\n\n"
        + (_style_exemplar_appendix(compact_payload) if _style_exemplar_appendix(compact_payload) else "")
        + "Требования к полям:\n"
        "- courseStyle.descriptionSections: массив строк.\n"
        "- courseStyle.descriptionTone: коротко опиши стиль формулировок курса.\n"
        "- courseStyle.testStyle: коротко опиши типичный набор тестов.\n"
        "- titleStyle.pattern: коротко опиши стиль названий курса.\n"
        "- titleStyle.examples: 3-6 коротких примеров названий из курса.\n"
        "- antiPatterns: чего нельзя делать при генерации, чтобы не выбиться из курса.\n"
        "- positivePatterns: что наоборот обязательно сохранить.\n\n"
        f"Course style payload:\n{_prompt_json(compact_payload)}"
    )




def _scenario_profile(payload: Dict[str, Any]) -> Dict[str, Any]:
    return detect_scenario_profile(payload)


def _payload_request_text(payload: Dict[str, Any]) -> str:
    parts: List[str] = []
    for key in ("prompt", "sourceText"):
        value = str(payload.get(key) or "").strip()
        if value:
            parts.append(value)
    conversation = payload.get("conversation") if isinstance(payload.get("conversation"), list) else []
    for item in conversation[-6:]:
        if not isinstance(item, dict):
            continue
        if str(item.get("role") or "").strip().lower() != "user":
            continue
        value = str(item.get("content") or item.get("text") or "").strip()
        if value:
            parts.append(value)
    memory = payload.get("memory") if isinstance(payload.get("memory"), dict) else {}
    for raw in (memory.get("latestExplicitInstruction"), memory.get("latestTeachingScript")):
        value = str(raw or "").strip()
        if value:
            parts.append(value)
    for raw in (memory.get("recentGoals") if isinstance(memory.get("recentGoals"), list) else []):
        value = str(raw or "").strip()
        if value:
            parts.append(value)
    return " ".join(parts).lower()


def _requests_explicit_if_from_start(text: str) -> bool:
    low = (text or "").lower()
    if not low:
        return False
    markers = [
        "на сам if",
        "именно if",
        "уже с if",
        "сразу if",
        "первый шаг if",
        "первый шаг — if",
        "первый шаг - if",
        "первым должен быть if",
        "можно if",
        "разрешаю if",
    ]
    return any(marker in low for marker in markers)


def _is_pre_if_scaffolding_request(payload: Dict[str, Any]) -> bool:
    low = _payload_request_text(payload)
    if not low or _requests_explicit_if_from_start(low):
        return False
    mentions_if_topic = any(token in low for token in ["if", "ветвл", "условн"])
    if not mentions_if_topic:
        return False
    explicit_before_markers = [
        "перед первым if",
        "перед первым появлением if",
        "до первого if",
        "до темы if",
        "до if",
        "до ветвлен",
        "до условн",
        "прежде чем объясн",
        "прежде чем вводить if",
        "до того как вводить if",
    ]
    if any(marker in low for marker in explicit_before_markers):
        return True
    mentions_course = any(token in low for token in ["курс", "задан", "assignment"])
    abrupt_markers = ["без введен", "без обучал", "без объяснен", "резко", "слишком рано", "появля"]
    prep_markers = ["подводящ", "подготов", "обучал", "лесенк", "пошаг", "перед темой"]
    return mentions_course and any(marker in low for marker in abrupt_markers) and any(marker in low for marker in prep_markers)


def _is_if_onboarding_request(payload: Dict[str, Any]) -> bool:
    low = _payload_request_text(payload)
    if not low or _is_pre_if_scaffolding_request(payload):
        return False
    mentions_if_topic = any(token in low for token in ["if", "ветвл", "условн"])
    asks_for_ladder = any(token in low for token in ["лесенк", "пошаг", "маленьких программ", "серия", "шаг за шаг", "с нуля"])
    return mentions_if_topic and asks_for_ladder


def _pre_if_scaffolding_appendix(payload: Dict[str, Any]) -> str:
    profile = _scenario_profile(payload)
    return scenario_prompt_appendix(profile) + ladder_style_appendix(profile, payload)


def _if_onboarding_appendix(payload: Dict[str, Any]) -> str:
    profile = _scenario_profile(payload)
    return scenario_prompt_appendix(profile) + ladder_style_appendix(profile, payload)

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
        "approvedBlueprint": _compact_approved_blueprint(payload),
        "anchorContext": payload.get("anchorContext") if isinstance(payload.get("anchorContext"), dict) else None,
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
        + (_style_exemplar_appendix(compact_payload) if _style_exemplar_appendix(compact_payload) else "")
        + _if_onboarding_appendix(compact_payload)
        + f"Generation spec payload:\n{_prompt_json(compact_payload)}"
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
        "approvedBlueprint": _compact_approved_blueprint(payload),
        "anchorContext": payload.get("anchorContext") if isinstance(payload.get("anchorContext"), dict) else None,
    }
    return (
        "Ты — TaskForge AI draft content planner. Верни только JSON без markdown.\n\n"
        "Не пиши ещё полное условие. Сначала составь content plan для будущего draft.\n"
        "План должен удерживать стиль курса, уникальность относительно соседних items и конкретную учебную цель.\n"
        "Верни JSON: {\"contentPlan\":{...},\"summary\":\"...\"}.\n"
        "В contentPlan должны быть поля: pedagogicalGoal, noveltyHook, inputModel, outputModel, constraintsPlan, sectionPlan, publicTestPlan, hiddenTestPlan, titleShape, bannedOverlaps, coursePhrasingRules.\n\n"
        + (_style_exemplar_appendix(compact_payload) if _style_exemplar_appendix(compact_payload) else "")
        + _if_onboarding_appendix(compact_payload)
        + f"Draft content plan payload:\n{_prompt_json(compact_payload)}"
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
        "publicTests": [{"input": "...", "expectedOutput": "..."}],
        "hiddenTests": [{"input": "...", "expectedOutput": "..."}],
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
        "- Для code-test обязательны: title, description, publicTests, referenceSolutionPython. hiddenTests можно оставить пустым списком.\n"
        "- allowedLanguages обязателен, если курс/контекст уже ограничивает языки.\n"
        f"- Разрешённые языки для этой генерации: {', '.join(_supported_code_languages(payload))}. Если контекст курса сужает список, не добавляй другие языки.\n"
        + (f"- Соблюдай нижнюю границу publicTests из qualityGates: {quality_gates.get('minPublicTests')}.\n" if 'minPublicTests' in quality_gates else "")
        + (f"- Соблюдай нижнюю границу hiddenTests из qualityGates: {quality_gates.get('minHiddenTests')}.\n" if 'minHiddenTests' in quality_gates else "")
        + (f"- Суммарное количество тестов не должно быть меньше {quality_gates.get('minTotalTests')}.\n" if 'minTotalTests' in quality_gates else "")
        + "- Количество publicTests и hiddenTests выбирай по задаче, не выравнивай их искусственно под один шаблон.\n"
        + "- Предпочтительно делать publicTests больше, чем hiddenTests, если это не вредит качеству покрытия.\n"
        + "- Используй только root-level requiredCalls и forbiddenCalls. Не вкладывай их в codePolicy.\n"
        + (_if_onboarding_appendix(payload) if _is_if_onboarding_request(payload) else "")
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
- Если в payload есть approvedBlueprint, он важнее noveltyHook, anti-duplicate и style-экспериментов: approvedBlueprint — это канон, а не вдохновение.
- При approvedBlueprint нельзя подменять cout на scanf/printf, добавлять ввод без явного запроса или менять точный вывод/каркас программы.
- Если approvedBlueprint.fullCondition и style exemplar указывают на дружелюбное вступление и пошаговый scaffold, description обязан повторить именно такой каркас, а не уходить в сухую олимпиадную формулировку.
- Если approvedBlueprint.mustKeep содержит указания про стиль/тон/порядок шагов, они обязательны и имеют приоритет над общими style digest правилами.
{rules}{_pedagogy_appendix(compact_payload)}{_instruction_fidelity_appendix(compact_payload)}- Не уходи в другую микроцель: строго соблюдай targetSkill, microGoal и contentPlan.pedagogicalGoal.
- Соблюдай contentPlan.sectionPlan и coursePhraseBank, но не копируй фразы дословно.
- Не используй чужие title из referenceAssignments.
- referenceSolutionPython обязан проходить все publicTests и hiddenTests без подгонки expectedOutput.
- Не создавай тесты, где input состоит только из пробелов или пустых строк с пробелами: такие кейсы несовместимы с сайтом.
- Если задача логически не требует ввода, оставляй input пустой строкой. Это нормальный пустой stdin.
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
{rules}{_instruction_fidelity_appendix(compact_payload)}{_style_exemplar_appendix(compact_payload)}- Сохраняй course-native терминологию и уровень сложности.
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
{rules}{_instruction_fidelity_appendix(compact_payload)}- Настройки settings и блоки должны быть полными и каноническими.

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
{rules}{_pedagogy_appendix(compact_payload)}{_instruction_fidelity_appendix(compact_payload)}{_style_exemplar_appendix(compact_payload)}- Задача должна соответствовать titleHint, targetSkill и microGoal, а не уходить в другой домен.
- Если есть approvedBlueprint, сначала подчинись ему, а уже потом style digest курса. approvedBlueprint — главный источник истинного pedagogical замысла.
- Не копируй referenceAssignments дословно и не пересобирай уже существующее задание с косметическими изменениями числа/формата.
- Если рядом с anchor уже есть очень похожая задача, смести учебную цель: измени действие, формат вывода, тип входа или ожидаемый результат.
- Особенно внимательно изучи anchorContext.nearbyAssignments и anchorContext.possibleDuplicates перед генерацией.
- Если в payload есть selectionTelemetry, используй topCandidates и selectedTitles как сигнал, какие referenceAssignments считались самыми близкими и почему.
- referenceSolutionPython должен быть детерминированным и совместимым со всеми test cases без подгонки expectedOutput.
- Если задача логически без ввода, используй пустой input в тестах. Это нормальный пустой stdin.
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
    if not isinstance(compact_payload.get("anchorContext"), dict) and isinstance(payload.get("anchorContext"), dict):
        compact_payload["anchorContext"] = payload.get("anchorContext")
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
        f"Style examples: {examples_block}\n"
        + (_style_exemplar_appendix(compact_payload) if _style_exemplar_appendix(compact_payload) else "")
        + "\n"
        + f"Payload:\n{_prompt_json(compact_payload)}\n\n"
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
        f"Style examples: {examples_block}\n"
        + (_style_exemplar_appendix(compact_payload) if _style_exemplar_appendix(compact_payload) else "")
        + "\n"
        + f"Payload:\n{_prompt_json(compact_payload)}\n\n"
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
