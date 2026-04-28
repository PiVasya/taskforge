from __future__ import annotations

import re
from typing import Any, Dict, Iterable, List, Optional


# Canonical runner language ids used by TaskForge runner containers and the AI payloads.
CANONICAL_LANGUAGES = {"cpp", "csharp", "python", "javascript", "pascal", "java"}

_LANGUAGE_ALIASES = {
    "cpp": {"c++", "cpp", "g++", "gcc", "cxx", "си++", "с++"},
    "csharp": {"c#", "csharp", "cs", "sharp", "шарп", "си#", "с#"},
    "python": {"py", "python", "python3", "питон"},
    "javascript": {"js", "javascript", "java-script", "node", "nodejs", "node.js"},
    "pascal": {"pas", "pascal", "паскаль"},
    "java": {"java", "джава"},
}

# Canonical field names for language-specific reference solutions.
LANGUAGE_CANONICAL_SOLUTION_KEY = {
    "cpp": "referenceSolutionCpp",
    "csharp": "referenceSolutionCsharp",
    "python": "referenceSolutionPython",
    "javascript": "referenceSolutionJavascript",
    "pascal": "referenceSolutionPascal",
    "java": "referenceSolutionJava",
}

LANGUAGE_SOLUTION_KEYS = {
    "cpp": ["referenceSolutionCpp", "solutionCpp", "referenceSolutionCxx", "solutionCxx", "referenceSolution", "solution"],
    "csharp": ["referenceSolutionCsharp", "referenceSolutionCSharp", "referenceSolutionCs", "solutionCsharp", "solutionCSharp", "solutionCs", "referenceSolution", "solution"],
    "python": ["referenceSolutionPython", "solutionPython", "referenceSolutionPy", "solutionPy", "referenceSolution", "solution"],
    "javascript": ["referenceSolutionJavascript", "referenceSolutionJavaScript", "referenceSolutionJs", "solutionJavascript", "solutionJavaScript", "solutionJs", "referenceSolution", "solution"],
    "pascal": ["referenceSolutionPascal", "solutionPascal", "referenceSolutionPas", "solutionPas", "referenceSolution", "solution"],
    "java": ["referenceSolutionJava", "solutionJava", "referenceSolution", "solution"],
}

SOLUTION_KEYS: List[str] = []
for _keys in LANGUAGE_SOLUTION_KEYS.values():
    for _key in _keys:
        if _key not in SOLUTION_KEYS:
            SOLUTION_KEYS.append(_key)


LANGUAGE_DISPLAY_NAMES = {
    "cpp": "C++",
    "csharp": "C#",
    "python": "Python",
    "javascript": "JavaScript",
    "pascal": "Pascal",
    "java": "Java",
}


def normalize_language(value: Any) -> str:
    text = str(value or "").strip().lower().replace("_", "-")
    if not text:
        return ""
    for canonical, aliases in _LANGUAGE_ALIASES.items():
        if text in aliases:
            return canonical
    return text


def solution_text_for_language(item: Dict[str, Any], language: Any) -> str:
    lang = normalize_language(language)
    for key in LANGUAGE_SOLUTION_KEYS.get(lang, []):
        value = item.get(key)
        if value:
            return str(value)
    return any_solution_text(item)


def any_solution_text(item: Dict[str, Any]) -> str:
    for key in SOLUTION_KEYS:
        value = item.get(key)
        if value:
            return str(value)
    return ""


def ensure_language_solution_field(item: Dict[str, Any], language: Any) -> None:
    """Copy a generic or alias solution into the canonical key for the language.

    This keeps downstream code/UI consistent without losing the original fields returned by the LLM.
    """
    lang = normalize_language(language)
    canonical = LANGUAGE_CANONICAL_SOLUTION_KEY.get(lang)
    if not canonical or item.get(canonical):
        return
    value = solution_text_for_language(item, lang)
    if value:
        item[canonical] = value


def language_solution_schema_fields(default_language: Any = None) -> Dict[str, str | None]:
    """Return all supported solution fields for JSON schemas/prompts.

    The expected language field is marked with a string, all others with null.
    """
    expected = normalize_language(default_language)
    fields: Dict[str, str | None] = {}
    for lang, key in LANGUAGE_CANONICAL_SOLUTION_KEY.items():
        fields[key] = "string|null" if not expected or lang == expected else None
    fields["referenceSolution"] = "string|null"
    return fields


def detect_expected_language_from_text(parts: Iterable[Any]) -> Optional[str]:
    # Prefer more specific tokens first. Do not infer Java from JavaScript.
    haystack = " ".join(str(part or "") for part in parts).lower()
    if re.search(r"(c\s*#|csharp|\bcs\b|си\s*#|с\s*#|шарп)", haystack):
        return "csharp"
    if re.search(r"(c\s*\+\+|cpp|g\+\+|\bcxx\b|си\s*\+\+|с\s*\+\+)", haystack):
        return "cpp"
    if re.search(r"(python3?|\bpy\b|питон)", haystack):
        return "python"
    if re.search(r"(javascript|java-script|node\.?js|\bnode\b|\bjs\b)", haystack):
        return "javascript"
    if re.search(r"(pascal|паскаль|\bpas\b)", haystack):
        return "pascal"
    if re.search(r"\bjava\b|джава", haystack):
        return "java"
    return None


def language_prompt_line(language: Any) -> str:
    lang = normalize_language(language) or "язык курса"
    display = LANGUAGE_DISPLAY_NAMES.get(lang, lang)
    key = LANGUAGE_CANONICAL_SOLUTION_KEY.get(lang, "referenceSolution")
    return (
        f"Для code-test сохраняй язык {display}: language='{lang}', allowedLanguages=['{lang}'], "
        f"эталонное решение заполняй в поле {key}. Поля решений для других языков оставляй null. "
        "Для test/math кодовое решение не требуется."
    )
