import os

import open_clip


def main() -> None:
    model_name = os.getenv("MODEL_NAME", "ViT-L-14")
    pretrained = os.getenv("MODEL_PRETRAINED", "laion2b_s32b_b82k")
    cache_dir = os.getenv("OPENCLIP_CACHE_DIR", "/models/openclip")

    os.makedirs(cache_dir, exist_ok=True)
    os.environ["OPENCLIP_CACHE_DIR"] = cache_dir
    os.environ["HF_HOME"] = cache_dir
    os.environ["HUGGINGFACE_HUB_CACHE"] = cache_dir
    os.environ["HF_HUB_CACHE"] = cache_dir
    os.environ.setdefault("HF_HUB_DISABLE_XET", "1")

    # This will download the weights into the configured persistent model cache.
    open_clip.create_model_and_transforms(
        model_name,
        pretrained=pretrained,
        cache_dir=cache_dir,
    )

    print(f"Downloaded OpenCLIP weights: {model_name} / {pretrained} -> {cache_dir}")


if __name__ == "__main__":
    main()
