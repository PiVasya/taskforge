from __future__ import annotations

import re
from typing import Any, Dict, List

from text_utils import normalize_text

_CONCEPT_PATTERNS = [
    re.compile(r"(?:по|на)\s+теме\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"тему\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"освоени[ея]\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"использовать\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"пользоваться\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"работ[аы]\s+с\s+([^\n\r\.,;:!?]{2,80})", re.I),
    re.compile(r"уч[иа]т[ья]?\s+([^\n\r\.,;:!?]{2,80})", re.I),
]
_NOISE = {
    "с нуля", "пошагово", "по шагам", "для курса", "маленькими шагами", "несколько задач", "несколько программ",
    "серия задач", "серия программ", "лесенка", "задач", "программ", "программа", "задачи", "тема", "темы",
}
_SLOT_DESCRIPTORS = [
    "самый первый микрошаг: один мгновенно видимый результат после запуска и минимум новых сущностей",
    "тот же навык, но уже с ещё одним маленьким действием или чуть более живым вариантом использования",
    "первое аккуратное усложнение: добавляется ровно одна новая маленькая идея поверх базы",
    "спокойная комбинированная практика: два связанных шага, но без резкого скачка сложности",
    "чуть более взрослая задача на тот же навык в реалистичной формулировке, но всё ещё без перегруза",
    "итоговое закрепление всей лесенки без смешивания слишком многих новых идей",
]


def extract_learning_concept(payload: Dict[str, Any]) -> str:
    parts: List[str] = []
    for key in ("prompt", "sourceText", "notes", "titleHint", "teachingScript", "userInstructionSnapshot"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            parts.append(value.strip())
    batch_memory = payload.get("batchMemory") if isinstance(payload.get("batchMemory"), dict) else {}
    agent_state = batch_memory.get("agentState") if isinstance(batch_memory.get("agentState"), dict) else {}
    for holder in (payload.get("brief"), payload.get("task"), batch_memory, agent_state):
        if isinstance(holder, dict):
            for key in (
                "summary", "generationPrompt", "targetSkill", "microGoal", "MicroGoal", "userIntentSummary",
                "latestExplicitInstruction", "latestTeachingScript", "objectiveSummary"
            ):
                value = holder.get(key)
                if isinstance(value, str) and value.strip():
                    parts.append(value.strip())
    hay = " ".join(parts)
    if not hay:
        return ""
    for pattern in _CONCEPT_PATTERNS:
        m = pattern.search(hay)
        if not m:
            continue
        candidate = _normalize_concept(m.group(1))
        if candidate:
            return candidate
    return ""


def ladder_style_appendix(profile: Dict[str, Any], payload: Dict[str, Any]) -> str:
    sid = normalize_text(profile.get("id"))
    if sid not in {"guided-onboarding-ladder", "step-by-step-ladder", "micro-program-series"}:
        return ""
    concept = extract_learning_concept(payload)
    concept_line = (
        f"- Точная учебная цель серии: «{concept}». Не подменяй её соседней темой и не уезжай в другую конструкцию.\n"
        if concept else
        "- Сохраняй именно ту тему, которую описал пользователь, и не подменяй её другой идеей.\n"
    )
    return (
        "\n- Сценарий: лесенка. Здесь захардкожен только стиль very-friendly guided walkthrough, а не конкретная тема.\n"
        "- Каждая задача должна ощущаться как маленькое обучение, а не как сухая проверка.\n"
        "- Заголовок и первая фраза должны быть дружелюбными и человеческими, а не абстрактно-олимпиадными.\n"
        "- Начинай с короткого дружелюбного вступления в духе «Давай...» или «Сейчас...».\n"
        "- Дальше веди ученика через отдельный блок «Следуй шагам:».\n"
        "- Обычно держи 3-6 нумерованных шагов: каждый шаг — одно маленькое действие без перегруза.\n"
        "- После важных шагов давай маленькие пояснения в скобках простым языком и по возможности на отдельной строке.\n"
        "- Для ранних шагов лесенки предпочитай мгновенно видимый эффект после запуска, а не сухую формальную историю.\n"
        "- В финале нужна живая фраза о запуске и том, что ученик увидит на экране.\n"
        "- Если ты формируешь proposals для save_chat_blueprint, в conditionPreview/fullCondition уже должен быть готовый дружелюбный текст задания, а не сухая заметка для методиста.\n"
        "- Не начинай описание с сухого шаблона «Напиши программу...», «Вам нужно...» или «Даны...».\n"
        "- Не раскладывай такие задания на безличные секции «Вход / Выход / Ограничения», если пользователь просит стиль первого дружелюбного задания.\n"
        f"{concept_line}"
    )


def looks_like_ladder_style(draft: Dict[str, Any]) -> bool:
    text = "\n".join([
        normalize_text(draft.get("title")),
        normalize_text(draft.get("description")),
        normalize_text(draft.get("explanation")),
    ])
    low = text.lower()
    has_intro = any(token in low for token in ["давай", "сейчас", "это задание", "эта программа", "она будет", "он будет", "начн", "попробуем"])
    has_steps_header = "следуй шагам" in low
    step_count = len(re.findall(r"(?:^|\n)\s*\d+\.", text))
    has_step_list = step_count >= 3
    has_explanations = "(" in text and ")" in text
    has_finish = any(token in low for token in ["запусти", "посмотри", "увид", "появ"])
    return has_intro and (has_steps_header or has_step_list) and has_step_list and has_explanations and has_finish


def looks_too_dry_for_ladder(draft: Dict[str, Any]) -> bool:
    desc = normalize_text(draft.get("description")).strip().lower()
    if not desc:
        return True
    return any(desc.startswith(prefix) for prefix in ["напиши программу", "в единственной строке", "даны", "вам нужно", "требуется", "считайте"])


def beautify_ladder_proposal(proposal: Dict[str, Any], concept: str, index: int, total_count: int) -> Dict[str, Any]:
    if not isinstance(proposal, dict):
        return proposal
    draft = {"title": proposal.get("title"), "description": proposal.get("fullCondition") or proposal.get("conditionPreview")}
    if looks_like_ladder_style(draft) and not looks_too_dry_for_ladder(draft):
        return proposal

    title = _friendly_title(str(proposal.get("title") or ""), str(proposal.get("fullCondition") or proposal.get("conditionPreview") or ""), concept, index)
    source = str(proposal.get("fullCondition") or proposal.get("conditionPreview") or "").strip()
    preview = _friendly_preview(source, concept, index)
    full_condition = _friendly_walkthrough(source, concept, index, total_count)
    aligned = dict(proposal)
    aligned["title"] = title
    aligned["conditionPreview"] = preview[:2000]
    aligned["fullCondition"] = full_condition[:8000]
    if not str(aligned.get("goal") or "").strip():
        aligned["goal"] = _goal_from_condition(source, concept, index)
    return aligned


def _friendly_title(raw_title: str, source: str, concept: str, index: int) -> str:
    text = normalize_text(raw_title or source).strip()
    text = re.sub(r"^(вариант|задание)\s*\d+[\.)\-: ]*", "", text, flags=re.I).strip()
    low = text.lower()
    body = ""
    if any(token in low for token in ["диапазон", "&&", "||", "логическ"]):
        body = "Проверяем диапазон"
    elif any(token in low for token in ["три вари", "несколько", "else if", "отрицател", "ноль"]):
        body = "Выбираем один из нескольких вариантов"
    elif any(token in low for token in ["два исход", "два пути", "иначе", "чет", "нечет", "else"]):
        body = "Сравниваем два варианта"
    elif any(token in low for token in ["перв", "прост", "проверк", "услови"]):
        body = "Делаем первую проверку"
    elif concept:
        body = f"Осваиваем {concept}"
    else:
        body = text or f"Маленький шаг {index}"
    return f"Задание {index}. {body}"


def _goal_from_condition(source: str, concept: str, index: int) -> str:
    low = normalize_text(source).lower()
    if "диапазон" in low:
        return "Аккуратно проверить число сразу по двум условиям."
    if any(token in low for token in ["else if", "несколько", "отрицател", "ноль"]):
        return "Научиться выбирать один ответ из нескольких вариантов."
    if any(token in low for token in ["иначе", "else", "чет", "нечет"]):
        return "Увидеть, как у программы появляются два понятных пути."
    if concept:
        return f"Сделать ещё один маленький шаг в теме «{concept}»."
    return f"Сделать спокойный шаг {index} без резкого скачка сложности."


def _friendly_preview(source: str, concept: str, index: int) -> str:
    target = _summarize_target(source, concept, index)
    return f"Давай сделаем маленькую программу. {target}"


def _friendly_walkthrough(source: str, concept: str, index: int, total_count: int) -> str:
    intro = _friendly_intro(source, concept, index, total_count)
    clauses = _split_instruction_clauses(source)
    steps = _build_steps_from_clauses(clauses, concept, index)
    if len(steps) < 3:
        fallback = _fallback_steps(source, concept, index)
        for item in fallback:
            if item not in steps:
                steps.append(item)
            if len(steps) >= 4:
                break
    lines: List[str] = [intro, "", "Следуй шагам:"]
    for idx, item in enumerate(steps[:5], start=1):
        lines.append(f"{idx}. {item['step']}")
        if item.get('explanation'):
            lines.append(f"({item['explanation']})")
    lines.append("")
    lines.append(_friendly_finish(source, concept, index))
    return "\n".join(line for line in lines if line is not None).strip()


def _friendly_intro(source: str, concept: str, index: int, total_count: int) -> str:
    target = _summarize_target(source, concept, index)
    if index == 1:
        return f"Давай сделаем первую маленькую программу в этой лесенке. {target}"
    if index >= total_count:
        return f"Сейчас сделаем ещё один уверенный шаг и аккуратно закрепим тему. {target}"
    return f"Давай сделаем ещё одну маленькую программу без резкого скачка сложности. {target}"


def _summarize_target(source: str, concept: str, index: int) -> str:
    low = normalize_text(source).lower()
    if "диапазон" in low:
        return "Она будет проверять, попало ли число в нужный диапазон."
    if any(token in low for token in ["чет", "неч"]):
        return "Она будет определять, какой из двух ответов нужно показать."
    if all(token in low for token in ["полож", "отриц", "ноль"]):
        return "Она будет выбирать один ответ из трёх понятных вариантов."
    if any(token in low for token in ["полож", "> 0", ">0"]):
        return "Она будет проверять число и выводить ответ только в нужном случае."
    if concept:
        return f"Она поможет тебе спокойно разобраться с темой «{concept}»."
    return f"Она поможет тебе сделать ещё один понятный шаг {index}."


def _split_instruction_clauses(source: str) -> List[str]:
    raw = normalize_text(source).replace("\r", "\n")
    raw = re.sub(r"\n+", " ", raw)
    parts = [chunk.strip(" -—") for chunk in re.split(r"(?<=[.!?])\s+|;\s+", raw) if chunk.strip(" -—")]
    cleaned: List[str] = []
    for part in parts:
        low = part.lower()
        if low in {"черновик условия", "условие", "черновик"}:
            continue
        cleaned.append(part.rstrip('.'))
    return cleaned[:6]


def _build_steps_from_clauses(clauses: List[str], concept: str, index: int) -> List[Dict[str, str]]:
    steps: List[Dict[str, str]] = []
    for clause in clauses:
        step = clause.strip()
        if not step:
            continue
        explanation = _explain_clause(step, concept, index)
        steps.append({"step": _cleanup_step_text(step), "explanation": explanation})
    return steps


def _cleanup_step_text(step: str) -> str:
    text = normalize_text(step).strip()
    text = re.sub(r"^черновик условия:?\s*", "", text, flags=re.I)
    text = re.sub(r"^используйте\s+", "Используй ", text, flags=re.I)
    text = re.sub(r"^считайте\s+", "Считай ", text, flags=re.I)
    text = re.sub(r"^выведите\s+", "Выведи ", text, flags=re.I)
    text = re.sub(r"^добавьте\s+", "Добавь ", text, flags=re.I)
    return text.rstrip('.')


def _explain_clause(clause: str, concept: str, index: int) -> str:
    low = normalize_text(clause).lower()
    if any(token in low for token in ["считай", "считайте", "введ", "клавиатур"]):
        return "Так программа получит данные, с которыми дальше будет работать."
    if "иначе" in low or "остальных" in low or "в противном" in low:
        return "Так у программы появится второй понятный путь, а результат не потеряется."
    if any(token in low for token in ["else if", "несколько", "три вари", "отрицател", "ноль"]):
        return "Так программа сможет выбрать один подходящий ответ из нескольких вариантов."
    if any(token in low for token in ["диапазон", "&&", "||"]):
        return "Так ты потренируешь более точную проверку без лишних повторов."
    if any(token in low for token in ["if", "else", "switch", "for", "while", "цикл", "логическ"]):
        return "Так ты потренируешь именно нужную конструкцию, а не обходной путь рядом с ней."
    if any(token in low for token in ["вывед", "напечат", "экран"]):
        return "После запуска ты сразу увидишь, правильно ли сработала программа."
    return "Это маленький шаг, который спокойно ведёт тебя дальше без резкого скачка сложности."


def _fallback_steps(source: str, concept: str, index: int) -> List[Dict[str, str]]:
    target = _summarize_target(source, concept, index)
    steps = [
        {"step": "Считай входные данные так, как указано в условии.", "explanation": "Так программа получит всё, что ей нужно для проверки."},
        {"step": "Сделай нужную проверку и выбери подходящее действие.", "explanation": "Это главный момент задачи: здесь программа решает, что делать дальше."},
        {"step": "Выведи только тот результат, который подходит под условие.", "explanation": "После запуска будет сразу видно, правильно ли сработала проверка."},
        {"step": f"Держи в голове цель шага: {target[0].lower() + target[1:] if target else 'сделать понятный маленький шаг.'}", "explanation": "Так задача не расползётся в лишние идеи и останется спокойной по сложности."},
    ]
    if concept:
        steps.append({"step": f"Если нужно, используй именно {concept}, а не соседнюю конструкцию.", "explanation": "Так практика останется точно в той теме, которую вы сейчас осваиваете."})
    return steps


def _friendly_finish(source: str, concept: str, index: int) -> str:
    low = normalize_text(source).lower()
    if any(token in low for token in ["вывед", "экран", "чет", "неч", "полож", "отриц", "ноль", "диапазон"]):
        return "Запусти код и посмотри, какой результат появится на экране в разных случаях."
    if concept:
        return f"Запусти код и посмотри, как тема «{concept}» начинает работать в живой программе."
    return "Запусти код и посмотри, какой результат появится на экране."


def _normalize_concept(raw: str) -> str:
    value = normalize_text(raw)
    if not value:
        return ""
    value = value.strip('"«»\'“”()[] ')
    value = re.sub(r"^(именно|только|просто)\s+", "", value, flags=re.I)
    for noise in _NOISE:
        value = re.sub(rf"\b{re.escape(noise)}\b", "", value, flags=re.I)
    value = re.sub(r"\s+", " ", value).strip(" -—:")
    if len(value) < 2 or len(value) > 80:
        return ""
    return value
