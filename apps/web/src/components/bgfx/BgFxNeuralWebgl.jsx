import { useEffect, useRef } from 'react';

function cssVar(name, fallback) {
  if (typeof window === 'undefined') return fallback;
  const v = getComputedStyle(document.documentElement).getPropertyValue(name);
  return (v || fallback || '').trim();
}

function parseRgbTriplet(s, fallback) {
  const clean = (s || '').replace(/,/g, ' ').trim();
  const parts = clean.split(/\s+/).map(Number).filter(Number.isFinite);
  return parts.length >= 3 ? [parts[0], parts[1], parts[2]] : fallback;
}

function clamp(n, a, b) {
  return Math.min(b, Math.max(a, n));
}

function rand(min, max) {
  return min + Math.random() * (max - min);
}

export default function BgFxNeuralWebgl({ enabled, intensity = 1, paletteKey = 'default' }) {
  const canvasRef = useRef(null);

  useEffect(() => {
    if (!enabled) return undefined;

    const canvas = canvasRef.current;
    if (!canvas) return undefined;

    const gl = canvas.getContext('webgl', {
      alpha: true,
      antialias: true,
      depth: false,
      stencil: false,
      premultipliedAlpha: false,
      preserveDrawingBuffer: false,
    });

    if (!gl) return undefined;

    let disposed = false;
    let raf = 0;
    let resizeObserver = null;

    const isDarkTheme = () => document.documentElement.classList.contains('dark');
    const alphaBoost = () => (isDarkTheme() ? 1.0 : 1.8);
    const rgb01 = (rgb) => [rgb[0] / 255, rgb[1] / 255, rgb[2] / 255];

    const fx1 = parseRgbTriplet(cssVar('--fx-1', '245 0 128'), [245, 0, 128]);
    const fx2 = parseRgbTriplet(cssVar('--fx-2', '14 165 233'), [14, 165, 233]);
    const fx3 = parseRgbTriplet(cssVar('--fx-3', '34 197 94'), [34, 197, 94]);
    const fxIntensity = clamp(Number(intensity) || 1, 0.25, 2);

    let w = 1;
    let h = 1;
    let dpr = 1;
    let last = performance.now();

    const nodes = [];
    const pulses = [];
    let pulseTimer = 0;

    const MODE_NORMAL = 0;
    const MODE_GATHER = 1;
    const MODE_OVERHEAT = 2;
    const MODE_EXPLODE = 3;

    const GATHER_TO_OVERHEAT = 2.25;
    const OVERHEAT_TIME = 1.35;
    const EXPLODE_TIME = 0.85;
    const REGATHER_COOLDOWN = 0.55;

    let mode = MODE_NORMAL;
    let gatherTimer = 0;
    let overheatTimer = 0;
    let explodeTimer = 0;
    let regatherCooldown = 0;
    let needsRelease = false;

    let coreX = 0;
    let coreY = 0;
    let coreHeat = 0;
    let corePulse = 0;

    let flashX = 0;
    let flashY = 0;
    let flashPower = 0;
    let shockwaveRadius = 0;
    let shockwavePower = 0;

    const pointer = {
      x: 0,
      y: 0,
      vx: 0,
      vy: 0,
      down: false,
      rdown: false,
      has: false,
    };

    rand(0, 9999);
    rand(0, 9999);
    rand(-0.00008, 0.00008);
    rand(-0.00008, 0.00008);
    rand(6, 14);
    for (let i = 0; i < 240 * 240; i += 1) {
      rand(0, 45);
      rand(6, 24);
    }

    window.__neuralDebug = {
      nodes: 0,
      edges: 0,
      pulses: 0,
      mode: 'normal',
      heat: 0,
      flash: 0,
      renderer: 'webgl',
      paletteKey,
    };

    function makeShader(type, source) {
      const shader = gl.createShader(type);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        const msg = gl.getShaderInfoLog(shader) || 'Shader compile error';
        gl.deleteShader(shader);
        throw new Error(msg);
      }
      return shader;
    }

    function makeProgram(vsSource, fsSource) {
      const vs = makeShader(gl.VERTEX_SHADER, vsSource);
      const fs = makeShader(gl.FRAGMENT_SHADER, fsSource);
      const program = gl.createProgram();
      gl.attachShader(program, vs);
      gl.attachShader(program, fs);
      gl.linkProgram(program);
      gl.deleteShader(vs);
      gl.deleteShader(fs);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        const msg = gl.getProgramInfoLog(program) || 'Program link error';
        gl.deleteProgram(program);
        throw new Error(msg);
      }
      return program;
    }

    const bgProgram = makeProgram(`
      attribute vec2 a_pos;
      varying vec2 v_uv;
      void main() {
        v_uv = a_pos * 0.5 + 0.5;
        gl_Position = vec4(a_pos, 0.0, 1.0);
      }
    `, `
      precision mediump float;
      varying vec2 v_uv;
      uniform vec2 u_res;
      uniform vec3 u_fx1;
      uniform vec3 u_fx2;
      uniform float u_dark;

      void main() {
        vec2 p = v_uv * u_res;
        vec2 c = vec2(u_res.x * 0.5, u_res.y * 0.55);
        float k = distance(p, c) / (max(u_res.x, u_res.y) * 0.75);

        float a0 = mix(0.05, 0.08, u_dark);
        float a35 = mix(0.025, 0.04, u_dark);
        vec3 col;
        float a;

        if (k <= 0.35) {
          float m = clamp(k / 0.35, 0.0, 1.0);
          col = mix(u_fx1, u_fx2, m);
          a = mix(a0, a35, m);
        } else {
          float m = clamp((k - 0.35) / 0.65, 0.0, 1.0);
          col = mix(u_fx2, vec3(0.0), m);
          a = mix(a35, 0.0, m);
        }

        if (k >= 1.0) a = 0.0;
        gl_FragColor = vec4(col, a);
      }
    `);

    const lineProgram = makeProgram(`
      attribute vec2 a_pos;
      attribute vec3 a_color;
      attribute float a_alpha;
      uniform vec2 u_res;
      varying vec3 v_color;
      varying float v_alpha;
      void main() {
        vec2 clip = (a_pos / u_res) * 2.0 - 1.0;
        gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
        v_color = a_color;
        v_alpha = a_alpha;
      }
    `, `
      precision mediump float;
      varying vec3 v_color;
      varying float v_alpha;
      void main() {
        gl_FragColor = vec4(v_color, v_alpha);
      }
    `);

    const circleProgram = makeProgram(`
      attribute vec2 a_pos;
      attribute vec2 a_local;
      attribute vec3 a_color;
      attribute float a_alpha;
      attribute float a_mode;
      uniform vec2 u_res;
      varying vec2 v_local;
      varying vec3 v_color;
      varying float v_alpha;
      varying float v_mode;
      void main() {
        vec2 clip = (a_pos / u_res) * 2.0 - 1.0;
        gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
        v_local = a_local;
        v_color = a_color;
        v_alpha = a_alpha;
        v_mode = a_mode;
      }
    `, `
      precision mediump float;
      varying vec2 v_local;
      varying vec3 v_color;
      varying float v_alpha;
      varying float v_mode;
      void main() {
        float d = length(v_local);
        if (d > 1.0) discard;

        vec3 color = v_color;
        float alpha = v_alpha;

        if (v_mode < 0.5) {
          vec3 midColor = min(vec3(1.0), v_color + vec3(25.0 / 255.0));
          if (d <= 0.25) {
            float m = d / 0.25;
            color = mix(v_color, midColor, m);
            alpha = mix(v_alpha, v_alpha * 0.50, m);
          } else {
            float m = (d - 0.25) / 0.75;
            color = mix(midColor, vec3(0.0), m);
            alpha = mix(v_alpha * 0.50, 0.0, m);
          }
        } else {
          alpha = v_alpha * (1.0 - smoothstep(0.88, 1.0, d));
        }

        gl_FragColor = vec4(color, alpha);
      }
    `);

    const bgBuffer = gl.createBuffer();
    const lineBuffer = gl.createBuffer();
    const circleBuffer = gl.createBuffer();

    gl.bindBuffer(gl.ARRAY_BUFFER, bgBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([
      -1, -1, 1, -1, -1, 1,
      -1, 1, 1, -1, 1, 1,
    ]), gl.STATIC_DRAW);

    function setPointer(px, py) {
      pointer.has = true;
      const nx = px * dpr;
      const ny = py * dpr;
      pointer.vx = nx - pointer.x;
      pointer.vy = ny - pointer.y;
      pointer.x = nx;
      pointer.y = ny;
    }

    const handleMouseMove = (e) => setPointer(e.clientX, e.clientY);
    const handleMouseDown = (e) => { if (e?.button === 2) pointer.rdown = true; else pointer.down = true; };
    const handleMouseUp = (e) => { if (e?.button === 2) pointer.rdown = false; else pointer.down = false; };
    const handleTouchStart = (e) => {
      pointer.down = true;
      pointer.rdown = !!(e.touches && e.touches.length >= 2);
      if (e.touches && e.touches[0]) setPointer(e.touches[0].clientX, e.touches[0].clientY);
    };
    const handleTouchMove = (e) => {
      pointer.rdown = !!(e.touches && e.touches.length >= 2);
      if (e.touches && e.touches[0]) setPointer(e.touches[0].clientX, e.touches[0].clientY);
    };
    const handleTouchEnd = () => { pointer.down = false; pointer.rdown = false; };

    window.addEventListener('mousemove', handleMouseMove, { passive: true });
    window.addEventListener('mousedown', handleMouseDown, { passive: true });
    window.addEventListener('mouseup', handleMouseUp, { passive: true });
    window.addEventListener('touchstart', handleTouchStart, { passive: true });
    window.addEventListener('touchmove', handleTouchMove, { passive: true });
    window.addEventListener('touchend', handleTouchEnd, { passive: true });

    function resize() {
      const rect = canvas.getBoundingClientRect();
      w = Math.max(1, Math.floor(rect.width));
      h = Math.max(1, Math.floor(rect.height));
      dpr = clamp(window.devicePixelRatio || 1, 1, 2);

      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      gl.viewport(0, 0, canvas.width, canvas.height);

      nodes.length = 0;
      pulses.length = 0;
      pulseTimer = 0;
      mode = MODE_NORMAL;
      gatherTimer = 0;
      overheatTimer = 0;
      explodeTimer = 0;
      regatherCooldown = 0;
      needsRelease = false;
      coreHeat = 0;
      flashPower = 0;
      shockwavePower = 0;

      const area = w * h;
      const nodeCount = clamp(Math.floor((area / 18000) * fxIntensity), 50, 140);

      for (let i = 0; i < nodeCount; i += 1) {
        const s = rand(0.35, 1.25);
        const hueIdx = i % 3;
        nodes.push({
          x: rand(0, w),
          y: rand(0, h),
          vx: rand(-0.35, 0.35),
          vy: rand(-0.35, 0.35),
          r: rand(1.2, 2.9) * s,
          c: hueIdx === 0 ? fx1 : hueIdx === 1 ? fx2 : fx3,
          core: rand(0.65, 1.0),
          wob: rand(0, Math.PI * 2),
          wobSp: rand(0.002, 0.01),
          mass: rand(0.5, 1.6) * (1 / s),
        });
      }
    }

    function addLine(out, x1, y1, x2, y2, width, color, alpha) {
      if (alpha <= 0.001 || width <= 0.001) return;
      const dx = x2 - x1;
      const dy = y2 - y1;
      const len = Math.sqrt(dx * dx + dy * dy);
      if (len < 0.001) return;

      const nx = (-dy / len) * width * 0.5;
      const ny = (dx / len) * width * 0.5;
      const r = color[0] / 255;
      const g = color[1] / 255;
      const b = color[2] / 255;

      const ax = x1 + nx;
      const ay = y1 + ny;
      const bx = x1 - nx;
      const by = y1 - ny;
      const cx = x2 + nx;
      const cy = y2 + ny;
      const dx2 = x2 - nx;
      const dy2 = y2 - ny;

      out.push(
        ax, ay, r, g, b, alpha,
        bx, by, r, g, b, alpha,
        cx, cy, r, g, b, alpha,
        cx, cy, r, g, b, alpha,
        bx, by, r, g, b, alpha,
        dx2, dy2, r, g, b, alpha,
      );
    }

    function addCircle(out, x, y, radius, color, alpha, circleMode) {
      if (alpha <= 0.001 || radius <= 0.001) return;
      const r = color[0] / 255;
      const g = color[1] / 255;
      const b = color[2] / 255;
      const x0 = x - radius;
      const y0 = y - radius;
      const x1 = x + radius;
      const y1 = y + radius;

      out.push(
        x0, y0, -1, -1, r, g, b, alpha, circleMode,
        x1, y0, 1, -1, r, g, b, alpha, circleMode,
        x0, y1, -1, 1, r, g, b, alpha, circleMode,
        x0, y1, -1, 1, r, g, b, alpha, circleMode,
        x1, y0, 1, -1, r, g, b, alpha, circleMode,
        x1, y1, 1, 1, r, g, b, alpha, circleMode,
      );
    }

    function drawBackground() {
      gl.useProgram(bgProgram);
      gl.bindBuffer(gl.ARRAY_BUFFER, bgBuffer);
      const pos = gl.getAttribLocation(bgProgram, 'a_pos');
      gl.enableVertexAttribArray(pos);
      gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, 2 * 4, 0);
      gl.uniform2f(gl.getUniformLocation(bgProgram, 'u_res'), w, h);
      gl.uniform3fv(gl.getUniformLocation(bgProgram, 'u_fx1'), rgb01(fx1));
      gl.uniform3fv(gl.getUniformLocation(bgProgram, 'u_fx2'), rgb01(fx2));
      gl.uniform1f(gl.getUniformLocation(bgProgram, 'u_dark'), isDarkTheme() ? 1 : 0);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
    }

    function drawLines(data) {
      if (!data.length) return;
      gl.useProgram(lineProgram);
      gl.bindBuffer(gl.ARRAY_BUFFER, lineBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), gl.DYNAMIC_DRAW);
      const stride = 6 * 4;
      const pos = gl.getAttribLocation(lineProgram, 'a_pos');
      const color = gl.getAttribLocation(lineProgram, 'a_color');
      const alpha = gl.getAttribLocation(lineProgram, 'a_alpha');
      gl.enableVertexAttribArray(pos);
      gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, stride, 0);
      gl.enableVertexAttribArray(color);
      gl.vertexAttribPointer(color, 3, gl.FLOAT, false, stride, 2 * 4);
      gl.enableVertexAttribArray(alpha);
      gl.vertexAttribPointer(alpha, 1, gl.FLOAT, false, stride, 5 * 4);
      gl.uniform2f(gl.getUniformLocation(lineProgram, 'u_res'), w, h);
      gl.drawArrays(gl.TRIANGLES, 0, data.length / 6);
    }

    function drawCircles(data) {
      if (!data.length) return;
      gl.useProgram(circleProgram);
      gl.bindBuffer(gl.ARRAY_BUFFER, circleBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), gl.DYNAMIC_DRAW);
      const stride = 9 * 4;
      const pos = gl.getAttribLocation(circleProgram, 'a_pos');
      const local = gl.getAttribLocation(circleProgram, 'a_local');
      const color = gl.getAttribLocation(circleProgram, 'a_color');
      const alpha = gl.getAttribLocation(circleProgram, 'a_alpha');
      const circleMode = gl.getAttribLocation(circleProgram, 'a_mode');
      gl.enableVertexAttribArray(pos);
      gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, stride, 0);
      gl.enableVertexAttribArray(local);
      gl.vertexAttribPointer(local, 2, gl.FLOAT, false, stride, 2 * 4);
      gl.enableVertexAttribArray(color);
      gl.vertexAttribPointer(color, 3, gl.FLOAT, false, stride, 4 * 4);
      gl.enableVertexAttribArray(alpha);
      gl.vertexAttribPointer(alpha, 1, gl.FLOAT, false, stride, 7 * 4);
      gl.enableVertexAttribArray(circleMode);
      gl.vertexAttribPointer(circleMode, 1, gl.FLOAT, false, stride, 8 * 4);
      gl.uniform2f(gl.getUniformLocation(circleProgram, 'u_res'), w, h);
      gl.drawArrays(gl.TRIANGLES, 0, data.length / 9);
    }

    function modeName() {
      if (mode === MODE_GATHER) return 'gather';
      if (mode === MODE_OVERHEAT) return 'overheat';
      if (mode === MODE_EXPLODE) return 'explode';
      return 'normal';
    }

    function getGatherProgress() {
      if (!nodes.length || !pointer.has) return 0;

      let inside = 0;
      let sum = 0;
      const gatherRadius = 72 * dpr;
      const softRadius = 210 * dpr;

      for (const n of nodes) {
        const dx = n.x - coreX;
        const dy = n.y - coreY;
        const d = Math.sqrt(dx * dx + dy * dy);
        if (d < gatherRadius) inside += 1;
        sum += clamp(1 - d / softRadius, 0, 1);
      }

      return Math.max(inside / nodes.length, sum / nodes.length);
    }

    function triggerExplosion() {
      flashX = coreX;
      flashY = coreY;
      flashPower = 1.0;
      shockwaveRadius = 18 * dpr;
      shockwavePower = 1.0;

      for (const n of nodes) {
        let dx = n.x - coreX;
        let dy = n.y - coreY;
        let dist = Math.sqrt(dx * dx + dy * dy);

        if (dist < 6 * dpr) {
          const a = Math.random() * Math.PI * 2;
          dx = Math.cos(a);
          dy = Math.sin(a);
          dist = 1;
        }

        const nx = dx / dist;
        const ny = dy / dist;
        const power = (12 + Math.random() * 18 + coreHeat * 16) * dpr;
        const swirl = (Math.random() - 0.5) * 7 * dpr;

        n.vx = nx * power - ny * swirl + rand(-2.5, 2.5) * dpr;
        n.vy = ny * power + nx * swirl + rand(-2.5, 2.5) * dpr;

        n.x += nx * rand(4, 18) * dpr;
        n.y += ny * rand(4, 18) * dpr;
      }

      mode = MODE_EXPLODE;
      explodeTimer = 0;
      gatherTimer = 0;
      overheatTimer = 0;
      coreHeat = 0;
      regatherCooldown = REGATHER_COOLDOWN;
      needsRelease = true;
      pulses.length = 0;
    }

    function updateCoreState(dt) {
      if (regatherCooldown > 0) {
        regatherCooldown = Math.max(0, regatherCooldown - dt);
      }

      if (!pointer.down) {
        needsRelease = false;
      }

      if (flashPower > 0) {
        flashPower = Math.max(0, flashPower - dt * 1.85);
      }

      if (shockwavePower > 0) {
        shockwaveRadius += dt * (760 + shockwavePower * 340) * dpr;
        shockwavePower = Math.max(0, shockwavePower - dt * 1.18);
      }

      corePulse += dt * 1000;

      if (mode === MODE_EXPLODE) {
        explodeTimer += dt;
        if (explodeTimer >= EXPLODE_TIME) {
          mode = MODE_NORMAL;
        }
        return;
      }

      if (!pointer.has || !pointer.down || needsRelease || regatherCooldown > 0) {
        mode = MODE_NORMAL;
        gatherTimer = Math.max(0, gatherTimer - dt * 2.2);
        overheatTimer = 0;
        coreHeat = Math.max(0, coreHeat - dt * 2.8);
        return;
      }

      coreX = pointer.x;
      coreY = pointer.y;

      const progress = getGatherProgress();
      const gatherSpeed = 0.65 + progress * 1.35;
      gatherTimer += dt * gatherSpeed;

      if (gatherTimer < GATHER_TO_OVERHEAT) {
        mode = MODE_GATHER;
        coreHeat = Math.max(coreHeat, clamp(gatherTimer / GATHER_TO_OVERHEAT, 0, 1) * 0.22);
        return;
      }

      mode = MODE_OVERHEAT;
      overheatTimer += dt;
      coreHeat = clamp(overheatTimer / OVERHEAT_TIME, 0, 1);

      if (overheatTimer >= OVERHEAT_TIME) {
        triggerExplosion();
      }
    }

    function updateNodes(dt) {
      const isGathering = mode === MODE_GATHER || mode === MODE_OVERHEAT;
      const isExploding = mode === MODE_EXPLODE;
      const basePull = pointer.has ? (pointer.down ? 0.024 : 0.012) : 0.0;

      for (const p of nodes) {
        p.wob += p.wobSp * (dt * 1000);
        const wob = Math.sin(p.wob) * 0.12;

        if (isGathering) {
          const dx = coreX - p.x;
          const dy = coreY - p.y;
          const dist = Math.sqrt(dx * dx + dy * dy) + 0.001;
          const nx = dx / dist;
          const ny = dy / dist;
          const pull = (0.18 + coreHeat * 0.18) * dpr;
          const brake = mode === MODE_OVERHEAT ? 0.925 : 0.955;
          const jitter = mode === MODE_OVERHEAT ? coreHeat * 0.38 * dpr : 0;

          p.vx += nx * pull / p.mass;
          p.vy += ny * pull / p.mass;
          p.vx *= brake;
          p.vy *= brake;

          if (jitter > 0) {
            p.vx += rand(-jitter, jitter);
            p.vy += rand(-jitter, jitter);
          }
        } else if (pointer.has && !isExploding) {
          const dx = pointer.x - p.x;
          const dy = pointer.y - p.y;
          const d2 = dx * dx + dy * dy + 1;
          const dist = Math.sqrt(d2);
          const f = basePull * (1 / dist) * (120 * dpr);
          p.vx += (dx / dist) * f / p.mass;
          p.vy += (dy / dist) * f / p.mass;
        }

        const friction = isExploding ? 0.972 : 0.992;
        p.vx *= friction;
        p.vy *= friction;

        const heatShake = mode === MODE_OVERHEAT ? coreHeat * rand(-0.25, 0.25) * dpr : 0;
        const moveScale = isExploding ? 0.078 : 0.06;

        p.x += (p.vx + wob + heatShake) * (dt * 1000) * moveScale;
        p.y += (p.vy - wob - heatShake) * (dt * 1000) * moveScale;

        const margin = 40 * dpr;
        if (p.x < -margin) p.x = w + margin;
        if (p.x > w + margin) p.x = -margin;
        if (p.y < -margin) p.y = h + margin;
        if (p.y > h + margin) p.y = -margin;
      }
    }

    function buildEdges() {
      const LR = clamp(Math.sqrt(w * h) * 0.085, 140 * dpr, 260 * dpr);
      const LR2 = LR * LR;
      const edges = [];
      const nearPairs = [];

      for (let i = 0; i < nodes.length; i += 1) {
        const a = nodes[i];
        for (let j = i + 1; j < nodes.length; j += 1) {
          const b = nodes[j];
          const dx = a.x - b.x;
          const dy = a.y - b.y;
          const d2 = dx * dx + dy * dy;
          if (d2 < LR2) {
            const dist = Math.sqrt(d2);
            const k = 1 - (dist / LR);
            const alpha = (k * k) * 0.55;
            edges.push({ i, j, d: dist, alpha });
            if (dist < LR * 0.55 && Math.random() < 0.0025) nearPairs.push([i, j]);
          }
        }
      }

      return { edges, nearPairs };
    }

    function updatePulses(dt, edges, nearPairs) {
      pulseTimer += dt * 1000;

      if (pulseTimer > 40) {
        pulseTimer = 0;
        const count = Math.floor(Math.random() * 3) + (pointer.down ? 1 : 0);
        for (let k = 0; k < count; k += 1) {
          if (nearPairs.length) {
            const pair = nearPairs[Math.floor(Math.random() * nearPairs.length)];
            pulses.push({
              a: pair[0],
              b: pair[1],
              t: 0,
              speed: rand(0.006, 0.02),
              w: rand(0.8, 2.0) * dpr,
              c: Math.random() < 0.5 ? fx2 : fx1,
              alpha: rand(0.25, 0.75),
            });
          } else if (edges.length) {
            const e = edges[Math.floor(Math.random() * edges.length)];
            pulses.push({
              a: e.i,
              b: e.j,
              t: 0,
              speed: rand(0.006, 0.02),
              w: rand(0.8, 2.0) * dpr,
              c: Math.random() < 0.5 ? fx2 : fx1,
              alpha: rand(0.25, 0.75),
            });
          }
        }

        if (pulses.length > 120) pulses.splice(0, pulses.length - 120);
      }

      for (let k = pulses.length - 1; k >= 0; k -= 1) {
        const P = pulses[k];
        P.t += P.speed * (dt * 1000 / 16);
        if (P.t >= 1) pulses.splice(k, 1);
      }
    }

    function frame(now) {
      if (disposed) return;

      const dt = clamp((now - last) / 1000, 0.001, 0.033);
      last = now;

      updateCoreState(dt);
      updateNodes(dt);
      const { edges, nearPairs } = buildEdges();
      updatePulses(dt, edges, nearPairs);

      window.__neuralDebug.nodes = nodes.length;
      window.__neuralDebug.edges = edges.length;
      window.__neuralDebug.pulses = pulses.length;
      window.__neuralDebug.mode = modeName();
      window.__neuralDebug.heat = +coreHeat.toFixed(3);
      window.__neuralDebug.flash = +flashPower.toFixed(3);
      window.__neuralDebug.renderer = 'webgl';

      gl.disable(gl.DEPTH_TEST);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.enable(gl.BLEND);

      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      drawBackground();

      const pointerBoost = pointer.has ? (pointer.down ? 1.55 : 1.15) : 1.0;
      const heatBoost = mode === MODE_OVERHEAT ? (1.0 + coreHeat * 2.35) : 1.0;
      const explodeBoost = mode === MODE_EXPLODE ? 1.18 : 1.0;
      const ab = alphaBoost();
      const lines = [];
      const circles = [];
      const TAU = Math.PI * 2;

      for (const e of edges) {
        const a = nodes[e.i];
        const b = nodes[e.j];

        let hl = 1.0;
        if (pointer.has) {
          const mx = (a.x + b.x) * 0.5;
          const my = (a.y + b.y) * 0.5;
          const dx = mx - pointer.x;
          const dy = my - pointer.y;
          const dist = Math.sqrt(dx * dx + dy * dy);
          hl = clamp(1.35 - dist / (260 * dpr), 1.0, 1.35);
        }

        const color = [
          Math.floor((a.c[0] + b.c[0]) / 2),
          Math.floor((a.c[1] + b.c[1]) / 2),
          Math.floor((a.c[2] + b.c[2]) / 2),
        ];
        const width = (0.7 + e.alpha * 1.9) * dpr * hl;
        const alpha = Math.min(1, e.alpha * 0.55 * pointerBoost * heatBoost * explodeBoost * ab * 2.7);
        addLine(lines, a.x, a.y, b.x, b.y, width, color, alpha);
      }

      for (let k = pulses.length - 1; k >= 0; k -= 1) {
        const P = pulses[k];
        const a = nodes[P.a];
        const b = nodes[P.b];
        if (!a || !b) continue;

        const x = a.x + (b.x - a.x) * P.t;
        const y = a.y + (b.y - a.y) * P.t;
        const radius = (2.2 * dpr + P.w * 0.6) * (0.7 + 0.6 * Math.sin(P.t * TAU));
        addCircle(circles, x, y, radius, P.c, Math.min(1, P.alpha * ab * 1.35), 1);

        const backT = clamp(P.t - 0.03, 0, 1);
        const x2 = a.x + (b.x - a.x) * backT;
        const y2 = a.y + (b.y - a.y) * backT;
        addLine(lines, x2, y2, x, y, P.w * 0.9, P.c, Math.min(1, P.alpha * 0.75 * 1.35));
      }

      for (const p of nodes) {
        const radius = p.r * (1 + 0.15 * Math.sin(p.wob * 1.2));
        const nodeHeat = mode === MODE_OVERHEAT ? 1 + coreHeat * 1.25 : 1;
        addCircle(circles, p.x, p.y, radius * 14 * nodeHeat, p.c, Math.min(1, 0.30 * p.core * pointerBoost * heatBoost * ab * 3.5), 0);
        addCircle(circles, p.x, p.y, radius * 1.20 * nodeHeat, p.c, Math.min(1, 0.85 * p.core * ab * 1.25), 1);
      }

      if (mode === MODE_GATHER || mode === MODE_OVERHEAT) {
        const pulse = 0.5 + 0.5 * Math.sin(corePulse * 0.026);
        const shake = mode === MODE_OVERHEAT ? coreHeat * 5.0 * dpr : 0;
        const cx = coreX + rand(-shake, shake);
        const cy = coreY + rand(-shake, shake);
        const coreRadius = (18 + gatherTimer * 8 + coreHeat * 30 + pulse * 8 * coreHeat) * dpr;
        const glowRadius = coreRadius * (2.2 + coreHeat * 2.7);
        const hotColor = coreHeat > 0.58 ? [255, 245, 220] : fx2;

        addCircle(circles, cx, cy, glowRadius, fx1, Math.min(1, (0.11 + coreHeat * 0.32) * ab), 0);
        addCircle(circles, cx, cy, glowRadius * 0.72, fx2, Math.min(1, (0.16 + coreHeat * 0.42) * ab), 0);
        addCircle(circles, cx, cy, coreRadius * 0.70, hotColor, Math.min(1, (0.32 + coreHeat * 0.65) * ab), 1);
      }

      if (flashPower > 0) {
        const t = 1 - flashPower;
        addCircle(circles, flashX, flashY, (32 + t * 210) * dpr, fx2, flashPower * 0.55 * ab, 0);
        addCircle(circles, flashX, flashY, (18 + t * 76) * dpr, [255, 245, 225], flashPower * 0.82 * ab, 1);
        addCircle(circles, flashX, flashY, (90 + t * 420) * dpr, fx1, flashPower * 0.16 * ab, 0);
      }

      if (shockwavePower > 0) {
        const ringW = 3.5 * dpr;
        const a = shockwavePower * 0.42 * ab;
        addCircle(circles, flashX, flashY, shockwaveRadius + ringW * 5.0, fx2, a * 0.18, 0);
        addCircle(circles, flashX, flashY, shockwaveRadius + ringW, fx2, a * 0.20, 0);
        addCircle(circles, flashX, flashY, Math.max(1, shockwaveRadius - ringW), [255, 245, 225], a * 0.12, 0);
      }

      gl.blendFunc(gl.SRC_ALPHA, gl.ONE);
      drawLines(lines);
      drawCircles(circles);

      raf = requestAnimationFrame(frame);
    }

    resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(canvas);
    resize();

    raf = requestAnimationFrame((t) => {
      last = t;
      frame(t);
    });

    return () => {
      disposed = true;
      cancelAnimationFrame(raf);
      if (resizeObserver) resizeObserver.disconnect();
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mousedown', handleMouseDown);
      window.removeEventListener('mouseup', handleMouseUp);
      window.removeEventListener('touchstart', handleTouchStart);
      window.removeEventListener('touchmove', handleTouchMove);
      window.removeEventListener('touchend', handleTouchEnd);

      gl.deleteBuffer(bgBuffer);
      gl.deleteBuffer(lineBuffer);
      gl.deleteBuffer(circleBuffer);
      gl.deleteProgram(bgProgram);
      gl.deleteProgram(lineProgram);
      gl.deleteProgram(circleProgram);
    };
  }, [enabled, intensity, paletteKey]);

  if (!enabled) return null;

  return (
    <canvas
      ref={canvasRef}
      aria-hidden
      className="bgfx-canvas bgfx-canvas--webgl-neural"
      style={{
        position: 'fixed',
        inset: 0,
        width: '100%',
        height: '100%',
        zIndex: 0,
        pointerEvents: 'none',
        opacity: 1,
      }}
    />
  );
}
