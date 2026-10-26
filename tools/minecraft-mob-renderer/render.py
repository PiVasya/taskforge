"""Offline textured-cuboid rendering. No browser/AI-generated approximations of mobs."""
import argparse
import copy
import hashlib
import json
import math
import pathlib

import moderngl
import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent
ASSETS = ROOT / "assets"
REPO = ROOT.parents[1]
OUTPUT = REPO / "apps/web/public/updates/media/mobs/animated"
NAMES = ["wither-skeleton", "snow-golem", "drowned", "stray", "parched", "illusioner", "trident-zombie"]
FRAMES = 80
FRAME_MS = 50
SIZE = 512


def translate(v):
    m = np.eye(4)
    m[:3, 3] = v
    return m


def rotation(x=0, y=0, z=0):
    x, y, z = np.radians([x, y, z])
    cx, cy, cz, sx, sy, sz = math.cos(x), math.cos(y), math.cos(z), math.sin(x), math.sin(y), math.sin(z)
    rx = np.array([[1, 0, 0, 0], [0, cx, -sx, 0], [0, sx, cx, 0], [0, 0, 0, 1.]])
    ry = np.array([[cy, 0, sy, 0], [0, 1, 0, 0], [-sy, 0, cy, 0], [0, 0, 0, 1.]])
    rz = np.array([[cz, -sz, 0, 0], [sz, cz, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1.]])
    return rz @ ry @ rx


def geometry(name):
    data = json.loads((ASSETS / f"models/entity/{name}.geo.json").read_text())
    if "minecraft:geometry" in data:
        geo = data["minecraft:geometry"][0]
        return copy.deepcopy(geo["bones"]), (geo["description"].get("texture_width",64), geo["description"].get("texture_height",64))
    geo = next(v for k, v in data.items() if k.startswith("geometry."))
    return copy.deepcopy(geo["bones"]), (geo["texturewidth"], geo["textureheight"])


def transformed(points, matrix):
    p = np.column_stack([points, np.ones(len(points))])
    return (p @ matrix.T)[:, :3]


def cube_faces(cube, dimensions, inherited_inflate=0, mirror=False):
    x, y, z = np.array(cube["origin"], dtype=float)
    w, h, d = np.array(cube["size"], dtype=float)
    inflate = cube.get("inflate", inherited_inflate)
    x0, y0, z0, x1, y1, z1 = x-inflate, y-inflate, z-inflate, x+w+inflate, y+h+inflate, z+d+inflate
    points = {
        "north": [[x0,y1,z0],[x1,y1,z0],[x1,y0,z0],[x0,y0,z0]],
        "east": [[x1,y1,z0],[x1,y1,z1],[x1,y0,z1],[x1,y0,z0]],
        "south": [[x1,y1,z1],[x0,y1,z1],[x0,y0,z1],[x1,y0,z1]],
        "west": [[x0,y1,z1],[x0,y1,z0],[x0,y0,z0],[x0,y0,z1]],
        "up": [[x0,y1,z1],[x1,y1,z1],[x1,y1,z0],[x0,y1,z0]],
        "down": [[x0,y0,z0],[x1,y0,z0],[x1,y0,z1],[x0,y0,z1]],
    }
    normals = {"north":[0,0,-1],"east":[1,0,0],"south":[0,0,1],"west":[-1,0,0],"up":[0,1,0],"down":[0,-1,0]}
    uv = cube.get("uv", [0, 0])
    if isinstance(uv, list):
        u, v = uv
        rects = {"north":[u+d,v+d,w,h],"east":[u+d+w,v+d,d,h],"south":[u+2*d+w,v+d,w,h],
                 "west":[u,v+d,d,h],"up":[u+d,v,w,d],"down":[u+d+w,v,w,d]}
    else:
        rects = {face:[*spec["uv"],*spec.get("uv_size", [w,h])] for face,spec in uv.items()}
    own = np.eye(4)
    if cube.get("rotation"):
        pivot = np.array(cube.get("pivot", [0,0,0]))
        own = translate(pivot) @ rotation(*cube["rotation"]) @ translate(-pivot)
    result = []
    for face, rect in rects.items():
        u, v, uw, vh = rect
        if uw == 0 or vh == 0:
            continue
        tex = np.array([[u,v],[u+uw,v],[u+uw,v+vh],[u,v+vh]], dtype=float)
        if cube.get("mirror", mirror):
            tex[:,0] = 2 * u + uw - tex[:,0]
        tex /= np.array(dimensions)
        pts = transformed(points[face], own)
        normal = own[:3,:3] @ np.array(normals[face])
        result.append((pts, tex, normal))
    return result


def pose(name, t):
    s, c = math.sin(t), math.cos(t)
    result = {"body": [0.5*c, 1.4*s, 0.7*s], "head": [1.8*math.sin(t+0.3), 5*math.sin(t), 0],
              "rightArm":[8+2*s,0,-3-1.3*c], "leftArm":[5-2*s,0,3+1.3*c],
              "rightLeg":[0,0,0], "leftLeg":[0,0,0]}
    if name in ("stray", "parched", "illusioner"):
        result["leftArm"] = [12+2*s, -4, 8+1.2*c]
        result["rightArm"] = [5+2*s, 0, -4-1.2*c]
    if name in ("drowned", "trident-zombie"):
        result["leftArm"] = [51+3*s, -6, 4]
        result["rightArm"] = [10+2*s, -4, -12-1.2*c]
    if name == "snow-golem":
        result = {"piece1":[0,3.0*s,1.2*c], "head":[2*s,7*math.sin(t+.6),0],
                  "arm1":[4*s,8,-16+4*c], "arm2":[-4*s,172,16-4*c]}
    return result


class Mob:
    def __init__(self, name):
        self.name = name
        model = {"wither-skeleton":"wither_skeleton", "snow-golem":"snow_golem", "trident-zombie":"zombie", "illusioner":"evoker"}.get(name, name)
        tex = {"wither-skeleton":"entity/skeleton/wither_skeleton.png", "snow-golem":"entity/snow_golem.png",
               "drowned":"entity/zombie/drowned.tga", "stray":"entity/skeleton/stray.png",
               "parched":"entity/parched/parched.png", "illusioner":"entity/illusioner.png", "trident-zombie":"entity/zombie/zombie.png"}[name]
        bones, dimensions = geometry(model)
        if name == "illusioner":
            bones = [b for b in bones if b["name"] != "arms"]
            bones.append({"name":"cap", "parent":"head", "pivot":[0,24,0],
                          "cubes":[{"origin":[-4,24,-4],"size":[8,12,8],"uv":[32,0],"inflate":.2}]})
        self.bones = {b["name"]: b for b in bones}
        self.layers = [(bones, dimensions, tex)]
        if name == "stray":
            overlay, dims = geometry("stray_armor")
            self.layers.append((overlay, dims, "entity/skeleton/stray_overlay.png"))
        self.hand_items = []
        self.sprites = {}
        for _, filename, *_ in self.hand_items:
            image = Image.open(ASSETS / "textures/items" / filename).convert("RGBA")
            self.sprites[filename] = image

    def matrices(self, t):
        angles = pose(self.name, t)
        cache = {}
        def bone_matrix(name):
            if name in cache:
                return cache[name]
            bone = self.bones[name]
            parent = bone_matrix(bone["parent"]) if bone.get("parent") in self.bones else np.eye(4)
            pivot = np.array(bone.get("pivot", [0,0,0]), dtype=float)
            rot = np.array(bone.get("rotation", [0,0,0])) + angles.get(name, [0,0,0])
            cache[name] = parent @ translate(pivot) @ rotation(*rot) @ translate(-pivot)
            return cache[name]
        for name in self.bones:
            bone_matrix(name)
        return cache

    def batches(self, t):
        matrices = self.matrices(t)
        result = []
        for bones, dims, tex in self.layers:
            faces = []
            for bone in bones:
                if bone.get("neverRender"):
                    continue
                mat = matrices.get(bone["name"], np.eye(4))
                for cube in bone.get("cubes", []):
                    for pts, uv, normal in cube_faces(cube, dims, bone.get("inflate",0), bone.get("mirror",False)):
                        faces.append((transformed(pts, mat), uv, mat[:3,:3] @ normal))
            result.append((tex, faces))
        for bone, filename, hand, rot, grip, scale in self.hand_items:
            image = self.sprites[filename]
            pixels = np.asarray(image)
            matrix = matrices[bone] @ translate(hand) @ rotation(*rot)
            faces = []
            for py, px in zip(*np.nonzero(pixels[:,:,3] > 127)):
                cube = {"origin":[(px-grip[0])*scale,(grip[1]-py-1)*scale,-.4],
                        "size":[scale,scale,.8],"uv":{f:{"uv":[px+.08,py+.08],"uv_size":[.84,.84]} for f in ("north","south","east","west","up","down")}}
                for pts, uv, normal in cube_faces(cube, image.size):
                    faces.append((transformed(pts, matrix), uv, matrix[:3,:3] @ normal))
            result.append(("items/"+filename, faces))
        if self.name in ("drowned", "trident-zombie"):
            bones, dims = geometry("trident")
            matrix = matrices["rightArm"] @ translate([-6,12,-2]) @ rotation(0,-8,24) @ translate([0,-9,0])
            faces = []
            for bone in bones:
                for cube in bone.get("cubes", []):
                    for pts, uv, normal in cube_faces(cube, dims):
                        faces.append((transformed(pts, matrix),uv,matrix[:3,:3]@normal))
            result.append(("entity/trident.png", faces))
        if self.name == "snow-golem":
            cube = {"origin":[-4.5,20.5,-4.5],"size":[9,9,9],
                    "uv":{f:{"uv":[0,0],"uv_size":[16,16]} for f in ("north","south","east","west","up","down")}}
            for pts, uv, normal in cube_faces(cube, (16,16)):
                face = "top" if normal[1] else ("face_off" if normal[2]<0 else "side")
                mat = matrices["head"]
                result.append((f"blocks/pumpkin_{face}.png",[(transformed(pts,mat),uv,mat[:3,:3]@normal)]))
        return result


class Renderer:
    def __init__(self):
        self.ctx = moderngl.create_standalone_context(backend="egl")
        self.ctx.enable(moderngl.DEPTH_TEST)
        self.fbo = self.ctx.simple_framebuffer((SIZE*2,SIZE*2), components=4)
        self.fbo.use()
        self.program = self.ctx.program(vertex_shader='''#version 330
uniform mat4 projection; in vec3 pos; in vec2 uv; in vec3 normal;
out vec2 texture_uv; out vec3 surface_normal;
void main(){gl_Position=projection*vec4(pos,1.0);texture_uv=uv;surface_normal=normal;}
''', fragment_shader='''#version 330
uniform sampler2D image; in vec2 texture_uv; in vec3 surface_normal; out vec4 color;
void main(){vec4 c=texture(image,texture_uv);if(c.a<0.45)discard;
float light=.76+.29*max(dot(normalize(surface_normal),normalize(vec3(-.45,1.,-.8))),0.);
color=vec4(c.rgb*light,1.);}
''')
        self.program["image"].value = 0
        self.textures = {}
        eye = np.array([1.0,.68,-1.6]); direction = -eye/np.linalg.norm(eye)
        right = np.cross(direction, [0,1,0]); right /= np.linalg.norm(right)
        up = np.cross(right,direction)
        self.view = np.eye(4)
        self.view[:3,:3] = np.array([right,up,-direction])

    def framing(self, mob):
        points = []
        for t in np.linspace(0,2*math.pi,16,endpoint=False):
            for _, faces in mob.batches(t):
                points.extend(pts for pts,_,_ in faces)
        viewed = transformed(np.concatenate(points), self.view)
        lo, hi = viewed.min(axis=0), viewed.max(axis=0)
        center = (lo+hi)/2
        span = max(hi[0]-lo[0],hi[1]-lo[1]) * (SIZE/416)
        projection = np.diag([2/span,2/span,-.01,1.])
        projection[:2,3] = -center[:2]*2/span
        return projection @ self.view

    def render(self, mob, t, framing):
        self.fbo.clear(0,0,0,0,depth=1)
        self.program["projection"].write(framing.astype("f4").T.tobytes())
        for filename, faces in mob.batches(t):
            if filename not in self.textures:
                image = Image.open(ASSETS / "textures" / filename).convert("RGBA")
                texture = self.ctx.texture(image.size,4,image.tobytes())
                texture.filter = (moderngl.NEAREST,moderngl.NEAREST)
                texture.repeat_x = texture.repeat_y = False
                self.textures[filename] = texture
            self.textures[filename].use()
            vertices = []
            for points, uv, normal in faces:
                for idx in [0,1,2,0,2,3]:
                    vertices.extend([*points[idx],*uv[idx],*normal])
            buffer = self.ctx.buffer(np.array(vertices,dtype="f4").tobytes())
            vao = self.ctx.vertex_array(self.program,[(buffer,"3f 2f 3f","pos","uv","normal")])
            vao.render()
            vao.release(); buffer.release()
        image = Image.frombytes("RGBA",(SIZE*2,SIZE*2),self.fbo.read(components=4,alignment=1)).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
        return image.resize((SIZE,SIZE),Image.Resampling.LANCZOS)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ids", nargs="+", default=NAMES, choices=NAMES)
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--qa-dir", type=pathlib.Path, default=ROOT / "qa")
    args = parser.parse_args()
    args.qa_dir.mkdir(parents=True,exist_ok=True)
    OUTPUT.mkdir(parents=True,exist_ok=True)
    renderer = Renderer()
    manifest_path = OUTPUT / "rendered-manifest.json"
    metadata = {m["id"]:m for m in json.loads(manifest_path.read_text())} if manifest_path.exists() else {}
    for name in args.ids:
        mob = Mob(name)
        framing = renderer.framing(mob)
        first = renderer.render(mob,0,framing)
        first.save(args.qa_dir / f"{name}.png")
        if args.preview:
            print(f"Preview {name}",flush=True)
            continue
        frames = [first] + [renderer.render(mob,2*math.pi*i/FRAMES,framing) for i in range(1,FRAMES)]
        path = OUTPUT / f"{name}.webp"
        first.save(path,format="WEBP",save_all=True,append_images=frames[1:],duration=FRAME_MS,loop=0,quality=90,method=4,exact=True)
        metadata[name] = {"id":name,"file":path.name,"size":[SIZE,SIZE],"frames":len(frames),"durationMs":FRAMES*FRAME_MS,
                         "source":"Mojang Bedrock geometry/textures; TaskForge idle animation", "sha256":hashlib.sha256(path.read_bytes()).hexdigest()}
        print(f"Rendered {name}: {len(frames)} frames, {path.stat().st_size} bytes",flush=True)
    if metadata and not args.preview:
        manifest_path.write_text(json.dumps([metadata[name] for name in NAMES if name in metadata],ensure_ascii=False,indent=2))


if __name__ == "__main__":
    main()
