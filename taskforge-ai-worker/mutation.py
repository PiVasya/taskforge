"""Proxy-mutation testing for code-test drafts."""

from typing import Any, Dict, List

from text_utils import normalize_text
from runners import run_python_solution, run_code_test_suite


def build_python_mutants(source_code: str) -> List[Dict[str, Any]]:
    patterns = [
        ("boundary", "off_by_one_plus", "+ 1", "+ 0", "Потенциальный off-by-one в +1"),
        ("boundary", "off_by_one_minus", "- 1", "- 0", "Потенциальный off-by-one в -1"),
        ("comparison", "strict_gt", ">=", ">", "Ослабление граничного условия >= -> >"),
        ("comparison", "strict_lt", "<=", "<", "Ослабление граничного условия <= -> <"),
        ("comparison", "equality_flip", "==", "!=", "Инверсия equality-проверки"),
        ("whitespace", "drop_strip", ".strip()", "", "Убрать strip() и проверить пробелы/пустой ввод"),
        ("whitespace", "drop_split_strip", "splitlines()", "split('\\n')", "Ослабить нормализацию переноса строк"),
        ("aggregation", "reverse_sort", "sorted(", "list(", "Сломать сортировку/нормализацию порядка"),
    ]
    mutants: List[Dict[str, Any]] = []
    seen: set = set()
    for family, name, needle, repl, summary in patterns:
        if needle not in source_code:
            continue
        mutated = source_code.replace(needle, repl, 1)
        if mutated == source_code or mutated in seen:
            continue
        try:
            compile(mutated, "<mutant>", "exec")
        except Exception:
            continue
        seen.add(mutated)
        mutants.append({"family": family, "name": name, "code": mutated, "summary": summary})
    if "replace(" in source_code:
        mutants.append({
            "family": "string-processing",
            "name": "first_occurrence_bias",
            "code": source_code,
            "summary": "Нужны тесты, отличающие замену первого и всех вхождений.",
        })
    return mutants[:10]


def assess_mutation_strength(draft: Dict[str, Any]) -> Dict[str, Any]:
    if str(draft.get("assignmentType") or "").strip().lower() != "code-test":
        return {
            "generated": 0, "killed": 0, "survived": 0, "skipped": 0,
            "killRatio": 1.0, "survivors": [], "mutants": [],
        }
    tests = [
        t for t in list(draft.get("publicTests") or []) + list(draft.get("hiddenTests") or [])
        if isinstance(t, dict)
    ]
    source_code = str(draft.get("referenceSolutionPython") or "")
    mutants = build_python_mutants(source_code)
    if not source_code or not tests or not mutants:
        return {
            "generated": len(mutants), "killed": 0, "survived": 0, "skipped": 0,
            "killRatio": 1.0 if not mutants else 0.0,
            "survivors": [], "mutants": mutants,
        }
    survivors: List[Dict[str, Any]] = []
    killed = 0
    skipped = 0
    for mutant in mutants:
        code = mutant.get("code")
        if not isinstance(code, str) or code == source_code:
            skipped += 1
            continue
        suite = run_code_test_suite(code, tests)
        if suite.get("total") == 0:
            skipped += 1
            continue
        if suite.get("passed") == suite.get("total"):
            survivors.append({"name": mutant.get("name"), "summary": mutant.get("summary")})
        else:
            killed += 1
    executed = max(1, len(mutants) - skipped)
    return {
        "generated": len(mutants),
        "killed": killed,
        "survived": len(survivors),
        "skipped": skipped,
        "killRatio": round(killed / executed, 3),
        "survivors": survivors[:5],
        "families": sorted({str(m.get("family") or "general") for m in mutants}),
        "mutants": [{"family": m.get("family"), "name": m.get("name"), "summary": m.get("summary")} for m in mutants],
    }
