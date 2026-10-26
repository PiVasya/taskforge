"""Fetch pinned Mojang geometry/textures for the offline news-media renderer."""
import concurrent.futures
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent
COMMIT = "46ba6ea985fb5a92d79a9419198f10dda14c199d"
BASE = f"https://raw.githubusercontent.com/Mojang/bedrock-samples/{COMMIT}/resource_pack/"
FILES = [
    "models/entity/wither_skeleton.geo.json", "models/entity/snow_golem.geo.json",
    "models/entity/drowned.geo.json", "models/entity/stray.geo.json",
    "models/entity/stray_armor.geo.json", "models/entity/parched.geo.json",
    "models/entity/evoker.geo.json", "models/entity/zombie.geo.json",
    "models/entity/trident.geo.json",
    "textures/entity/skeleton/wither_skeleton.png", "textures/entity/snow_golem.png",
    "textures/entity/zombie/drowned.tga", "textures/entity/skeleton/stray.png",
    "textures/entity/skeleton/stray_overlay.png", "textures/entity/parched/parched.png",
    "textures/entity/zombie/zombie.png", "textures/entity/trident.png",
    "textures/items/bow_standby.png", "textures/items/stone_sword.png",
    "textures/blocks/pumpkin_face_off.png", "textures/blocks/pumpkin_side.png",
    "textures/blocks/pumpkin_top.png",
]


def fetch(entry):
    relative, url = entry
    path = ROOT / "assets" / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        subprocess.run(["curl", "-fLsS", "--retry", "2", "--max-time", "35", "-o", str(path), url], check=True)
    return {"path": relative, "url": url, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


if __name__ == "__main__":
    entries = [(name, BASE + name) for name in FILES]
    entries.append(("textures/entity/illusioner.png", "https://minecraft.wiki/images/Illusioner_%28texture%29.png?8523f"))
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        sources = list(pool.map(fetch, entries))
    (ROOT / "sources.json").write_text(json.dumps({"mojangCommit": COMMIT, "files": sources}, indent=2))
    print(f"Fetched {len(sources)} pinned geometry/texture sources")
