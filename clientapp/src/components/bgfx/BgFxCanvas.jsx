import React, { useEffect, useMemo, useRef } from 'react';

// Canvas-фоны (bgfx)
// Варианты:
// 0: Туман
// 1: Пыль + кометы
// 2: Нейросвязи (как в присланном HTML)
// 3: Аврора
// 4: Сердечки
// 5: Matrix
// 6: Соты (Honeycomb)
// 7: Дым (Vorticity)

function cssVar(name, fallback) {
  if (typeof window === 'undefined') return fallback;
  const v = getComputedStyle(document.documentElement).getPropertyValue(name);
  return (v || fallback || '').trim();
}

function parseRgbTriplet(s, fallback = [255, 255, 255]) {
  // ожидаем "r g b" или "r, g, b"
  const clean = (s || '').replace(/,/g, ' ').trim();
  const parts = clean.split(/\s+/).map((x) => Number(x)).filter((n) => Number.isFinite(n));
  if (parts.length >= 3) return [parts[0], parts[1], parts[2]];
  return fallback;
}

function rgba(rgb, a) {
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${a})`;
}

function clamp(n, a, b) {
  return Math.min(b, Math.max(a, n));
}

function rand(min, max) {
  return min + Math.random() * (max - min);
}

function heartPath(ctx, x, y, s) {
  ctx.beginPath();
  const topCurveHeight = s * 0.3;
  ctx.moveTo(x, y + topCurveHeight);
  ctx.bezierCurveTo(
    x,
    y,
    x - s / 2,
    y,
    x - s / 2,
    y + topCurveHeight
  );
  ctx.bezierCurveTo(
    x - s / 2,
    y + (s + topCurveHeight) / 2,
    x,
    y + (s + topCurveHeight) / 1.15,
    x,
    y + s
  );
  ctx.bezierCurveTo(
    x,
    y + (s + topCurveHeight) / 1.15,
    x + s / 2,
    y + (s + topCurveHeight) / 2,
    x + s / 2,
    y + topCurveHeight
  );
  ctx.bezierCurveTo(x + s / 2, y, x, y, x, y + topCurveHeight);
  ctx.closePath();
}

export default function BgFxCanvas({ enabled, variant, intensity = 1, uiRev = 0 }) {
  const canvasRef = useRef(null);
  const rafRef = useRef(0);

  const preset = useMemo(() => {
    if (variant === 'random') {
      // 0..6
      return Math.floor(Math.random() * 8);
    }
    const v = Number(variant);
    return Number.isFinite(v) ? v : 0;
  }, [variant]);

  useEffect(() => {
    if (!enabled) return;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d', { alpha: true });
    if (!ctx) return;

    // На светлых темах нужен буст альфы, чтобы эффекты были видны.
    const isDarkTheme = () => document.documentElement.classList.contains('dark');
    const alphaBoost = () => (isDarkTheme() ? 1.0 : 1.8);
    const rgbaB = (rgb, a) => rgba(rgb, Math.min(1, a * alphaBoost()));

    let w = 0;
    let h = 0;
    let dpr = 1;

    // ВАЖНО: цвета читаем из CSS-переменных, которые зависят от классов темы на <html>.
    // Если классы применились позже (например, после auto-refresh), компонент может
    // стартовать со "старыми" цветами. Поэтому effect зависит от uiRev.
    const fx1 = parseRgbTriplet(cssVar('--fx-1', '245 0 128'), [245, 0, 128]);
    const fx2 = parseRgbTriplet(cssVar('--fx-2', '14 165 233'), [14, 165, 233]);
    const fx3 = parseRgbTriplet(cssVar('--fx-3', '34 197 94'), [34, 197, 94]);
    const fg = parseRgbTriplet(cssVar('--fg', '255 255 255'), [255, 255, 255]);

    const state = {
      t: 0,
      nodes: [],
      dust: [],
      blobs: [],
      aurora: [],
      hearts: [],
      pulses: [],       // для нейросвязей
      pulseTimer: 0,    // таймер создания импульсов
      matrix: [],       // для Matrix темы
      honey: {
        phaseA: rand(0, 9999),
        phaseB: rand(0, 9999),
        // медленные «переливы», которые иногда рандомизируются
        driftA: rand(-0.00008, 0.00008),
        driftB: rand(-0.00008, 0.00008),
        nextJitter: rand(6, 14),
        grain: null,
      },

      smoke: {
        // fluid sim state is allocated on resize when preset==7
        NX: 0, NY: 0, N: 0,
        u: null, v: null, u0: null, v0: null,
        dens: null, dens0: null,
        p: null, div: null,
        curl: null, curlAbs: null, fx: null, fy: null,
        small: null, sctx: null, imgData: null, imgArr: null,
        _fpsAcc: 0,
      },
    };

    // маленькие искры (используются в honeycomb)
    const sparks = [];
    const spawnSpark = (x, y) => {
      sparks.push({
        x,
        y,
        vx: rand(-0.4, 0.4),
        vy: rand(-0.7, -0.1),
        life: 1,
        r: rand(0.8, 2.1),
      });
      if (sparks.length > 160) sparks.splice(0, sparks.length - 160);
    };

    // зерно (для сот) — один раз на инициализацию
    try {
      const grain = document.createElement('canvas');
      const gctx = grain.getContext('2d');
      if (gctx) {
        const s = 240;
        grain.width = s;
        grain.height = s;
        const img = gctx.createImageData(s, s);
        for (let i = 0; i < img.data.length; i += 4) {
          const v = Math.floor(rand(0, 45));
          img.data[i] = v;
          img.data[i + 1] = v;
          img.data[i + 2] = v;
          img.data[i + 3] = Math.floor(rand(6, 24));
        }
        gctx.putImageData(img, 0, 0);
        state.honey.grain = grain;
      }
    } catch {
      // не критично
    }

    // rgb -> hsl (нужен для сот, чтобы подстраиваться под палитру)
    const rgbToHsl = (rgb) => {
      const r = rgb[0] / 255;
      const g = rgb[1] / 255;
      const b = rgb[2] / 255;
      const max = Math.max(r, g, b);
      const min = Math.min(r, g, b);
      const d = max - min;
      let h0 = 0;
      if (d !== 0) {
        if (max === r) h0 = ((g - b) / d) % 6;
        else if (max === g) h0 = (b - r) / d + 2;
        else h0 = (r - g) / d + 4;
        h0 *= 60;
        if (h0 < 0) h0 += 360;
      }
      const l = (max + min) / 2;
      const s = d === 0 ? 0 : d / (1 - Math.abs(2 * l - 1));
      return [h0, s, l];
    };

    // Pointer tracking для нейросвязей (интерактивность)
    const pointer = { x: w/2, y: h/2, vx: 0, vy: 0, down: false, rdown: false, has: false };
    
    const setPointer = (px, py) => {
      pointer.has = true;
      // Для fixed canvas с inset:0, координаты относительно viewport
      const nx = px * dpr;
      const ny = py * dpr;
      pointer.vx = nx - pointer.x;
      pointer.vy = ny - pointer.y;
      pointer.x = nx;
      pointer.y = ny;
    };
    
    const handleMouseMove = (e) => {
      setPointer(e.clientX, e.clientY);
    };
    const handleMouseDown = (e) => { if (e?.button === 2) pointer.rdown = true; else pointer.down = true; };
    const handleMouseUp = (e) => { if (e?.button === 2) pointer.rdown = false; else pointer.down = false; };
    
    const handleTouchStart = (e) => {
      pointer.down = true;
      pointer.rdown = (e.touches && e.touches.length >= 2);
      if (e.touches[0]) {
        setPointer(e.touches[0].clientX, e.touches[0].clientY);
      }
    };
    const handleTouchMove = (e) => {
      pointer.rdown = (e.touches && e.touches.length >= 2);
      if (e.touches[0]) {
        setPointer(e.touches[0].clientX, e.touches[0].clientY);
      }
    };
    const handleTouchEnd = () => { pointer.down = false; pointer.rdown = false; };
    
    // Подписываемся на события window (чтобы работало даже с pointerEvents:none на canvas)
    window.addEventListener('mousemove', handleMouseMove, { passive: true });
    window.addEventListener('mousedown', handleMouseDown, { passive: true });
    window.addEventListener('mouseup', handleMouseUp, { passive: true });
    window.addEventListener('touchstart', handleTouchStart, { passive: true });
    window.addEventListener('touchmove', handleTouchMove, { passive: true });
    window.addEventListener('touchend', handleTouchEnd, { passive: true });

    const resize = () => {
      const rect = canvas.getBoundingClientRect();
      w = Math.max(1, Math.floor(rect.width));
      h = Math.max(1, Math.floor(rect.height));
      // Для тяжёлых эффектов (например, «Соты»), держим DPR=1 — иначе лаги на слабых ПК.
      dpr = clamp(window.devicePixelRatio || 1, 1, 2);
      if (preset === 6) dpr = 1;
      if (preset === 7) dpr = 1;
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

      // пересоздаём частицы под текущий пресет
      state.nodes = [];
      state.dust = [];
      state.blobs = [];
      state.aurora = [];
      state.hearts = [];

      // matrix drops
      state.matrix = [];

      // honeycomb cache
      state.honey.hexR = 0;
      state.honey.hexPts = null;
      state.honey._fpsAcc = 0;
      state.honey._fpsNow = 0;

      state.smoke._fpsAcc = 0;

      // Плотность зависит от площади
      const area = w * h;

      // Нейросвязи (по примеру Hello.html)
      const nodeCount = clamp(Math.floor((area / 18000) * intensity), 50, 140);
      for (let i = 0; i < nodeCount; i += 1) {
        const s = rand(0.35, 1.25);
        const hueIdx = i % 3;
        state.nodes.push({
          x: rand(0, w),
          y: rand(0, h),
          vx: rand(-0.35, 0.35),
          vy: rand(-0.35, 0.35),
          r: rand(1.2, 2.9) * s,
          // распределяем цвета между fx1, fx2, fx3
          c: hueIdx === 0 ? fx1 : hueIdx === 1 ? fx2 : fx3,
          core: rand(0.65, 1.0),      // яркость ядра
          wob: rand(0, Math.PI * 2),  // фаза "дыхания"
          wobSp: rand(0.002, 0.01),   // скорость "дыхания"
          mass: rand(0.5, 1.6) * (1/s),
        });
      }
      // Импульсы (pulses) для нейросвязей
      state.pulses = [];
      state.pulseTimer = 0;

      // Пыль/кометы (замедленные)
      const dustCount = clamp(Math.floor((area / 14000) * intensity), 40, 200);
      for (let i = 0; i < dustCount; i += 1) {
        const fast = Math.random() < 0.12;
        state.dust.push({
          x: rand(0, w),
          y: rand(0, h),
          vx: fast ? rand(-0.35, -0.15) : rand(-0.12, 0.05),
          vy: fast ? rand(-0.08, 0.08) : rand(-0.04, 0.04),
          r: fast ? rand(1.2, 2.6) : rand(0.6, 1.6),
          a: fast ? rand(0.18, 0.35) : rand(0.05, 0.16),
          c: fast ? fx2 : fg,
          fast,
        });
      }

      // Туман (большие блюры)
      const blobCount = clamp(Math.floor((area / 90000) * intensity), 6, 20);
      for (let i = 0; i < blobCount; i += 1) {
        state.blobs.push({
          x: rand(0, w),
          y: rand(0, h),
          vx: rand(-0.12, 0.12),
          vy: rand(-0.08, 0.08),
          r: rand(Math.min(w, h) * 0.18, Math.min(w, h) * 0.35),
          a: rand(0.04, 0.09),
          c: i % 2 === 0 ? fx1 : fx2,
        });
      }

      // Аврора (полосы)
      const bandCount = 5;
      for (let i = 0; i < bandCount; i += 1) {
        state.aurora.push({
          baseY: rand(h * 0.15, h * 0.85),
          amp: rand(16, 60),
          speed: rand(0.0008, 0.0022),
          width: rand(120, 240),
          alpha: rand(0.05, 0.12),
          c: i % 3 === 0 ? fx3 : i % 3 === 1 ? fx2 : fx1,
          phase: rand(0, Math.PI * 2),
        });
      }

      // Сердечки
      const heartCount = clamp(Math.floor((area / 52000) * intensity), 10, 40);
      for (let i = 0; i < heartCount; i += 1) {
        state.hearts.push({
          x: rand(0, w),
          y: rand(0, h),
          vy: rand(-0.25, -0.08),
          vx: rand(-0.08, 0.08),
          s: rand(10, 28),
          rot: rand(-0.25, 0.25),
          vr: rand(-0.004, 0.004),
          a: rand(0.06, 0.16),
          c: i % 2 === 0 ? fx1 : fx2,
        });
      }

      // Matrix (5) - падающие символы
      state.matrix = [];
      const matrixCols = Math.floor(w / 18);
      for (let i = 0; i < matrixCols; i += 1) {
        state.matrix.push({
          x: i * 18 + rand(-4, 4),
          y: rand(-h, 0),
          speed: rand(0.4, 1.2),
          length: Math.floor(rand(8, 25)),
          chars: [],
        });
      }

    };

    // helper for honeycomb: flat-top hex points
    const TAU = Math.PI * 2;
    const hexPoints = (cx, cy, r) => {
      const pts = [];
      for (let i = 0; i < 6; i += 1) {
        const a = TAU * (i / 6) + Math.PI / 6;
        pts.push([cx + Math.cos(a) * r, cy + Math.sin(a) * r]);
      }
      return pts;
    };

    const drawHexStroke = (cx, cy, r, alpha, width, hue) => {
      const pts = hexPoints(cx, cy, r);
      ctx.strokeStyle = `hsla(${hue}, 95%, 62%, ${alpha})`;
      ctx.lineWidth = width;
      ctx.beginPath();
      ctx.moveTo(pts[0][0], pts[0][1]);
      for (let i = 1; i < 6; i += 1) ctx.lineTo(pts[i][0], pts[i][1]);
      ctx.closePath();
      ctx.stroke();
    };

    const drawHexFillGlow = (cx, cy, r, alpha, hue) => {
      const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, r * 1.6);
      g.addColorStop(0, `hsla(${hue}, 98%, 60%, ${alpha})`);
      g.addColorStop(0.55, `hsla(${hue + 12}, 98%, 48%, ${alpha * 0.45})`);
      g.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.arc(cx, cy, r * 1.6, 0, TAU);
      ctx.fill();
    };

    const bounce = (p) => {
      if (p.x < 0) {
        p.x = 0;
        p.vx = Math.abs(p.vx);
      } else if (p.x > w) {
        p.x = w;
        p.vx = -Math.abs(p.vx);
      }
      if (p.y < 0) {
        p.y = 0;
        p.vy = Math.abs(p.vy);
      } else if (p.y > h) {
        p.y = h;
        p.vy = -Math.abs(p.vy);
      }
    };

    const step = (dt) => {
      state.t += dt;

      // общая мягкая очистка
      ctx.clearRect(0, 0, w, h);

      // 0: Туман
      if (preset === 0) {
        ctx.save();
        // В светлой теме lighter не работает, используем multiply
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';
        ctx.filter = 'blur(40px)';
        for (const b of state.blobs) {
          b.x += b.vx * (dt * 60);
          b.y += b.vy * (dt * 60);
          // мягкий wrap
          if (b.x < -b.r) b.x = w + b.r;
          if (b.x > w + b.r) b.x = -b.r;
          if (b.y < -b.r) b.y = h + b.r;
          if (b.y > h + b.r) b.y = -b.r;

          const g = ctx.createRadialGradient(b.x, b.y, 0, b.x, b.y, b.r);
          const alpha = isDarkTheme() ? b.a : b.a * 1.5;
          g.addColorStop(0, rgba(b.c, alpha));
          g.addColorStop(1, rgba(b.c, 0));
          ctx.fillStyle = g;
          ctx.beginPath();
          ctx.arc(b.x, b.y, b.r, 0, Math.PI * 2);
          ctx.fill();
        }
        ctx.restore();
        return;
      }

      // 1: Пыль + кометы
      if (preset === 1) {
        ctx.save();
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';

        for (const p of state.dust) {
          const oldX = p.x;
          const oldY = p.y;
          p.x += p.vx * (dt * 60);
          p.y += p.vy * (dt * 60);
          if (p.x < -20) p.x = w + 20;
          if (p.x > w + 20) p.x = -20;
          if (p.y < -20) p.y = h + 20;
          if (p.y > h + 20) p.y = -20;

          if (p.fast) {
            const alpha = isDarkTheme() ? p.a : p.a * 1.5;
            ctx.strokeStyle = rgba(p.c, alpha);
            ctx.lineWidth = 1.25;
            ctx.beginPath();
            ctx.moveTo(oldX, oldY);
            ctx.lineTo(p.x, p.y);
            ctx.stroke();
          }

          const alpha = isDarkTheme() ? p.a : p.a * 1.5;
          ctx.fillStyle = rgba(p.c, alpha);
          ctx.beginPath();
          ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
          ctx.fill();
        }

        ctx.restore();
        return;
      }

      // 2: Нейросвязи (Neural Beauty) — полная реализация по примеру Hello.html
      if (preset === 2) {
        const TAU = Math.PI * 2;
        
        // Очищаем canvas (прозрачный фон, чтобы видеть контент)
        ctx.clearRect(0, 0, w, h);
        
        // Лёгкая виньетка для атмосферы (но не перекрывает контент)
        ctx.save();
        const gx = ctx.createRadialGradient(w*0.5, h*0.55, 0, w*0.5, h*0.55, Math.max(w,h)*0.75);
        if (isDarkTheme()) {
          gx.addColorStop(0, 'rgba(35,50,120,0.08)');
          gx.addColorStop(0.35, 'rgba(20,30,80,0.04)');
          gx.addColorStop(1, 'rgba(0,0,0,0)');
        } else {
          gx.addColorStop(0, 'rgba(200,220,255,0.05)');
          gx.addColorStop(0.35, 'rgba(180,200,240,0.02)');
          gx.addColorStop(1, 'rgba(255,255,255,0)');
        }
        ctx.fillStyle = gx;
        ctx.fillRect(0, 0, w, h);
        ctx.restore();
        
        // Параметры для pointer влияния
        const pointerPull = pointer.has ? (pointer.down ? 0.024 : 0.012) : 0.0;
        const pointerBoost = pointer.has ? (pointer.down ? 1.55 : 1.15) : 1.0;
        
        // Движение узлов
        for (const p of state.nodes) {
          // дыхание (wobble)
          p.wob += p.wobSp * (dt * 1000);
          const wob = Math.sin(p.wob) * 0.12;
          
          // гравитация к курсору
          if (pointer.has) {
            const dx = pointer.x - p.x;
            const dy = pointer.y - p.y;
            const d2 = dx*dx + dy*dy + 1;
            const f = pointerPull * (1 / Math.sqrt(d2)) * (120*dpr);
            p.vx += (dx / Math.sqrt(d2)) * f / p.mass;
            p.vy += (dy / Math.sqrt(d2)) * f / p.mass;
          }
          
          // затухание скорости
          p.vx *= 0.992;
          p.vy *= 0.992;
          
          // обновление позиции
          p.x += (p.vx + wob) * (dt * 1000) * 0.06;
          p.y += (p.vy - wob) * (dt * 1000) * 0.06;
          
          // wrap края (с запасом)
          const margin = 40*dpr;
          if (p.x < -margin) p.x = w + margin;
          if (p.x > w + margin) p.x = -margin;
          if (p.y < -margin) p.y = h + margin;
          if (p.y > h + margin) p.y = -margin;
        }
        
        // Построение рёбер (edges) между близкими узлами
        const LR = clamp(Math.sqrt(w*h) * 0.085, 140*dpr, 260*dpr); // радиус связи
        const LR2 = LR*LR;
        const edges = [];
        const nearPairs = []; // для импульсов
        
        for (let i = 0; i < state.nodes.length; i += 1) {
          const a = state.nodes[i];
          for (let j = i + 1; j < state.nodes.length; j += 1) {
            const b = state.nodes[j];
            const dx = a.x - b.x;
            const dy = a.y - b.y;
            const d2 = dx*dx + dy*dy;
            if (d2 < LR2) {
              const d = Math.sqrt(d2);
              const k = 1 - (d/LR);
              const alpha = (k*k) * 0.55;
              edges.push({ i, j, d, alpha });
              if (d < LR*0.55 && Math.random() < 0.0025) nearPairs.push([i,j]);
            }
          }
        }
        
        // Создание импульсов
        state.pulseTimer += dt * 1000;
        if (state.pulseTimer > 40) {
          state.pulseTimer = 0;
          const count = Math.floor(Math.random() * 3) + (pointer.down ? 1 : 0);
          for (let k = 0; k < count; k += 1) {
            if (nearPairs.length) {
              const [a, b] = nearPairs[Math.floor(Math.random() * nearPairs.length)];
              // спавним импульс
              state.pulses.push({
                a, b, t: 0,
                speed: rand(0.006, 0.02),
                w: rand(0.8, 2.0)*dpr,
                c: Math.random() < 0.5 ? fx2 : fx1,
                alpha: rand(0.25, 0.75)
              });
            } else if (edges.length) {
              const e = edges[Math.floor(Math.random() * edges.length)];
              state.pulses.push({
                a: e.i, b: e.j, t: 0,
                speed: rand(0.006, 0.02),
                w: rand(0.8, 2.0)*dpr,
                c: Math.random() < 0.5 ? fx2 : fx1,
                alpha: rand(0.25, 0.75)
              });
            }
          }
          // ограничение количества
          if (state.pulses.length > 120) state.pulses.splice(0, state.pulses.length - 120);
        }
        
        // Отрисовка рёбер (линий)
        ctx.save();
        ctx.globalCompositeOperation = 'screen';
        ctx.lineCap = 'round';
        
        for (const e of edges) {
          const a = state.nodes[e.i];
          const b = state.nodes[e.j];
          
          // Подсветка от курсора
          let hl = 1.0;
          if (pointer.has) {
            const mx = (a.x + b.x) * 0.5;
            const my = (a.y + b.y) * 0.5;
            const dx = mx - pointer.x;
            const dy = my - pointer.y;
            const d = Math.sqrt(dx*dx + dy*dy);
            hl = clamp(1.35 - d/(260*dpr), 1.0, 1.35);
          }
          
          // Смешиваем цвета узлов
          const [r1, g1, b1] = a.c;
          const [r2, g2, b2] = b.c;
          const r = Math.floor((r1+r2)/2);
          const g = Math.floor((g1+g2)/2);
          const b_ = Math.floor((b1+b2)/2);
          
          const w_ = (0.7 + e.alpha*1.9) * dpr * hl;
          const alpha_ = e.alpha * 0.55 * pointerBoost * alphaBoost();
          
          ctx.strokeStyle = `rgba(${r}, ${g}, ${b_}, ${Math.min(1, alpha_)})`;
          ctx.lineWidth = w_;
          ctx.beginPath();
          ctx.moveTo(a.x, a.y);
          ctx.lineTo(b.x, b.y);
          ctx.stroke();
        }
        
        // Обновление и отрисовка импульсов
        for (let k = state.pulses.length - 1; k >= 0; k -= 1) {
          const P = state.pulses[k];
          P.t += P.speed * (dt * 1000 / 16);
          if (P.t >= 1) {
            state.pulses.splice(k, 1);
            continue;
          }
          
          const a = state.nodes[P.a];
          const b = state.nodes[P.b];
          const x = a.x + (b.x - a.x) * P.t;
          const y = a.y + (b.y - a.y) * P.t;
          
          // искра
          const [r, g, b_] = P.c;
          ctx.beginPath();
          ctx.fillStyle = `rgba(${r}, ${g}, ${b_}, ${P.alpha * alphaBoost()})`;
          ctx.arc(x, y, (2.2*dpr + P.w*0.6) * (0.7 + 0.6*Math.sin(P.t*TAU)), 0, TAU);
          ctx.fill();
          
          // хвост
          const backT = clamp(P.t - 0.03, 0, 1);
          const x2 = a.x + (b.x - a.x) * backT;
          const y2 = a.y + (b.y - a.y) * backT;
          ctx.strokeStyle = `rgba(${r}, ${g}, ${b_}, ${P.alpha*0.75})`;
          ctx.lineWidth = P.w * 0.9;
          ctx.beginPath();
          ctx.moveTo(x2, y2);
          ctx.lineTo(x, y);
          ctx.stroke();
        }
        
        // Отрисовка узлов (с красивым свечением)
        for (const p of state.nodes) {
          const r_ = p.r * (1 + 0.15*Math.sin(p.wob*1.2));
          const [r, g, b_] = p.c;
          
          // свечение (glow)
          const glow = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, r_*10);
          glow.addColorStop(0, `rgba(${r}, ${g}, ${b_}, ${0.30*p.core*pointerBoost*alphaBoost()})`);
          glow.addColorStop(0.25, `rgba(${Math.min(255,r+25)}, ${Math.min(255,g+25)}, ${Math.min(255,b_+25)}, ${0.14*p.core*alphaBoost()})`);
          glow.addColorStop(1, 'rgba(0,0,0,0)');
          ctx.fillStyle = glow;
          ctx.beginPath();
          ctx.arc(p.x, p.y, r_*10, 0, TAU);
          ctx.fill();
          
          // ядро
          ctx.fillStyle = `rgba(${r}, ${g}, ${b_}, ${0.85*p.core*alphaBoost()})`;
          ctx.beginPath();
          ctx.arc(p.x, p.y, r_*1.15, 0, TAU);
          ctx.fill();
        }
        
        // Bloom эффект (fake) — повторная отрисовка с прозрачностью
        ctx.globalAlpha = 0.25;
        ctx.globalCompositeOperation = 'screen';
        ctx.drawImage(canvas, 0, 0, w*dpr, h*dpr, 0, 0, w, h);
        
        ctx.restore();
        return;
      }

      // 3: Аврора
      if (preset === 3) {
        ctx.save();
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';

        for (const band of state.aurora) {
          const t = state.t;
          const y0 = band.baseY + Math.sin(t * 0.7 + band.phase) * 20;
          const g = ctx.createLinearGradient(0, y0 - band.width / 2, 0, y0 + band.width / 2);
          const alpha = isDarkTheme() ? band.alpha : band.alpha * 1.8;
          g.addColorStop(0, rgba(band.c, 0));
          g.addColorStop(0.5, rgba(band.c, alpha));
          g.addColorStop(1, rgba(band.c, 0));
          ctx.fillStyle = g;

          // рисуем синусную полосу
          ctx.beginPath();
          const stepX = 32;
          ctx.moveTo(0, y0);
          for (let x = 0; x <= w + stepX; x += stepX) {
            const y =
              y0 +
              Math.sin(x * 0.01 + t * (band.speed * 1000) + band.phase) * band.amp +
              Math.sin(x * 0.004 + t * 0.6) * (band.amp * 0.35);
            ctx.lineTo(x, y);
          }
          ctx.lineTo(w, y0 + band.width);
          ctx.lineTo(0, y0 + band.width);
          ctx.closePath();
          ctx.filter = 'blur(26px)';
          ctx.fill();
          ctx.filter = 'none';
        }

        ctx.restore();
        return;
      }

      // 4: Сердечки
      if (preset === 4) {
        ctx.save();
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';
        for (const p of state.hearts) {
          p.x += p.vx * (dt * 60);
          p.y += p.vy * (dt * 60);
          p.rot += p.vr * (dt * 60);
          if (p.y < -40) {
            p.y = h + 40;
            p.x = rand(0, w);
          }
          if (p.x < -40) p.x = w + 40;
          if (p.x > w + 40) p.x = -40;

          ctx.save();
          ctx.translate(p.x, p.y);
          ctx.rotate(p.rot);
          const alpha = isDarkTheme() ? p.a : p.a * 1.6;
          ctx.fillStyle = rgba(p.c, alpha);
          heartPath(ctx, 0, 0, p.s);
          ctx.fill();
          ctx.restore();
        }
        ctx.restore();
        return;
      }

      // 5: Matrix - падающие символы
      if (preset === 5) {
        // Катакана и ASCII символы
        const chars = 'アイウエオカキクケコサシスセソタチツテト0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ$@%#&*+=<>[]{}|';
        
        ctx.save();
        // Тёмная затемняющая маска (след от символов)
        ctx.fillStyle = isDarkTheme() ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.08)';
        ctx.fillRect(0, 0, w, h);
        
        ctx.font = '14px monospace';
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';
        
        for (const drop of state.matrix) {
          // Движение вниз
          drop.y += drop.speed * (dt * 60);
          
          // Сброс наверх при выпадении за экран
          if (drop.y > h + drop.length * 16) {
            drop.y = rand(-h * 0.5, 0);
            drop.x = Math.floor(rand(0, Math.floor(w / 18))) * 18 + rand(-4, 4);
            drop.speed = rand(0.4, 1.2);
            drop.length = Math.floor(rand(8, 25));
          }
          
          // Рисуем символы
          for (let i = 0; i < drop.length; i += 1) {
            const y = drop.y - i * 16;
            if (y < 0 || y > h) continue;
            
            // Яркость убывает к хвосту
            const alpha = (1 - i / drop.length) * 0.8;
            
            // Голова дропа - белая/светлая
            if (i === 0) {
              ctx.fillStyle = isDarkTheme() 
                ? `rgba(${fg[0]}, ${fg[1]}, ${fg[2]}, ${alpha * 1.2})`
                : `rgba(${fx1[0]}, ${fx1[1]}, ${fx1[2]}, ${alpha * 0.9})`;
            } else {
              // Остальные - зелёные (или темные в светлой теме)
              const [r, g, b] = isDarkTheme() ? [100, 255, 150] : fx1;
              ctx.fillStyle = `rgba(${r}, ${g}, ${b}, ${alpha * (isDarkTheme() ? 0.85 : 0.6)})`;
            }
            
            // Случайный символ
            const char = chars[Math.floor(Math.random() * chars.length)];
            ctx.fillText(char, drop.x, y);
          }
        }
        
        ctx.restore();
        return;
      }

      // 6: Honeycomb — живые соты
      if (preset === 6) {
        const now = state.t;

        // Идея: упростить демо «Honeycomb», чтобы не лагало.
        // Убираем blur+glow+sparks+bloom+grain, делаем один быстрый проход по сетке.
        // Волны — в ~10 раз медленнее и с редкими случайными «перестройками».

        state.honey.nextJitter -= dt;
        if (state.honey.nextJitter <= 0) {
          state.honey.nextJitter = rand(10, 18);
          state.honey.driftA = rand(-0.00003, 0.00003);
          state.honey.phaseA = rand(0, 9999);
        }
        state.honey.phaseA += state.honey.driftA * dt;

        const [hue] = rgbToHsl(fx1);

        ctx.save();

        // фон (лёгкий)
        const bg = ctx.createLinearGradient(0, 0, w, h);
        if (isDarkTheme()) {
          bg.addColorStop(0, 'rgba(6, 6, 9, 1)');
          bg.addColorStop(1, 'rgba(2, 2, 4, 1)');
        } else {
          bg.addColorStop(0, 'rgba(255,255,255,0.92)');
          bg.addColorStop(1, 'rgba(255,255,255,0.78)');
        }
        ctx.fillStyle = bg;
        ctx.fillRect(0, 0, w, h);

        // parallax (очень лёгкий)
        const px = pointer.has ? (pointer.x - w * 0.5) : 0;
        const py = pointer.has ? (pointer.y - h * 0.5) : 0;
        const ox = -px * 0.02;
        const oy = -py * 0.02;

        // делаем соты крупнее => меньше ячеек => быстрее
        // В светлой теме делаем ещё крупнее (школьные ПК / слабые браузеры).
        const r = isDarkTheme()
          ? clamp(Math.sqrt(w * h) / 26, 30, 60)
          : clamp(Math.sqrt(w * h) / 18, 60, 120);
        const ww = Math.sqrt(3) * r;
        const hh = 2 * r;
        const rowStep = 1.5 * r;

        // кеш точек шестиугольника (без тригонометрии в цикле)
        if (!state.honey.hexPts || state.honey.hexR !== r) {
          state.honey.hexR = r;
          const pts = [];
          for (let i = 0; i < 6; i += 1) {
            const a = TAU * (i / 6) + Math.PI / 6; // flat-top
            pts.push({ x: Math.cos(a) * (r * 0.94), y: Math.sin(a) * (r * 0.94) });
          }
          state.honey.hexPts = pts;
        }

        // Цвет линии один, меняем только alpha/width
        // В светлой теме стараемся быть максимально лёгкими по композитингу.
        ctx.globalCompositeOperation = isDarkTheme() ? 'screen' : 'source-over';
        ctx.strokeStyle = `hsla(${hue}, 92%, ${isDarkTheme() ? 60 : 38}%, 1)`;

        const cx = pointer.has ? pointer.x : w * 0.55;
        const cy = pointer.has ? pointer.y : h * 0.52;
        const falloff2 = (isDarkTheme() ? 320 : 420) ** 2;

        let row = 0;
        for (let y = -hh + oy; y < h + hh; y += rowStep, row += 1) {
          const xOff = row % 2 ? ww / 2 : 0;
          for (let x = -ww + ox; x < w + ww; x += ww) {
            const hx = x + xOff;
            const hy = y;

            // лёгкая «волна» без sqrt — от положения клетки
            const wave = Math.sin((hx * 0.014 + hy * 0.011) - now * 0.00018 + state.honey.phaseA) * 0.5 + 0.5;

            // подсветка около курсора (без sqrt)
            const dx = hx - cx;
            const dy = hy - cy;
            const d2 = dx * dx + dy * dy;
            // exp() дорогой на большом количестве ячеек, используем более лёгкую аппроксимацию
            // 1/(1 + d^2/k) — достаточно похоже для «пятна» вокруг курсора.
            const ring = pointer.has ? (1 / (1 + d2 / falloff2)) : 0.12;

            // видимость по всей сетке + усиление возле курсора
            const a = (0.06 + 0.10 * wave) + ring * (0.10 + 0.14 * wave);
            const width = 0.9 + ring * 1.6;

            ctx.globalAlpha = a * (isDarkTheme() ? 0.85 : 0.55) * alphaBoost();
            ctx.lineWidth = width;

            const pts = state.honey.hexPts;
            ctx.beginPath();
            ctx.moveTo(hx + pts[0].x, hy + pts[0].y);
            ctx.lineTo(hx + pts[1].x, hy + pts[1].y);
            ctx.lineTo(hx + pts[2].x, hy + pts[2].y);
            ctx.lineTo(hx + pts[3].x, hy + pts[3].y);
            ctx.lineTo(hx + pts[4].x, hy + pts[4].y);
            ctx.lineTo(hx + pts[5].x, hy + pts[5].y);
            ctx.closePath();
            ctx.stroke();
          }
        }

        ctx.restore();
        return;
      }


// 7: Дым (вихри / vorticity) — портировано из твоего HTML (Smoke Vorticity)
if (preset === 7) {
  const sm = state.smoke;
  if (!sm || !sm.u || !sm.v || !sm.dens || !sm.sctx || !sm.imgData || !sm.imgArr) {
    return;
  }

  const NX = sm.NX;
  const NY = sm.NY;
  const N = sm.N;

  const idx = (x, y) => x + y * NX;

  const set_bnd = (b, x) => {
    for (let i = 1; i < NX - 1; i++) {
      x[idx(i, 0)] = b === 2 ? -x[idx(i, 1)] : x[idx(i, 1)];
      x[idx(i, NY - 1)] = b === 2 ? -x[idx(i, NY - 2)] : x[idx(i, NY - 2)];
    }
    for (let j = 1; j < NY - 1; j++) {
      x[idx(0, j)] = b === 1 ? -x[idx(1, j)] : x[idx(1, j)];
      x[idx(NX - 1, j)] = b === 1 ? -x[idx(NX - 2, j)] : x[idx(NX - 2, j)];
    }
    x[idx(0, 0)] = 0.5 * (x[idx(1, 0)] + x[idx(0, 1)]);
    x[idx(0, NY - 1)] = 0.5 * (x[idx(1, NY - 1)] + x[idx(0, NY - 2)]);
    x[idx(NX - 1, 0)] = 0.5 * (x[idx(NX - 2, 0)] + x[idx(NX - 1, 1)]);
    x[idx(NX - 1, NY - 1)] = 0.5 * (x[idx(NX - 2, NY - 1)] + x[idx(NX - 1, NY - 2)]);
  };

  const lin_solve = (b, x, x0, a, c, iters) => {
    for (let k = 0; k < iters; k++) {
      for (let j = 1; j < NY - 1; j++) {
        for (let i = 1; i < NX - 1; i++) {
          const id = idx(i, j);
          x[id] = (x0[id] + a * (x[idx(i - 1, j)] + x[idx(i + 1, j)] + x[idx(i, j - 1)] + x[idx(i, j + 1)])) / c;
        }
      }
      set_bnd(b, x);
    }
  };

  const diffuse = (b, x, x0, diff, dt2) => {
    const a = dt2 * diff * (NX - 2) * (NY - 2);
    lin_solve(b, x, x0, a, 1 + 4 * a, 12);
  };

  const advect = (b, d, d0, u, v, dt2) => {
    const dt0x = dt2 * (NX - 2);
    const dt0y = dt2 * (NY - 2);
    for (let j = 1; j < NY - 1; j++) {
      for (let i = 1; i < NX - 1; i++) {
        const id = idx(i, j);
        let x = i - dt0x * u[id];
        let y = j - dt0y * v[id];

        x = clamp(x, 0.5, NX - 1.5);
        y = clamp(y, 0.5, NY - 1.5);

        const i0 = x | 0, i1 = i0 + 1;
        const j0 = y | 0, j1 = j0 + 1;

        const s1 = x - i0, s0 = 1 - s1;
        const t1 = y - j0, t0 = 1 - t1;

        d[id] =
          s0 * (t0 * d0[idx(i0, j0)] + t1 * d0[idx(i0, j1)]) +
          s1 * (t0 * d0[idx(i1, j0)] + t1 * d0[idx(i1, j1)]);
      }
    }
    set_bnd(b, d);
  };

  const project = (u, v, p, div) => {
    for (let j = 1; j < NY - 1; j++) {
      for (let i = 1; i < NX - 1; i++) {
        const id = idx(i, j);
        div[id] = -0.5 * (u[idx(i + 1, j)] - u[idx(i - 1, j)] + v[idx(i, j + 1)] - v[idx(i, j - 1)]) / NX;
        p[id] = 0;
      }
    }
    set_bnd(0, div);
    set_bnd(0, p);
    lin_solve(0, p, div, 1, 4, 22);

    for (let j = 1; j < NY - 1; j++) {
      for (let i = 1; i < NX - 1; i++) {
        const id = idx(i, j);
        u[id] -= 0.5 * NX * (p[idx(i + 1, j)] - p[idx(i - 1, j)]);
        v[id] -= 0.5 * NY * (p[idx(i, j + 1)] - p[idx(i, j - 1)]);
      }
    }
    set_bnd(1, u);
    set_bnd(2, v);
  };

  const computeCurl = () => {
    for (let j = 1; j < NY - 1; j++) {
      for (let i = 1; i < NX - 1; i++) {
        const id = idx(i, j);
        const dv_dx = (sm.v[idx(i + 1, j)] - sm.v[idx(i - 1, j)]) * 0.5;
        const du_dy = (sm.u[idx(i, j + 1)] - sm.u[idx(i, j - 1)]) * 0.5;
        const c = dv_dx - du_dy;
        sm.curl[id] = c;
        sm.curlAbs[id] = Math.abs(c);
      }
    }
    for (let i = 0; i < NX; i++) {
      sm.curl[idx(i, 0)] = 0;
      sm.curlAbs[idx(i, 0)] = 0;
      sm.curl[idx(i, NY - 1)] = 0;
      sm.curlAbs[idx(i, NY - 1)] = 0;
    }
    for (let j = 0; j < NY; j++) {
      sm.curl[idx(0, j)] = 0;
      sm.curlAbs[idx(0, j)] = 0;
      sm.curl[idx(NX - 1, j)] = 0;
      sm.curlAbs[idx(NX - 1, j)] = 0;
    }
  };

  const applyVorticity = (dt2, eps) => {
    for (let j = 2; j < NY - 2; j++) {
      for (let i = 2; i < NX - 2; i++) {
        const id = idx(i, j);
        const dw_dx = (sm.curlAbs[idx(i + 1, j)] - sm.curlAbs[idx(i - 1, j)]) * 0.5;
        const dw_dy = (sm.curlAbs[idx(i, j + 1)] - sm.curlAbs[idx(i, j - 1)]) * 0.5;
        const len = Math.hypot(dw_dx, dw_dy) + 1e-6;
        const nx = dw_dx / len;
        const ny = dw_dy / len;
        const c = sm.curl[id];
        sm.fx[id] = ny * c;
        sm.fy[id] = -nx * c;
      }
    }
    for (let j = 2; j < NY - 2; j++) {
      for (let i = 2; i < NX - 2; i++) {
        const id = idx(i, j);
        sm.u[id] += sm.fx[id] * eps * dt2;
        sm.v[id] += sm.fy[id] * eps * dt2;
      }
    }
    set_bnd(1, sm.u);
    set_bnd(2, sm.v);
  };

  const splat = (px, py, vx, vy, addD) => {
    const gx = (px / w) * (NX - 1);
    const gy = (py / h) * (NY - 1);
    const r = pointer.down ? 13 : 9;
    const r2 = r * r;

    for (let j = -r; j <= r; j++) {
      for (let i = -r; i <= r; i++) {
        const x = (gx + i) | 0;
        const y = (gy + j) | 0;
        if (x <= 1 || x >= NX - 2 || y <= 1 || y >= NY - 2) continue;
        const d2 = i * i + j * j;
        if (d2 > r2) continue;
        const ww = Math.exp(-d2 / (r2 * 0.55));
        const id = idx(x, y);
        sm.u[id] += vx * ww;
        sm.v[id] += vy * ww;
        sm.dens[id] += addD * ww;
      }
    }
  };

  const vacuum = (px, py, strength) => {
    const gx = (px / w) * (NX - 1);
    const gy = (py / h) * (NY - 1);
    const r = 12;
    const r2 = r * r;

    for (let j = -r; j <= r; j++) {
      for (let i = -r; i <= r; i++) {
        const x = (gx + i) | 0;
        const y = (gy + j) | 0;
        if (x <= 1 || x >= NX - 2 || y <= 1 || y >= NY - 2) continue;
        const d2 = i * i + j * j;
        if (d2 > r2) continue;
        const ww = Math.exp(-d2 / (r2 * 0.55));
        const id = idx(x, y);

        sm.dens[id] = Math.max(0, sm.dens[id] - strength * ww);
        const dx = gx - x;
        const dy = gy - y;
        sm.u[id] += dx * 0.002 * ww;
        sm.v[id] += dy * 0.002 * ww;
      }
    }
  };

  const renderSmoke = () => {
    // прозрачный фон, только дым
    const arr = sm.imgArr;
    for (let j = 0; j < NY; j++) {
      for (let i = 0; i < NX; i++) {
        const id = idx(i, j);
        const d = clamp(sm.dens[id], 0, 1.35);

        // цвет дыма под тему: mix fx1/fx2 + чуть fg
        const mix = clamp(d * 0.55, 0, 0.85);
        const r = (fx1[0] * (1 - mix) + fx2[0] * mix) * 0.55 + fg[0] * 0.45;
        const g = (fx1[1] * (1 - mix) + fx2[1] * mix) * 0.55 + fg[1] * 0.45;
        const b = (fx1[2] * (1 - mix) + fx2[2] * mix) * 0.55 + fg[2] * 0.45;

        const a = clamp(d * 210 * (isDarkTheme() ? 1.0 : 0.75), 0, 235) * 0.9;

        const off = (i + j * NX) * 4;
        arr[off + 0] = r;
        arr[off + 1] = g;
        arr[off + 2] = b;
        arr[off + 3] = a;
      }
    }
    sm.sctx.putImageData(sm.imgData, 0, 0);

    ctx.save();
    ctx.globalCompositeOperation = 'screen';
    ctx.imageSmoothingEnabled = true;

    // 3 прохода как в демке (fog/details/filaments), но без заливки bg
    const blurA = 14;
    const blurB = 5;
    const blurC = 1.2;

    ctx.filter = `blur(${blurA}px)`;
    ctx.globalAlpha = isDarkTheme() ? 0.62 : 0.40;
    ctx.drawImage(sm.small, 0, 0, NX, NY, 0, 0, w, h);

    ctx.filter = `blur(${blurB}px)`;
    ctx.globalAlpha = isDarkTheme() ? 0.90 : 0.60;
    ctx.drawImage(sm.small, 0, 0, NX, NY, 0, 0, w, h);

    ctx.filter = `blur(${blurC}px)`;
    ctx.globalAlpha = isDarkTheme() ? 0.72 : 0.48;
    ctx.drawImage(sm.small, 0, 0, NX, NY, 0, 0, w, h);

    ctx.filter = 'none';
    ctx.restore();
  };

  // --- Simulation constants (с твоими значениями, чуть подстроено intensity) ---
  const VISC = 0.00012;
  const DIFF = 0.00007;
  const DISSIP = 0.9935;
  const VEL_DAMP = 0.995;
  const VORTICITY = 32.0 * (0.7 + intensity * 0.7);

  const dt2 = clamp(dt, 0.001, 0.03);

  // ambient emitter (ниже центра)
  const now = performance.now();
  const emitX = w * 0.5 + Math.sin(now * 0.00035) * w * 0.09;
  const emitY = h * 0.78 + Math.cos(now * 0.00031) * h * 0.05;
  splat(emitX, emitY, 0, -8 * dt2, 0.020);

  if (pointer.has) {
    const speed = Math.hypot(pointer.vx, pointer.vy);
    const force = (pointer.down ? 75 : 46) * (0.6 + clamp(speed / 22, 0, 1.2));
    const fx0 = (pointer.vx / Math.max(1, w)) * force;
    const fy0 = (pointer.vy / Math.max(1, h)) * force;
    const add = pointer.down ? 0.16 : 0.07;

    if (pointer.rdown) vacuum(pointer.x, pointer.y, 0.25);
    else splat(pointer.x, pointer.y, fx0, fy0, add);
  }

  // velocity
  sm.u0.set(sm.u);
  sm.v0.set(sm.v);
  diffuse(1, sm.u, sm.u0, VISC, dt2);
  diffuse(2, sm.v, sm.v0, VISC, dt2);
  project(sm.u, sm.v, sm.p, sm.div);

  computeCurl();
  applyVorticity(dt2, VORTICITY);

  sm.u0.set(sm.u);
  sm.v0.set(sm.v);
  advect(1, sm.u, sm.u0, sm.u0, sm.v0, dt2);
  advect(2, sm.v, sm.v0, sm.u0, sm.v0, dt2);
  project(sm.u, sm.v, sm.p, sm.div);

  // density
  sm.dens0.set(sm.dens);
  diffuse(0, sm.dens, sm.dens0, DIFF, dt2);
  sm.dens0.set(sm.dens);
  advect(0, sm.dens, sm.dens0, sm.u, sm.v, dt2);

  for (let i = 0; i < N; i++) {
    sm.dens[i] *= DISSIP;
    sm.u[i] *= VEL_DAMP;
    sm.v[i] *= VEL_DAMP;
    if (sm.dens[i] < 0.00001) sm.dens[i] = 0;
  }

  renderSmoke();
  return;
}

      // Smoke (7) — интерактивный дым с вихрями (vorticity)
      if (preset === 7) {
        const sm = state.smoke;
        const target = clamp(Math.sqrt(w * h) / 6.2, 130, 260);
        const aspect = w / h;
        let NX = Math.floor(target * Math.sqrt(aspect));
        let NY = Math.floor(target / Math.sqrt(aspect));
        NX = clamp(NX, 110, 320);
        NY = clamp(NY, 110, 320);

        sm.NX = NX;
        sm.NY = NY;
        sm.N = NX * NY;

        sm.u = new Float32Array(sm.N);
        sm.v = new Float32Array(sm.N);
        sm.u0 = new Float32Array(sm.N);
        sm.v0 = new Float32Array(sm.N);
        sm.dens = new Float32Array(sm.N);
        sm.dens0 = new Float32Array(sm.N);
        sm.p = new Float32Array(sm.N);
        sm.div = new Float32Array(sm.N);
        sm.curl = new Float32Array(sm.N);
        sm.curlAbs = new Float32Array(sm.N);
        sm.fx = new Float32Array(sm.N);
        sm.fy = new Float32Array(sm.N);

        try {
          if (!sm.small) {
            sm.small = document.createElement('canvas');
            sm.sctx = sm.small.getContext('2d', { willReadFrequently: true });
          }
          if (sm.small) {
            sm.small.width = NX;
            sm.small.height = NY;
          }
          if (sm.sctx) {
            sm.imgData = sm.sctx.createImageData(NX, NY);
            sm.imgArr = sm.imgData.data;
          }
        } catch {
          // ignore
        }
      }

    };

    let last = performance.now();
    const tick = (now) => {
      // Для тяжёлых фонов («Соты», «Дым») режем FPS до ~30, чтобы не убивать слабые машины.
      if (preset === 6 || preset === 7) {
        const ms = now - last;
        last = now;
        const accObj = preset === 6 ? state.honey : state.smoke;
        accObj._fpsAcc = (accObj._fpsAcc || 0) + ms;
        if (accObj._fpsAcc < 33) {
          rafRef.current = requestAnimationFrame(tick);
          return;
        }
        const dt = clamp((accObj._fpsAcc / 1000), 0.001, 0.05);
        accObj._fpsAcc = 0;
        step(dt);
      } else {
        const dt = clamp((now - last) / 1000, 0.001, 0.05);
        last = now;
        step(dt);
      }
      rafRef.current = requestAnimationFrame(tick);
    };

    const ro = new ResizeObserver(resize);
    ro.observe(canvas);
    resize();
    rafRef.current = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(rafRef.current);
      ro.disconnect();
      // Отписываемся от событий
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mousedown', handleMouseDown);
      window.removeEventListener('mouseup', handleMouseUp);
      window.removeEventListener('touchstart', handleTouchStart);
      window.removeEventListener('touchmove', handleTouchMove);
      window.removeEventListener('touchend', handleTouchEnd);
    };
  }, [enabled, preset, intensity, uiRev]);

  if (!enabled) return null;

  return (
    <canvas
      ref={canvasRef}
      aria-hidden
      className="bgfx-canvas"
      style={{
        position: 'fixed',
        inset: 0,
        width: '100%',
        height: '100%',
        zIndex: 0,
        pointerEvents: 'none',
        // чуть мягче на тёмной теме
        opacity: 1,
      }}
    />
  );
}
