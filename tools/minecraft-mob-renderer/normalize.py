"""Normalize complete existing loops without changing their camera or motion."""
import argparse
import hashlib
import json
import pathlib
import subprocess
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent
OUTPUT = ROOT.parents[1] / "apps/web/public/updates/media/mobs/animated"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=pathlib.Path, default=ROOT / "cache")
    args = parser.parse_args()
    args.source_dir.mkdir(parents=True,exist_ok=True)
    OUTPUT.mkdir(parents=True,exist_ok=True)
    metadata = []
    for source in json.loads((ROOT / "loop-sources.json").read_text()):
        path = args.source_dir / source["filename"]
        if not path.exists():
            subprocess.run(["curl","-fLsS","--retry","2","--max-time","45","-o",str(path),source["url"]],check=True)
        assert hashlib.sha256(path.read_bytes()).hexdigest() == source["sha256"], f"Source changed: {source['id']}"
        frames, durations, union = [], [], None
        with Image.open(path) as image:
            for i in range(image.n_frames):
                image.seek(i)
                frame = image.convert("RGBA")
                frames.append(frame)
                durations.append(max(20,image.info.get("duration",40)))
                bounds = frame.getchannel("A").getbbox()
                if bounds:
                    union = bounds if union is None else (min(union[0],bounds[0]),min(union[1],bounds[1]),max(union[2],bounds[2]),max(union[3],bounds[3]))
        assert len(frames)>1 and union, source["id"]
        width,height = union[2]-union[0],union[3]-union[1]
        scale = 416/max(width,height)
        target = (round(width*scale),round(height*scale))
        normalized = []
        for frame in frames:
            crop = frame.crop(union).resize(target,Image.Resampling.NEAREST)
            canvas = Image.new("RGBA",(512,512))
            canvas.alpha_composite(crop,((512-target[0])//2,(512-target[1])//2))
            normalized.append(canvas)
        output = OUTPUT / f"{source['id']}.webp"
        normalized[0].save(output,format="WEBP",save_all=True,append_images=normalized[1:],duration=durations,loop=0,quality=90,method=4,exact=True)
        metadata.append({"id":source["id"],"file":output.name,"source":source["page"],"sourceSha256":source["sha256"],"size":[512,512],"frames":len(frames),"durationMs":sum(durations),"sha256":hashlib.sha256(output.read_bytes()).hexdigest()})
        print(f"Normalized {source['id']}: {len(frames)} frames, {output.stat().st_size} bytes",flush=True)
    (OUTPUT / "normalized-manifest.json").write_text(json.dumps(metadata,ensure_ascii=False,indent=2))


if __name__ == "__main__":
    main()
