from __future__ import annotations

from dataclasses import asdict, dataclass, field, is_dataclass
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional


def utc_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def clean_text(value: Any) -> str:
    if value is None:
        return ""
    return str(value).strip()


def ensure_list(value: Any) -> List[Any]:
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value]


def deep_to_dict(value: Any) -> Any:
    if is_dataclass(value):
        return {k: deep_to_dict(v) for k, v in asdict(value).items()}
    if isinstance(value, dict):
        return {str(k): deep_to_dict(v) for k, v in value.items()}
    if isinstance(value, list):
        return [deep_to_dict(v) for v in value]
    return value


@dataclass
class IncomingAgentMessage:
    session_id: str
    user_id: Optional[str]
    course_id: Optional[str]
    raw_text: str
    attachments: List[Dict[str, Any]] = field(default_factory=list)
    current_memory: Dict[str, Any] = field(default_factory=dict)
    selected_course: Optional[Dict[str, Any]] = None
    recent_messages: List[Dict[str, Any]] = field(default_factory=list)
    recent_jobs: List[Dict[str, Any]] = field(default_factory=list)
    recent_drafts: List[Dict[str, Any]] = field(default_factory=list)
    instruction_strictness: int = 70
    payload: Dict[str, Any] = field(default_factory=dict)

    @staticmethod
    def from_payload(payload: Dict[str, Any]) -> "IncomingAgentMessage":
        data = payload.get("payload") if isinstance(payload.get("payload"), dict) else payload
        message = data.get("message") if isinstance(data.get("message"), dict) else {}
        raw_text = (
            data.get("rawText")
            or data.get("raw_text")
            or data.get("text")
            or message.get("text")
            or message.get("content")
            or ""
        )
        selected_course = data.get("selectedCourse") or data.get("course")
        course_id = (
            data.get("courseId")
            or data.get("course_id")
            or (selected_course or {}).get("id")
            or (selected_course or {}).get("Id")
        )
        session_id = (
            data.get("sessionId")
            or data.get("conversationId")
            or data.get("chatId")
            or data.get("runId")
            or payload.get("id")
            or "local-session"
        )
        return IncomingAgentMessage(
            session_id=str(session_id),
            user_id=(str(data.get("userId") or data.get("user_id")) if data.get("userId") or data.get("user_id") else None),
            course_id=(str(course_id) if course_id else None),
            raw_text=clean_text(raw_text),
            attachments=[x for x in ensure_list(data.get("attachments")) if isinstance(x, dict)],
            current_memory=data.get("memory") if isinstance(data.get("memory"), dict) else {},
            selected_course=selected_course if isinstance(selected_course, dict) else None,
            recent_messages=[x for x in ensure_list(data.get("recentMessages") or data.get("recent_messages")) if isinstance(x, dict)],
            recent_jobs=[x for x in ensure_list(data.get("recentJobs") or data.get("recent_jobs")) if isinstance(x, dict)],
            recent_drafts=[x for x in ensure_list(data.get("recentDrafts") or data.get("recent_drafts")) if isinstance(x, dict)],
            instruction_strictness=int(data.get("instructionStrictness") or data.get("instruction_strictness") or 70),
            payload=data,
        )


@dataclass
class NormalizedMessage:
    text: str
    lowered: str
    wants_analysis: bool = False
    wants_gap_audit: bool = False
    wants_generation: bool = False
    wants_ladder: bool = False
    wants_style_match: bool = False
    wants_revision: bool = False
    wants_background: bool = False
    requested_count: Optional[int] = None
    target_concept: Optional[str] = None
    requested_style: Optional[str] = None
    direct_mode: bool = False


@dataclass
class AgentContextSnapshot:
    course_id: Optional[str]
    course_title: Optional[str]
    user_message: str
    chat_summary: str
    active_goals: List[str]
    hard_rules: List[str]
    course_digest: Optional[Dict[str, Any]]
    style_profile: Optional[Dict[str, Any]]
    concept_map: Optional[Dict[str, Any]]
    gap_map: Optional[Dict[str, Any]]
    recent_assignments: List[Dict[str, Any]]
    landmark_assignments: List[Dict[str, Any]]
    recent_drafts: List[Dict[str, Any]]
    last_course_audit: Optional[Dict[str, Any]]
    last_bridge_plan: Optional[Dict[str, Any]]
    raw_payload: Dict[str, Any] = field(default_factory=dict)


@dataclass
class ScenarioRoute:
    scenario_id: str
    confidence: int
    reason: str
    execution_mode: str = "single"
    secondary_scenario_id: Optional[str] = None
    requested_count: Optional[int] = None
    target_concept: Optional[str] = None
    requested_style: Optional[str] = None


@dataclass
class ScenarioDefinition:
    id: str
    name: str
    family: str
    aliases: List[str]
    anti_aliases: List[str]
    default_count: int
    default_mode: str
    required_context: List[str]
    pipeline: List[str]
    output_type: str
    can_run_directly: bool
    needs_course: bool
    needs_approval_before_generation: bool = False


@dataclass
class ScenarioResult:
    type: str
    scenario_id: str
    summary: str
    data: Dict[str, Any]
    confidence: int = 75
    warnings: List[str] = field(default_factory=list)
    validation: Dict[str, Any] = field(default_factory=dict)


@dataclass
class ResultEnvelope:
    kind: str
    scenario_id: str
    status: str
    assistant_message: str
    artifacts: List[Dict[str, Any]]
    suggested_next_actions: List[Dict[str, Any]]
    memory_patch: Dict[str, Any]
    debug: Dict[str, Any]
    created_at_utc: str = field(default_factory=utc_iso)

    def to_dict(self) -> Dict[str, Any]:
        return deep_to_dict(self)
