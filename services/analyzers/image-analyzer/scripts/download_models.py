import os

import open_clip


def main() -> None:
    model_name = os.getenv("MODEL_NAME", "ViT-L-14")
    pretrained = os.getenv("MODEL_PRETRAINED", "laion2b_s32b_b82k")
    cache_dir = os.getenv("OPENCLIP_CACHE_DIR", "/models/openclip")

    os.makedirs(cache_dir, exist_ok=True)
    os.environ["OPENCLIP_CACHE_DIR"] = cache_dir

    # This will download the weights into OPENCLIP_CACHE_DIR.
    open_clip.create_model_and_transforms(model_name, pretrained=pretrained)

    print(f"Downloaded OpenCLIP weights: {model_name} / {pretrained} -> {cache_dir}")


if __name__ == "__main__":
    main()
