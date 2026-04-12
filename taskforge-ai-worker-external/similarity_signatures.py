"""Signature-based near-duplicate helpers for draft/reference comparison."""

from __future__ import annotations

import hashlib
from typing import Any, Dict, Iterable, List

from text_utils import normalize_text, tokenize_similarity_text


def _token_shingles(tokens: List[str], size: int = 3) -> List[str]:
    if not tokens:
        return []
    effective = min(size, len(tokens))
    if effective <= 1:
        return tokens[:]
    return [" ".join(tokens[idx: idx + effective]) for idx in range(0, len(tokens) - effective + 1)]


def stable_hash64(text: str) -> int:
    digest = hashlib.blake2b(text.encode("utf-8", errors="ignore"), digest_size=8).digest()
    return int.from_bytes(digest, "big", signed=False)


def simhash_tokens(tokens: Iterable[str]) -> int:
    vector = [0] * 64
    token_list = [str(token) for token in tokens if str(token).strip()]
    if not token_list:
        return 0
    for token in token_list:
        value = stable_hash64(token)
        weight = max(1, min(4, len(token.split())))
        for bit in range(64):
            if value & (1 << bit):
                vector[bit] += weight
            else:
                vector[bit] -= weight
    out = 0
    for bit, acc in enumerate(vector):
        if acc >= 0:
            out |= (1 << bit)
    return out


def hamming_distance64(a: int, b: int) -> int:
    return (a ^ b).bit_count()


def similarity_signature_report(a: Any, b: Any, *, shingle_size: int = 3) -> Dict[str, float | int]:
    a_tokens = tokenize_similarity_text(normalize_text(a))
    b_tokens = tokenize_similarity_text(normalize_text(b))
    sizes = sorted({max(1, min(shingle_size, len(a_tokens) or 1, len(b_tokens) or 1)), 2})
    best_shingles_a: set[str] = set()
    best_shingles_b: set[str] = set()
    best_shingle_jaccard = 0.0
    best_shared = 0
    for size in sizes:
        a_shingles = set(_token_shingles(a_tokens, size))
        b_shingles = set(_token_shingles(b_tokens, size))
        union = len(a_shingles | b_shingles)
        shared = len(a_shingles & b_shingles)
        score = (shared / union) if union else 0.0
        if score >= best_shingle_jaccard:
            best_shingle_jaccard = score
            best_shingles_a = a_shingles
            best_shingles_b = b_shingles
            best_shared = shared
    a_simhash = simhash_tokens(best_shingles_a or a_tokens)
    b_simhash = simhash_tokens(best_shingles_b or b_tokens)
    hamming = hamming_distance64(a_simhash, b_simhash)
    simhash_similarity = 1.0 - (hamming / 64.0)
    token_overlap = (len(set(a_tokens) & set(b_tokens)) / max(1, len(set(a_tokens) | set(b_tokens)))) if (a_tokens or b_tokens) else 0.0
    combined = max(best_shingle_jaccard, simhash_similarity, token_overlap, (best_shingle_jaccard * 0.45 + simhash_similarity * 0.35 + token_overlap * 0.20))
    return {
        "shingleJaccard": round(best_shingle_jaccard, 4),
        "simhashSimilarity": round(simhash_similarity, 4),
        "tokenOverlap": round(token_overlap, 4),
        "hammingDistance": hamming,
        "sharedShingles": best_shared,
        "combined": round(combined, 4),
    }
