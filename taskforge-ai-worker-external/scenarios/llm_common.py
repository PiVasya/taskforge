from __future__ import annotations

from typing import Any, Dict, List

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot

BASE_SYSTEM = """
Ты AI-агент TaskForge. Отвечай только валидным JSON-объектом без markdown.
Не выдумывай, что видел курс, если в контексте нет данных. Но если контекст содержит courseCatalog/courseContexts, используй их даже если selectedCourseId=null.
Нельзя использовать шаблонные заглушки. Любой результат должен быть сгенерирован по реальному контексту и пользовательскому запросу.
Если данных недостаточно, честно укажи это в warnings и предложи, какие данные нужны.
Сохранение в курс запрещено: возвращай только blueprint/draft/report.
""".strip()


def context_user_block(context: AgentContextSnapshot, task: str, schema: Dict[str, Any], max_chars: int = 36000) -> str:
    return (
        f"Задача сценария:\n{task.strip()}\n\n"
        "Контекст TaskForge JSON:\n"
        f"{build_ai_context(context, max_chars=max_chars)}\n\n"
        "Верни JSON строго по смыслу этой схемы. Схема не является данными для копирования:\n"
        f"{schema}\n"
    )


def as_str(value: Any, default: str = "") -> str:
    text = str(value).strip() if value is not None else ""
    return text or default


def as_list(value: Any) -> List[Any]:
    return value if isinstance(value, list) else []


def as_dict(value: Any) -> Dict[str, Any]:
    return value if isinstance(value, dict) else {}
