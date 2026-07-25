import { useEffect, useRef } from 'react';

function cssTriplet(name, fallback) {
  if (typeof window === 'undefined') return fallback;
  const value = getComputedStyle(document.documentElement)
    .getPropertyValue(name)
    .replace(/,/g, ' ')
    .trim();
  const parts = value
    .split(/\s+/)
    .map(Number)
    .filter(Number.isFinite);
  return parts.length >= 3 ? parts.slice(0, 3) : fallback;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function mixRgb(a, b, amount) {
  const t = clamp(amount, 0, 1);
  return [
    a[0] + (b[0] - a[0]) * t,
    a[1] + (b[1] - a[1]) * t,
    a[2] + (b[2] - a[2]) * t,
  ];
}

function lighten(rgb, amount) {
  return mixRgb(rgb, [255, 255, 255], amount);
}

function darken(rgb, amount) {
  return mixRgb(rgb, [0, 0, 0], amount);
}

function rgb01(rgb) {
  return rgb.map((value) => clamp(value / 255, 0, 1));
}

function hashString(text) {
  let hash = 2166136261;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return hash >>> 0;
}

function createRandom(seedValue) {
  let state = seedValue >>> 0;
  return () => {
    state += 0x6d2b79f5;
    let value = Math.imul(state ^ (state >>> 15), 1 | state);
    value ^= value + Math.imul(value ^ (value >>> 7), 61 | value);
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
  };
}

function makeThemePalette(isDark) {
  const fx1 = cssTriplet('--fx-1', [236, 72, 153]);
  const fx2 = cssTriplet('--fx-2', [168, 85, 247]);
  const fx3 = cssTriplet('--fx-3', [251, 113, 133]);
  const page = cssTriplet('--page-bg', isDark ? [24, 16, 38] : [250, 245, 255]);
  const card = cssTriplet('--card', isDark ? [28, 18, 46] : [253, 249, 255]);
  const fg = cssTriplet('--fg', isDark ? [237, 233, 254] : [148, 163, 184]);

  const bgA = isDark
    ? darken(mixRgb(page, fx1, 0.1), 0.2)
    : mixRgb(page, fx1, 0.075);
  const bgB = isDark
    ? darken(mixRgb(card, fx2, 0.13), 0.28)
    : mixRgb(card, fx2, 0.1);

  const planetPairs = [
    [lighten(fx1, 0.38), darken(fx2, isDark ? 0.18 : 0.08)],
    [lighten(fx2, 0.34), darken(fx3, isDark ? 0.2 : 0.1)],
    [lighten(fx3, 0.3), darken(fx1, isDark ? 0.25 : 0.12)],
    [lighten(mixRgb(fx1, fx2, 0.5), 0.42), darken(fx1, 0.22)],
    [lighten(mixRgb(fx2, fx3, 0.48), 0.4), darken(fx2, 0.24)],
    [lighten(mixRgb(fx3, fx1, 0.52), 0.36), darken(fx3, 0.22)],
  ];

  return {
    fx1,
    fx2,
    fx3,
    bgA,
    bgB,
    nebulaA: mixRgb(fx1, fx2, 0.36),
    nebulaB: mixRgb(fx2, fx3, 0.52),
    starA: lighten(fx2, 0.68),
    starB: lighten(fx3, 0.72),
    orbit: mixRgb(lighten(fg, isDark ? 0.15 : 0.02), fx2, isDark ? 0.28 : 0.42),
    sunA: lighten(mixRgb(fx2, fx3, 0.32), 0.72),
    sunB: mixRgb(fx1, fx3, 0.32),
    blackHole: darken(page, isDark ? 0.78 : 0.9),
    planetPairs,
    opacity: isDark ? 0.94 : 0.78,
  };
}

const VERTEX_SHADER = `#version 300 es
precision highp float;
const vec2 POSITIONS[3] = vec2[3](
  vec2(-1.0, -1.0),
  vec2( 3.0, -1.0),
  vec2(-1.0,  3.0)
);
out vec2 vUv;
void main() {
  vec2 p = POSITIONS[gl_VertexID];
  vUv = p * 0.5 + 0.5;
  gl_Position = vec4(p, 0.0, 1.0);
}`;

const FRAGMENT_SHADER = `#version 300 es
precision highp float;
precision highp int;

#define PLANET_COUNT 7
#define TAU 6.283185307179586

uniform vec2 uResolution;
uniform vec2 uCenter;
uniform float uTime;
uniform float uSeed;
uniform float uBaseRotation;
uniform float uTilt;
uniform float uOpacity;
uniform vec3 uBgA;
uniform vec3 uBgB;
uniform vec3 uNebulaA;
uniform vec3 uNebulaB;
uniform vec3 uStarA;
uniform vec3 uStarB;
uniform vec3 uOrbitColor;
uniform vec3 uSunA;
uniform vec3 uSunB;
uniform vec3 uBlackHole;
uniform vec4 uPlanetA[PLANET_COUNT];
uniform vec4 uPlanetB[PLANET_COUNT];
uniform vec4 uColorA[PLANET_COUNT];
uniform vec4 uColorB[PLANET_COUNT];

in vec2 vUv;
out vec4 outColor;

float saturate(float x) { return clamp(x, 0.0, 1.0); }

mat2 rot(float a) {
  float c = cos(a);
  float s = sin(a);
  return mat2(c, -s, s, c);
}

float hash11(float p) {
  p = fract(p * 0.1031);
  p *= p + 33.33;
  p *= p + p;
  return fract(p);
}

float hash21(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.xx + p3.yz) * p3.zy);
}

float noise2(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  float a = hash21(i);
  float b = hash21(i + vec2(1.0, 0.0));
  float c = hash21(i + vec2(0.0, 1.0));
  float d = hash21(i + vec2(1.0, 1.0));
  return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm3(vec2 p) {
  float value = 0.0;
  value += noise2(p) * 0.57;
  p = p * 2.03 + 17.1;
  value += noise2(p) * 0.28;
  p = p * 2.01 + 9.7;
  value += noise2(p) * 0.15;
  return value;
}

float starLayer(vec2 p, float scale, float threshold, float speed, float seedOffset) {
  vec2 q = p * scale;
  vec2 id = floor(q);
  vec2 cell = fract(q) - 0.5;
  float h = hash21(id + seedOffset + floor(uSeed * 37.0));
  vec2 jitter = (hash22(id + seedOffset + uSeed * 11.0) - 0.5) * 0.74;
  float dist = length(cell - jitter);
  float size = mix(0.008, 0.035, h * h);
  float star = smoothstep(size, 0.0, dist);
  star *= smoothstep(threshold, 1.0, h);
  float twinkle = 0.62 + 0.38 * sin(uTime * speed + h * 83.0);
  return star * twinkle;
}

float segmentDistance(vec2 p, vec2 a, vec2 b) {
  vec2 pa = p - a;
  vec2 ba = b - a;
  float h = clamp(dot(pa, ba) / max(dot(ba, ba), 0.0001), 0.0, 1.0);
  return length(pa - ba * h);
}

vec3 backgroundColor(vec2 screenP) {
  vec2 uv = screenP * 0.42;
  vec3 color = mix(uBgA, uBgB, saturate(vUv.x * 0.74 + vUv.y * 0.22));

  float cloudA = smoothstep(0.58, 0.90, fbm3(uv * 2.6 + vec2(uSeed * 0.22, -uSeed * 0.12)));
  float cloudB = smoothstep(0.64, 0.93, fbm3(rot(-0.46) * uv * 3.2 + vec2(-uSeed * 0.17, uSeed * 0.21)));
  color += uNebulaA * cloudA * 0.12;
  color += uNebulaB * cloudB * 0.10;

  vec2 drift = vec2(uTime * 0.0010, -uTime * 0.0006);
  float starsA = starLayer(screenP + drift, 23.0, 0.955, 0.9, 11.0);
  float starsB = starLayer(rot(0.31) * screenP - drift * 0.35, 44.0, 0.975, 1.4, 37.0) * 0.55;
  color += uStarA * starsA + uStarB * starsB;
  return color;
}

vec3 addComet(vec3 color, vec2 p, float index) {
  float cycle = 1.65 + index * 0.62;
  float seedValue = hash11(uSeed * 19.0 + index * 7.0);
  float local = mod(uTime + seedValue * cycle, cycle) / cycle;
  float visible = smoothstep(0.0, 0.018, local) * (1.0 - smoothstep(0.965, 1.0, local));
  if (visible <= 0.001) return color;

  float side = hash11(uSeed * 31.0 + index * 3.1) > 0.5 ? 1.0 : -1.0;
  vec2 direction = normalize(vec2(side, mix(-0.34, -0.08, hash11(uSeed + index * 17.0))));
  vec2 start = vec2(-side * 1.25, mix(0.06, 0.86, hash11(uSeed * 5.0 + index * 13.0)) - 0.5);
  vec2 head = start + direction * local * 3.28;
  vec2 tail = head - direction * mix(0.055, 0.095, hash11(uSeed * 9.0 + index));

  float d = segmentDistance(p, tail, head);
  float pixel = 1.0 / min(uResolution.x, uResolution.y);
  float aa = max(fwidth(d), pixel * 0.06);
  float core = 1.0 - smoothstep(pixel * 0.18 - aa, pixel * 0.34 + aa, d);
  vec3 laser = mix(uStarA, uStarB, 0.48 + index * 0.22);
  laser = mix(laser, vec3(1.0), core * 0.74);
  return color + laser * core * visible * 4.1;
}

float ellipseLine(vec2 p, vec2 radii, float width) {
  vec2 n = p / radii;
  float d = abs(length(n) - 1.0) * min(radii.x, radii.y);
  return smoothstep(width, 0.0, d);
}

vec3 sunColor(vec2 local, float radius) {
  float d = length(local);
  vec2 n2 = local / radius;
  float nz = sqrt(max(0.0, 1.0 - dot(n2, n2)));
  vec3 n = normalize(vec3(n2, nz));
  vec3 lightDir = normalize(vec3(-0.55, 0.62, 0.85));
  float diffuse = 0.64 + 0.36 * max(dot(n, lightDir), 0.0);
  float plasma = fbm3(local * 135.0 + vec2(uTime * 0.028, -uTime * 0.021) + uSeed * 0.7);
  float bands = 0.5 + 0.5 * sin(local.y * 125.0 + plasma * 4.0 + uTime * 0.22);
  vec3 body = mix(uSunB, uSunA, saturate(0.22 + diffuse * 0.62 + plasma * 0.14 + bands * 0.08));
  return body * (0.88 + 0.12 * nz);
}

vec3 planetSurface(int type, vec2 local, float radius, vec3 colorA, vec3 colorB, float seedValue) {
  vec2 q = local / radius;
  float rr = dot(q, q);
  float z = sqrt(max(0.0, 1.0 - rr));
  vec3 n = normalize(vec3(q, z));
  vec3 lightDir = normalize(vec3(-0.64, 0.58, 0.82));
  float diffuse = 0.30 + 0.70 * max(dot(n, lightDir), 0.0);
  float rim = pow(1.0 - z, 2.0);
  float vertical = 0.5 + 0.5 * q.y;
  vec3 color;

  if (type == 4) {
    float islands = smoothstep(0.50, 0.69, noise2(q * 4.7 + vec2(seedValue * 0.7, -seedValue * 0.4)));
    vec3 low = mix(colorB, colorA, 0.28 + vertical * 0.20);
    vec3 high = mix(colorA, uStarA, 0.25 + vertical * 0.12);
    color = mix(low, high, islands * 0.9);
    float cloud = smoothstep(0.80, 0.92, noise2(rot(0.7) * q * 7.2 + vec2(seedValue * 1.2, seedValue * 0.8)));
    color = mix(color, uStarB, cloud * 0.12);
  } else if (type == 1) {
    float bands = 0.5 + 0.5 * sin(q.y * 7.5 + seedValue * 5.0);
    color = mix(colorB, colorA, 0.34 + bands * 0.28);
  } else if (type == 9) {
    color = mix(colorB, colorA, 0.46 + vertical * 0.16);
    color *= 0.92 + diffuse * 0.22;
    color += colorA * (0.30 + rim * 0.50);
  } else {
    color = mix(colorB, colorA, 0.40 + vertical * 0.18);
    color *= 0.82 + diffuse * 0.32;
  }

  color += colorA * rim * (type == 9 ? 0.20 : 0.05);
  return color;
}

void main() {
  float minSide = min(uResolution.x, uResolution.y);
  vec2 screenP = (gl_FragCoord.xy - 0.5 * uResolution) / minSide;
  vec2 p = screenP - uCenter;

  vec3 color = backgroundColor(screenP);
  color = addComet(color, screenP, 0.0);
  color = addComet(color, screenP, 1.0);

  float systemRotation = uBaseRotation + sin(uTime * 0.0105 + uSeed) * radians(25.0);
  mat2 systemRot = rot(systemRotation);
  vec2 orbitP = rot(-systemRotation) * p;

  float sunRadius = 0.041;
  float sunDist = length(p);
  float sunHalo = exp(-sunDist * sunDist * 78.0) * 0.60;
  float corona = exp(-sunDist * sunDist * 250.0) * 0.52;
  color += uSunB * sunHalo;
  color += uSunA * corona;

  for (int i = 0; i < PLANET_COUNT; i++) {
    float orbit = 0.105 + float(i) * 0.054;
    float line = ellipseLine(orbitP, vec2(orbit, orbit * uTilt), 0.00068);
    float shimmer = 0.68 + 0.32 * sin(uTime * 0.34 + float(i) * 1.7 + uSeed);
    color += uOrbitColor * line * 0.10 * shimmer;
  }

  float objectDepth = -2.0;
  vec3 objectColor = vec3(0.0);
  float objectMask = 0.0;

  if (sunDist < sunRadius) {
    objectDepth = 0.50;
    objectColor = sunColor(p, sunRadius);
    objectMask = smoothstep(sunRadius, sunRadius - 0.0025, sunDist);
  }

  for (int i = 0; i < PLANET_COUNT; i++) {
    vec4 a = uPlanetA[i];
    vec4 b = uPlanetB[i];
    float angle = a.y + uTime * a.z;
    vec2 orbitPos = vec2(cos(angle) * a.x, sin(angle) * a.x * uTilt);
    vec2 planetPos = systemRot * orbitPos;
    float depth = 0.5 + 0.5 * sin(angle);
    float size = a.w * mix(0.86, 1.12, depth);
    vec2 local = p - planetPos;
    float d = length(local);

    if (b.y > 0.5) {
      vec2 ringLocal = rot(-0.20 - systemRotation * 0.08) * local;
      float frontHalf = smoothstep(-size * 0.24, size * 0.26, ringLocal.y);
      float ringVisible = depth > objectDepth ? 1.0 : (1.0 - objectMask);
      vec3 ringColor = mix(uColorA[i].rgb, uStarA, 0.62);
      float ring1 = ellipseLine(ringLocal, vec2(size * 1.95, size * 0.60), max(0.00042, size * 0.055));
      float ring2 = ellipseLine(ringLocal, vec2(size * 2.18, size * 0.68), max(0.00040, size * 0.050));
      float ring3 = ellipseLine(ringLocal, vec2(size * 2.42, size * 0.76), max(0.00036, size * 0.042));
      float ring = ring1 * 0.40 + ring2 * 0.30 + ring3 * 0.20;
      color += ringColor * ring * 0.40 * ringVisible * mix(0.58, 1.0, frontHalf);
    }

    int type = int(b.x + 0.5);
    if (type == 8) {
      float halo = exp(-d * d / max(size * size * 2.9, 0.00001));
      float rimGlow = smoothstep(size * 1.16, size * 0.92, d) - smoothstep(size * 0.94, size * 0.82, d);
      float disk = ellipseLine(rot(-0.18) * local, vec2(size * 2.05, size * 0.62), max(0.00034, size * 0.075));
      color += uColorA[i].rgb * disk * 0.34;
      color += mix(uColorB[i].rgb, uStarA, 0.5) * rimGlow * 0.38;
      color += uColorB[i].rgb * halo * 0.055;
      float core = smoothstep(size * 0.98, size * 0.86, d);
      if (core > 0.0 && depth > objectDepth) {
        objectDepth = depth;
        objectColor = uBlackHole;
        objectMask = core;
      }
    } else {
      if (type == 9) {
        float glow = exp(-d * d / max(size * size * 3.4, 0.00001));
        color += uColorA[i].rgb * glow * 0.34;
      }
      if (d < size && depth > objectDepth) {
        objectDepth = depth;
        objectColor = planetSurface(type, local, size, uColorA[i].rgb, uColorB[i].rgb, b.z);
        objectMask = smoothstep(size, size - max(0.0009, size * 0.12), d);
      }
    }
  }

  color = mix(color, objectColor, objectMask);
  float vignette = smoothstep(1.08, 0.18, length(screenP * vec2(0.80, 1.0)));
  color *= mix(0.64, 1.0, vignette);
  float grain = hash21(gl_FragCoord.xy + floor(uTime * 23.0)) - 0.5;
  color += grain * 0.005;
  color = pow(max(color, 0.0), vec3(0.94));
  outColor = vec4(color, uOpacity);
}`;

export default function BgFxSolarWebgl({ enabled, intensity = 1, uiRev = 0 }) {
  const canvasRef = useRef(null);

  useEffect(() => {
    if (!enabled) return undefined;

    const canvas = canvasRef.current;
    if (!canvas) return undefined;

    const gl = canvas.getContext('webgl2', {
      alpha: true,
      antialias: false,
      depth: false,
      stencil: false,
      premultipliedAlpha: false,
      preserveDrawingBuffer: false,
      desynchronized: true,
      powerPreference: 'high-performance',
    });

    if (!gl) return undefined;

    let disposed = false;
    let rafId = 0;
    let resizeObserver = null;
    let lastFrame = 0;
    let startTime = performance.now() / 1000;
    let renderScale = 0.72;
    let samples = [];
    let adaptiveCooldown = 0;

    const isDark = document.documentElement.classList.contains('dark');
    const theme = makeThemePalette(isDark);
    const cores = navigator.hardwareConcurrency || 4;
    const memory = navigator.deviceMemory || 4;
    if (cores <= 4 || memory <= 4) renderScale *= 0.82;
    renderScale = clamp(renderScale, 0.52, 0.86);

    function compileShader(type, source) {
      const shader = gl.createShader(type);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        const message = gl.getShaderInfoLog(shader) || 'Shader compile error';
        gl.deleteShader(shader);
        throw new Error(message);
      }
      return shader;
    }

    function createProgram() {
      const vertexShader = compileShader(gl.VERTEX_SHADER, VERTEX_SHADER);
      const fragmentShader = compileShader(gl.FRAGMENT_SHADER, FRAGMENT_SHADER);
      const nextProgram = gl.createProgram();
      gl.attachShader(nextProgram, vertexShader);
      gl.attachShader(nextProgram, fragmentShader);
      gl.linkProgram(nextProgram);
      gl.deleteShader(vertexShader);
      gl.deleteShader(fragmentShader);
      if (!gl.getProgramParameter(nextProgram, gl.LINK_STATUS)) {
        const message = gl.getProgramInfoLog(nextProgram) || 'Program link error';
        gl.deleteProgram(nextProgram);
        throw new Error(message);
      }
      return nextProgram;
    }

    let program;
    try {
      program = createProgram();
    } catch (error) {
      console.error('[BgFxSolarWebgl]', error);
      return undefined;
    }

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    gl.useProgram(program);
    gl.disable(gl.BLEND);
    gl.disable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);

    const uniform = (name) => gl.getUniformLocation(program, name);
    const locations = {
      resolution: uniform('uResolution'),
      center: uniform('uCenter'),
      time: uniform('uTime'),
      seed: uniform('uSeed'),
      baseRotation: uniform('uBaseRotation'),
      tilt: uniform('uTilt'),
      opacity: uniform('uOpacity'),
      bgA: uniform('uBgA'),
      bgB: uniform('uBgB'),
      nebulaA: uniform('uNebulaA'),
      nebulaB: uniform('uNebulaB'),
      starA: uniform('uStarA'),
      starB: uniform('uStarB'),
      orbitColor: uniform('uOrbitColor'),
      sunA: uniform('uSunA'),
      sunB: uniform('uSunB'),
      blackHole: uniform('uBlackHole'),
      planetA: uniform('uPlanetA[0]'),
      planetB: uniform('uPlanetB[0]'),
      colorA: uniform('uColorA[0]'),
      colorB: uniform('uColorB[0]'),
    };

    let storedSeed = '';
    try {
      storedSeed = sessionStorage.getItem('tf-solar-system-seed') || '';
      if (!storedSeed) {
        storedSeed = window.crypto?.randomUUID?.() || `${Date.now()}-${Math.random()}`;
        sessionStorage.setItem('tf-solar-system-seed', storedSeed);
      }
    } catch {
      storedSeed = `${Date.now()}-${Math.random()}`;
    }

    const baseSeed = hashString(storedSeed);
    const random = createRandom(baseSeed);
    const planetA = new Float32Array(7 * 4);
    const planetB = new Float32Array(7 * 4);
    const colorA = new Float32Array(7 * 4);
    const colorB = new Float32Array(7 * 4);
    const typePool = [0, 0, 1, 1, 4, 8, 9, 9];
    const planetTypes = [4, 1, 8, 9, 0];

    while (planetTypes.length < 7) {
      planetTypes.push(typePool[Math.floor(random() * typePool.length)]);
    }
    for (let i = planetTypes.length - 1; i > 0; i -= 1) {
      const j = Math.floor(random() * (i + 1));
      [planetTypes[i], planetTypes[j]] = [planetTypes[j], planetTypes[i]];
    }

    for (let i = 0; i < 7; i += 1) {
      const type = planetTypes[i];
      const pair = theme.planetPairs[(i + Math.floor(random() * 3)) % theme.planetPairs.length];
      const orbit = 0.105 + (i + 0.68) * 0.054 + random() * 0.005;
      const phase = random() * Math.PI * 2;
      const direction = random() > 0.5 ? 1 : -1;
      const speed = direction * (0.012 + i * 0.0032 + random() * 0.006);
      const typeScale = type === 1 ? 1.24 : type === 8 ? 1.1 : type === 9 ? 1.04 : 1;
      const size = (0.0065 + random() * 0.0048) * 1.5 * typeScale;
      const hasRing = type === 1 || (type === 0 && random() < 0.12);

      planetA.set([orbit, phase, speed, size], i * 4);
      planetB.set([type, hasRing ? 1 : 0, random() * 100, random()], i * 4);

      const primary = rgb01(pair[0]);
      const secondary = rgb01(pair[1]);
      colorA.set([primary[0], primary[1], primary[2], 1], i * 4);
      colorB.set([secondary[0], secondary[1], secondary[2], 1], i * 4);
    }

    const baseRotation = ((-8 + (random() - 0.5) * 4) * Math.PI) / 180;
    const tilt = 0.3 + random() * 0.025;
    const center = new Float32Array([
      (random() - 0.5) * 0.012,
      0.045 + (random() - 0.5) * 0.014,
    ]);

    const setVec3 = (location, rgb) => gl.uniform3fv(location, new Float32Array(rgb01(rgb)));
    gl.uniform1f(locations.seed, (baseSeed % 100000) / 100000);
    gl.uniform1f(locations.baseRotation, baseRotation);
    gl.uniform1f(locations.tilt, tilt);
    gl.uniform1f(locations.opacity, clamp(theme.opacity * (Number(intensity) || 1), 0.35, 1));
    gl.uniform2fv(locations.center, center);
    setVec3(locations.bgA, theme.bgA);
    setVec3(locations.bgB, theme.bgB);
    setVec3(locations.nebulaA, theme.nebulaA);
    setVec3(locations.nebulaB, theme.nebulaB);
    setVec3(locations.starA, theme.starA);
    setVec3(locations.starB, theme.starB);
    setVec3(locations.orbitColor, theme.orbit);
    setVec3(locations.sunA, theme.sunA);
    setVec3(locations.sunB, theme.sunB);
    setVec3(locations.blackHole, theme.blackHole);
    gl.uniform4fv(locations.planetA, planetA);
    gl.uniform4fv(locations.planetB, planetB);
    gl.uniform4fv(locations.colorA, colorA);
    gl.uniform4fv(locations.colorB, colorB);

    function resize() {
      if (disposed) return;
      const rect = canvas.getBoundingClientRect();
      const widthCss = Math.max(1, rect.width || window.innerWidth);
      const heightCss = Math.max(1, rect.height || window.innerHeight);
      const dpr = Math.min(window.devicePixelRatio || 1, 1.1);
      const width = Math.max(1, Math.round(widthCss * dpr * renderScale));
      const height = Math.max(1, Math.round(heightCss * dpr * renderScale));
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
        gl.viewport(0, 0, width, height);
        gl.uniform2f(locations.resolution, width, height);
      }
    }

    function render(timeSeconds) {
      gl.uniform1f(locations.time, timeSeconds);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    function adaptQuality(delta) {
      samples.push(delta);
      if (samples.length < 72) return;
      const average = samples.reduce((sum, value) => sum + value, 0) / samples.length;
      samples = [];
      if (adaptiveCooldown > 0) {
        adaptiveCooldown -= 1;
        return;
      }
      if (average > 0.044 && renderScale > 0.54) {
        renderScale = Math.max(0.52, renderScale - 0.07);
        adaptiveCooldown = 2;
        resize();
      } else if (average < 0.034 && renderScale < 0.84) {
        renderScale = Math.min(0.84, renderScale + 0.03);
        adaptiveCooldown = 3;
        resize();
      }
    }

    function loop(now) {
      if (disposed) return;
      rafId = requestAnimationFrame(loop);
      if (document.hidden) return;
      const seconds = now / 1000;
      if (lastFrame === 0) {
        lastFrame = seconds;
        render(seconds - startTime);
        return;
      }
      const delta = seconds - lastFrame;
      if (delta < 1 / 30) return;
      lastFrame = seconds - (delta % (1 / 30));
      adaptQuality(delta);
      render(seconds - startTime);
    }

    const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
    const handleVisibility = () => {
      if (!document.hidden) {
        lastFrame = 0;
        startTime = performance.now() / 1000;
      }
    };

    resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(canvas);
    document.addEventListener('visibilitychange', handleVisibility);
    resize();

    if (reducedMotion) render(0);
    else rafId = requestAnimationFrame(loop);

    return () => {
      disposed = true;
      cancelAnimationFrame(rafId);
      resizeObserver?.disconnect();
      document.removeEventListener('visibilitychange', handleVisibility);
      gl.bindVertexArray(null);
      if (vao) gl.deleteVertexArray(vao);
      gl.deleteProgram(program);
    };
  }, [enabled, intensity, uiRev]);

  if (!enabled) return null;

  return (
    <canvas
      ref={canvasRef}
      aria-hidden
      className="bgfx-canvas bgfx-canvas--solar"
      style={{
        position: 'fixed',
        inset: 0,
        width: '100%',
        height: '100%',
        zIndex: 0,
        pointerEvents: 'none',
      }}
    />
  );
}
