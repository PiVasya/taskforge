# Minecraft news media

The two Minecraft news documents reference the same fifteen local animated WebP
files under `apps/web/public/updates/media/mobs/animated/`.

Every canvas is 512×512 with transparency. The renderer uses a fixed orthographic
camera and textured game geometry. Weapons follow the animated arm transforms.
Snow golem, wither skeleton, drowned, stray, parched, illusioner and trident zombie
use authored periodic idle poses; the other eight preserve sourced animation.
The renderer does not rotate the camera or animate a static illustration.

`MobCard` keeps one image URL for the card lifetime, including outside the
viewport and while the document is hidden. Native lazy loading defers downloads
without swapping media. An image error does not substitute another illustration.

## Reproduce

Use an isolated Python environment with `tools/minecraft-mob-renderer/requirements.txt`.
OpenGL 3.3 via EGL is required for offline rendering; there is no production
Python/OpenGL dependency in the frontend.

```sh
python tools/minecraft-mob-renderer/fetch_sources.py
python tools/minecraft-mob-renderer/render.py
python tools/minecraft-mob-renderer/normalize.py
python tools/minecraft-mob-renderer/verify.py
```

Geometry/textures are pinned to a Mojang Bedrock Samples revision in `sources.json`.
The Java-only illusioner uses the Minecraft Wiki texture and an illager rig with
the appropriate cap. Original loops have source URLs and SHA-256 values in
`loop-sources.json`; a changed source fails validation instead of being accepted
silently. Source textures/models stay under `tools/`, not in the web bundle.

Framing is constant for the whole cycle and accounts for all animated bounds.
Weapons and clothing are included in the same depth-tested render. Existing loops
are cropped using the union of all frame bounds, never independently per frame.
Native pixel textures are resized without smoothing.

`verify.py` decodes every deployed frame, validates animation, transparency,
512px size, nonempty content and clear edge margins, checks the seam of generated
loops, and writes the final SHA-256 manifest. The news Node suite additionally
checks the real WebP container; React tests cover stable URLs and error handling.

```sh
cd apps/web
node --test scripts/updates-tests/*.test.mjs
CI=true npm test -- --watchAll=false --runInBand --runTestsByPath src/components/ChangelogShowcase.test.jsx
```

Models, textures and Minecraft characters belong to Mojang/Microsoft. Wikimedia
file-page URLs are retained for source attribution; this is not a claim of new
ownership or a blanket open-content license over the original game assets.
