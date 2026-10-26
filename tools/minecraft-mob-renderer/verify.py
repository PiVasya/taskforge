"""Decode and validate every frame of the deployed media, then write its manifest."""
import hashlib
import json
import pathlib
import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent
REPO = ROOT.parents[1]
MEDIA = REPO / "apps/web/public/updates/media/mobs/animated"
page = json.loads((REPO / "apps/web/public/updates/minecraft-server.json").read_text())
mobs = next(s["items"] for s in page["sections"] if s["type"] == "mobs")
sources = {s["id"]:s for s in json.loads((ROOT / "loop-sources.json").read_text())}
generated = {s["id"]:s for s in json.loads((MEDIA / "rendered-manifest.json").read_text())}
manifest = []
for mob in mobs:
    path = MEDIA / (mob["id"] + ".webp")
    hashes, durations, frames, bounds = set(), [], [], None
    with Image.open(path) as image:
        assert image.size == (512,512) and image.is_animated and image.n_frames > 1, mob["id"]
        assert image.info.get("loop") == 0, mob["id"]
        for n in range(image.n_frames):
            image.seek(n)
            frame = image.convert("RGBA")
            bbox = frame.getchannel("A").getbbox()
            assert bbox and min(bbox[0],bbox[1],512-bbox[2],512-bbox[3]) >= 20, (mob["id"],n,bbox)
            assert frame.getpixel((0,0))[3] == 0, (mob["id"],n)
            hashes.add(hashlib.sha256(frame.tobytes()).hexdigest())
            durations.append(image.info.get("duration",0))
            frames.append(np.asarray(frame.resize((64,64)),dtype=float))
            bounds = bbox if bounds is None else (min(bounds[0],bbox[0]),min(bounds[1],bbox[1]),max(bounds[2],bbox[2]),max(bounds[3],bbox[3]))
        assert len(hashes)>1 and min(durations)>0, mob["id"]
        seam = float(np.abs(frames[-1]-frames[0]).mean())
        adjacent = float(np.mean([np.abs(b-a).mean() for a,b in zip(frames,frames[1:])]))
        if mob["id"] in generated:
            assert seam <= max(2.0,adjacent*2.5), (mob["id"],"loop discontinuity",seam,adjacent)
        item = {"id":mob["id"],"file":path.name,"size":[512,512],"frames":image.n_frames,"distinctFrames":len(hashes),
                "durationMs":sum(durations),"loop":0,"alphaBounds":bounds,"bytes":path.stat().st_size,
                "sha256":hashlib.sha256(path.read_bytes()).hexdigest(),
                "source":sources[mob["id"]]["page"] if mob["id"] in sources else generated[mob["id"]]["source"]}
        manifest.append(item)
        print(f"OK {mob['id']}: {image.n_frames} frames / {len(hashes)} distinct / {sum(durations)} ms / {path.stat().st_size} bytes",flush=True)
assert len(manifest) == 15
(MEDIA / "manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2))
print(f"All 15 valid; total {sum(m['bytes'] for m in manifest):,} bytes",flush=True)
