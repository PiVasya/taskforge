"""Pure text-processing utilities — no IO, no network."""

import ast
import difflib
import re
from typing import Any, List, Optional, Tuple


# ── Basic normalisation ──────────────────────────────

def normalize_text(value: Any) -> str:
    """Strip and normalise line endings."""
    return str(value or "").replace("\r\n", "\n").strip()


def truncate_text(value: Any, limit: int) -> str:
    text = normalize_text(value)
    return text if len(text) <= limit else text[:limit] + "..."


def summarize_description(value: Any, limit: int = 260) -> str:
    raw = str(value or "")
    raw = raw.replace("\r\n", " ").replace("\n", " ").replace("\r", " ")
    raw = " ".join(raw.split())
    return raw if len(raw) <= limit else raw[:limit] + "..."


# ── Safe number helpers ──────────────────────────────

def safe_int(value: Any, default: int = 0) -> int:
    """Convert *value* to int robustly (handles float-strings, None, etc.)."""
    if value is None:
        return default
    try:
        return int(value)
    except (ValueError, TypeError):
        try:
            return int(float(value))
        except (ValueError, TypeError):
            return default


def safe_eval_number(expr: str) -> Optional[float]:
    try:
        node = ast.parse(expr, mode="eval")
    except Exception:
        return None
    safe_names = {
        "pi": 3.141592653589793,
        "e": 2.718281828459045,
        "abs": abs,
        "sqrt": lambda x: x ** 0.5,
    }
    allowed = (
        ast.Expression, ast.BinOp, ast.UnaryOp, ast.Constant,
        ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Pow,
        ast.USub, ast.UAdd, ast.Mod, ast.FloorDiv, ast.Load,
        ast.Call, ast.Name,
    )
    for child in ast.walk(node):
        if not isinstance(child, allowed):
            return None
        if isinstance(child, ast.Call) and (
            not isinstance(child.func, ast.Name) or child.func.id not in safe_names
        ):
            return None
        if isinstance(child, ast.Name) and child.id not in safe_names:
            return None
    try:
        return float(eval(compile(node, "<expr>", "eval"), {"__builtins__": {}}, safe_names))
    except Exception:
        return None


# ── List / policy helpers ────────────────────────────

def unique_string_list(values: Any, limit: int = 12) -> List[str]:
    result: List[str] = []
    seen: set = set()
    if not isinstance(values, list):
        return result
    for raw in values:
        text = normalize_text(raw)
        if not text:
            continue
        key = text.casefold()
        if key in seen:
            continue
        seen.add(key)
        result.append(text)
        if len(result) >= limit:
            break
    return result


def strip_conflicting_lists(
    preferred: Any, blocked: Any, limit: int = 12
) -> Tuple[List[str], List[str], List[str]]:
    preferred_list = unique_string_list(preferred, limit)
    blocked_list = unique_string_list(blocked, limit)
    preferred_keys = {x.casefold() for x in preferred_list}
    overlap = [x for x in blocked_list if x.casefold() in preferred_keys]
    blocked_list = [x for x in blocked_list if x.casefold() not in preferred_keys]
    return preferred_list, blocked_list, overlap


# ── HTML detection ───────────────────────────────────

def has_html_markup(text: str) -> bool:
    lowered = text.lower()
    return any(
        tag in lowered
        for tag in ["<p", "<ul", "<ol", "<li", "<strong", "<br", "<h1", "<h2", "<h3"]
    )


# ── Similarity / tokenisation ────────────────────────

def tokenize_similarity_text(value: Any) -> List[str]:
    text = normalize_text(value).lower()
    cleaned = []
    for ch in text:
        cleaned.append(ch if ch.isalnum() or ch.isspace() else " ")
    return [x for x in "".join(cleaned).split() if x]


def jaccard(set_a: set, set_b: set) -> float:
    """Jaccard similarity of two sets.  Returns 0.0 when both are empty."""
    if not set_a and not set_b:
        return 0.0
    return len(set_a & set_b) / len(set_a | set_b)


def compute_text_similarity(a: Any, b: Any) -> float:
    a_text = normalize_text(a)
    b_text = normalize_text(b)
    if not a_text or not b_text:
        return 0.0
    seq = difflib.SequenceMatcher(None, a_text.lower(), b_text.lower()).ratio()
    a_tokens = set(tokenize_similarity_text(a_text))
    b_tokens = set(tokenize_similarity_text(b_text))
    j = jaccard(a_tokens, b_tokens)
    return max(seq, j)


# ── HTML / sentence helpers ─────────────────────────

def strip_html_to_text(value: Any) -> str:
    raw = normalize_text(value)
    if not raw:
        return ""
    text = re.sub(r"<\s*br\s*/?>", "\n", raw, flags=re.IGNORECASE)
    text = re.sub(r"</\s*(p|div|section|article|h1|h2|h3|h4|h5|h6|li)\s*>", "\n\n", text, flags=re.IGNORECASE)
    text = re.sub(r"<\s*li[^>]*>", "• ", text, flags=re.IGNORECASE)
    text = re.sub(r"<[^>]+>", "", text, flags=re.IGNORECASE)
    text = text.replace("&nbsp;", " ")
    text = re.sub(r"\n{3,}", "\n\n", text)
    return normalize_text(text)


def extract_first_meaningful_sentence(value: Any, limit: int = 80) -> str:
    text = strip_html_to_text(value) or normalize_text(value)
    if not text:
        return ""
    pieces = re.split(r"(?<=[.!?])\s+|\n\n+", text)
    for piece in pieces:
        candidate = normalize_text(piece)
        if not candidate:
            continue
        candidate = re.sub(r"^(Условие|Входные данные|Выходные данные|Ограничения|Примечания)\s*:?\s*", "", candidate, flags=re.IGNORECASE)
        if candidate:
            return truncate_text(candidate, limit)
    return truncate_text(text.replace("\n", " "), limit)
