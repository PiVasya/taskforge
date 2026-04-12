from __future__ import annotations

from typing import Any, Dict, List, Tuple

from jsonschema import Draft7Validator


def _string(min_length: int = 1, max_length: int | None = None) -> Dict[str, Any]:
    schema: Dict[str, Any] = {"type": "string", "minLength": min_length}
    if max_length is not None:
        schema["maxLength"] = max_length
    return schema


CHAT_TURN_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["assistantMessage", "actions"],
    "properties": {
        "assistantMessage": _string(1),
        "sessionTitle": {"type": ["string", "null"], "maxLength": 80},
        "actions": {
            "type": "array",
            "maxItems": 3,
            "items": {
                "type": "object",
                "required": ["name", "reason", "arguments"],
                "properties": {
                    "name": _string(1, 120),
                    "reason": _string(1, 600),
                    "arguments": {"type": "object"},
                },
                "additionalProperties": False,
            },
        },
    },
    "additionalProperties": True,
}

COURSE_PROFILE_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["canonicalRequest", "courseDigest", "courseProfile"],
    "properties": {
        "canonicalRequest": {"type": "object"},
        "courseDigest": {"type": "object"},
        "courseProfile": {"type": "object"},
        "summary": {"type": ["string", "null"]},
        "decisionSummary": {"type": ["object", "null"]},
    },
    "additionalProperties": True,
}

GAP_ANALYSIS_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["gapAnalysis", "coverage"],
    "properties": {
        "gapAnalysis": {"type": "object"},
        "coverage": {"type": "object"},
        "summary": {"type": ["string", "null"]},
        "decisionSummary": {"type": ["object", "null"]},
        "canonicalRequest": {"type": ["object", "null"]},
        "courseDigest": {"type": ["object", "null"]},
    },
    "additionalProperties": True,
}

BATCH_PLAN_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["plan"],
    "properties": {
        "plan": {
            "type": "object",
            "required": ["tasks"],
            "properties": {
                "tasks": {
                    "type": "array",
                    "minItems": 1,
                    "items": {
                        "type": "object",
                        "required": ["targetSkill", "microGoal"],
                        "properties": {
                            "index": {"type": ["integer", "number", "null"]},
                            "titleHint": {"type": ["string", "null"]},
                            "targetSkill": _string(1),
                            "microGoal": _string(1),
                            "placementAfterAssignmentId": {"type": ["string", "null"]},
                            "antiDuplicateHints": {"type": "array", "items": {"type": "string"}},
                        },
                        "additionalProperties": True,
                    },
                },
            },
            "additionalProperties": True,
        },
        "summary": {"type": ["string", "null"]},
        "decisionSummary": {"type": ["object", "null"]},
        "coverage": {"type": ["object", "null"]},
        "canonicalRequest": {"type": ["object", "null"]},
        "planValidation": {"type": ["object", "null"]},
    },
    "additionalProperties": True,
}

BRIEF_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["titleHint", "generationPrompt", "targetSkill"],
    "properties": {
        "titleHint": _string(1),
        "generationPrompt": _string(1),
        "targetSkill": _string(1),
        "summary": {"type": ["string", "null"]},
        "difficultyTarget": {"type": ["integer", "number", "null"]},
        "sourceText": {"type": ["string", "null"]},
        "notes": {"type": ["string", "null"]},
        "decisionLog": {"type": ["array", "null"]},
    },
    "additionalProperties": True,
}

REFERENCE_PACK_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["stylePack", "policyPack", "exemplarPack"],
    "properties": {
        "stylePack": {"type": ["object", "array"]},
        "policyPack": {"type": ["object", "array"]},
        "exemplarPack": {"type": ["object", "array"]},
        "generationHints": {"type": ["array", "null"]},
        "signals": {"type": ["array", "null"]},
    },
    "additionalProperties": True,
}

DRAFT_ONLY_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["draft"],
    "properties": {
        "schemaVersion": {"type": ["string", "null"]},
        "draft": {
            "type": "object",
            "required": ["title", "description", "assignmentType"],
            "properties": {
                "title": _string(1),
                "description": _string(1),
                "assignmentType": _string(1),
                "publicTests": {"type": ["array", "null"]},
                "hiddenTests": {"type": ["array", "null"]},
                "referenceSolutionPython": {"type": ["string", "null"]},
            },
            "additionalProperties": True,
        },
        "summary": {"type": ["string", "null"]},
        "decisionSummary": {"type": ["object", "null"]},
        "draftValidation": {"type": ["object", "null"]},
        "repairSummary": {"type": ["string", "null"]},
    },
    "additionalProperties": True,
}

STYLE_ANALYSIS_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["courseStyle", "titleStyle"],
    "properties": {
        "courseStyle": {"type": "object"},
        "titleStyle": {"type": "object"},
        "antiPatterns": {"type": ["array", "null"]},
        "positivePatterns": {"type": ["array", "null"]},
        "summary": {"type": ["string", "null"]},
    },
    "additionalProperties": True,
}

GENERATION_SPEC_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["generationSpec"],
    "properties": {
        "generationSpec": {
            "type": "object",
            "required": ["exactTask", "ioContract", "constraintsFocus", "titleDirection", "distinctFromPeers"],
            "properties": {
                "exactTask": _string(1),
                "ioContract": _string(1),
                "constraintsFocus": _string(1),
                "titleDirection": _string(1),
                "distinctFromPeers": _string(1),
                "keepStyle": {"type": ["array", "null"]},
                "avoid": {"type": ["array", "null"]},
            },
            "additionalProperties": True,
        },
        "summary": {"type": ["string", "null"]},
    },
    "additionalProperties": True,
}

CONTENT_PLAN_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["contentPlan"],
    "properties": {
        "contentPlan": {
            "type": "object",
            "required": ["pedagogicalGoal", "noveltyHook", "inputModel", "outputModel", "constraintsPlan", "sectionPlan", "publicTestPlan", "hiddenTestPlan", "titleShape"],
            "properties": {
                "pedagogicalGoal": _string(1),
                "noveltyHook": _string(1),
                "inputModel": _string(1),
                "outputModel": _string(1),
                "constraintsPlan": _string(1),
                "sectionPlan": {"type": "array", "minItems": 1},
                "publicTestPlan": {"type": "array", "minItems": 1},
                "hiddenTestPlan": {"type": "array", "minItems": 1},
                "titleShape": _string(1),
            },
            "additionalProperties": True,
        },
        "summary": {"type": ["string", "null"]},
    },
    "additionalProperties": True,
}

STAGE_SCHEMA_BY_NAME: Dict[str, Dict[str, Any]] = {
    "assistant_chat_turn": CHAT_TURN_SCHEMA,
    "chat_turn": CHAT_TURN_SCHEMA,
    "course_profile_build": COURSE_PROFILE_SCHEMA,
    "assignment_course_profile_build": COURSE_PROFILE_SCHEMA,
    "gap_analysis": GAP_ANALYSIS_SCHEMA,
    "assignment_gap_analysis": GAP_ANALYSIS_SCHEMA,
    "batch_plan": BATCH_PLAN_SCHEMA,
    "assignment_batch_plan": BATCH_PLAN_SCHEMA,
    "assignment_batch_replan": BATCH_PLAN_SCHEMA,
    "brief": BRIEF_SCHEMA,
    "assignment_brief_generate": BRIEF_SCHEMA,
    "assignment_brief_repair": BRIEF_SCHEMA,
    "reference_pack": REFERENCE_PACK_SCHEMA,
    "assignment_reference_pack_build": REFERENCE_PACK_SCHEMA,
    "draft_generate": DRAFT_ONLY_SCHEMA,
    "assignment_generate_from_text": DRAFT_ONLY_SCHEMA,
    "assignment_repair": DRAFT_ONLY_SCHEMA,
    "repair": DRAFT_ONLY_SCHEMA,
    "draft_course_style_analysis": STYLE_ANALYSIS_SCHEMA,
    "draft_generation_spec": GENERATION_SPEC_SCHEMA,
    "draft_content_plan": CONTENT_PLAN_SCHEMA,
}


def schema_for_stage(stage_name: str | None) -> Dict[str, Any] | None:
    if not stage_name:
        return None
    return STAGE_SCHEMA_BY_NAME.get(str(stage_name).strip())


def openrouter_response_format(stage_name: str | None) -> Dict[str, Any] | None:
    schema = schema_for_stage(stage_name)
    if not schema:
        return None
    return {
        "name": str(stage_name).replace("/", "_").replace(" ", "_")[:64] or "taskforge_stage",
        "strict": True,
        "schema": schema,
    }


def validate_stage_result(stage_name: str | None, result: Any) -> Tuple[bool, List[str]]:
    schema = schema_for_stage(stage_name)
    if not schema:
        return True, []
    validator = Draft7Validator(schema)
    errors = sorted(validator.iter_errors(result), key=lambda err: list(err.path))
    if not errors:
        return True, []
    return False, [f"{'.'.join(str(part) for part in err.path) or '$'}: {err.message}" for err in errors[:12]]
