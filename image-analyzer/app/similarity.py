import io
import os
from dataclasses import dataclass

import numpy as np
from PIL import Image
import imagehash

import torch
import open_clip


def _env_float(name: str, default: float) -> float:
    v = os.getenv(name)
    if v is None or v.strip() == "":
        return default
    try:
        return float(v)
    except ValueError:
        return default


@dataclass(frozen=True)
class CompareResult:
    clip_similarity: float
    phash_similarity: float
    combined_similarity: float
    model: str


class ImageComparator:
    """CLIP + pHash comparator.

    - CLIP similarity is best for "semantically same" images.
    - pHash adds stability for tiny pixel-level changes.
    """

    def __init__(self) -> None:
        self.model_name = os.getenv("MODEL_NAME", "ViT-L-14")
        self.pretrained = os.getenv("MODEL_PRETRAINED", "laion2b_s32b_b82k")
        self.cache_dir = os.getenv("OPENCLIP_CACHE_DIR", "/models/openclip")

        self.clip_weight = _env_float("CLIP_WEIGHT", 0.85)
        self.phash_weight = _env_float("PHASH_WEIGHT", 0.15)
        s = self.clip_weight + self.phash_weight
        if s <= 0:
            self.clip_weight, self.phash_weight = 0.85, 0.15
            s = 1.0
        self.clip_weight /= s
        self.phash_weight /= s

        os.makedirs(self.cache_dir, exist_ok=True)
        os.environ.setdefault("OPENCLIP_CACHE_DIR", self.cache_dir)

        # CPU-first. If you run the container with GPU + CUDA build, this will use CUDA.
        self.device = "cuda" if torch.cuda.is_available() else "cpu"

        self.model, _, self.preprocess = open_clip.create_model_and_transforms(
            self.model_name,
            pretrained=self.pretrained,
        )
        self.model.to(self.device)
        self.model.eval()

        self._model_label = f"{self.model_name} / {self.pretrained}"

    @property
    def model_label(self) -> str:
        return self._model_label

    @property
    def device_name(self) -> str:
        return self.device

    def compare(self, expected_bytes: bytes, actual_bytes: bytes) -> CompareResult:
        exp = self._load_rgb(expected_bytes)
        act = self._load_rgb(actual_bytes)

        clip_sim = float(self._clip_cosine(exp, act))
        phash_sim = float(self._phash_similarity(exp, act))
        combined = float(self.clip_weight * clip_sim + self.phash_weight * phash_sim)
        combined = min(max(combined, 0.0), 1.0)

        return CompareResult(
            clip_similarity=clip_sim,
            phash_similarity=phash_sim,
            combined_similarity=combined,
            model=self._model_label,
        )

    @staticmethod
    def _load_rgb(b: bytes) -> Image.Image:
        with Image.open(io.BytesIO(b)) as im:
            return im.convert("RGB")

    def _clip_cosine(self, a: Image.Image, b: Image.Image) -> float:
        with torch.no_grad():
            ta = self.preprocess(a).unsqueeze(0).to(self.device)
            tb = self.preprocess(b).unsqueeze(0).to(self.device)

            ea = self.model.encode_image(ta)
            eb = self.model.encode_image(tb)

            ea = ea / ea.norm(dim=-1, keepdim=True)
            eb = eb / eb.norm(dim=-1, keepdim=True)
            sim = (ea * eb).sum(dim=-1).item()

        # Cosine similarity in [-1..1] => map to [0..1]
        sim01 = (sim + 1.0) / 2.0
        return float(min(max(sim01, 0.0), 1.0))

    @staticmethod
    def _phash_similarity(a: Image.Image, b: Image.Image) -> float:
        # 64-bit perceptual hash by default (size=8)
        ha = imagehash.phash(a)
        hb = imagehash.phash(b)
        dist = ha - hb
        # dist in [0..64]
        sim = 1.0 - (dist / 64.0)
        return float(min(max(sim, 0.0), 1.0))
