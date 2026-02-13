import React, { useEffect, useMemo, useRef } from 'react';

// Canvas-фоны (bgfx)
// Варианты:
// 0: Туман
// 1: Пыль + кометы
// 2: Нейросвязи (как в присланном HTML)
// 3: Аврора
// 4: Сердечки

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

export default function BgFxCanvas({ enabled, variant, intensity = 1 }) {
  const canvasRef = useRef(null);
  const rafRef = useRef(0);

  const preset = useMemo(() => {
    if (variant === 'random') {
      // 0..4
      return Math.floor(Math.random() * 5);
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

    // На светлых темах многие альфы выглядят слишком "нежно".
    // Даём небольшой буст и убираем чёрную "шторку" в нейросвязях.
    const isDarkTheme = () => document.documentElement.classList.contains('dark');
    const alphaBoost = () => (isDarkTheme() ? 1 : 2.0);
    const rgbaB = (rgb, a) => rgba(rgb, Math.min(1, a * alphaBoost()));

    let w = 0;
    let h = 0;
    let dpr = 1;

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
    };

    const resize = () => {
      const rect = canvas.getBoundingClientRect();
      w = Math.max(1, Math.floor(rect.width));
      h = Math.max(1, Math.floor(rect.height));
      dpr = clamp(window.devicePixelRatio || 1, 1, 2);
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

      // пересоздаём частицы под текущий пресет
      state.nodes = [];
      state.dust = [];
      state.blobs = [];
      state.aurora = [];
      state.hearts = [];

      // Плотность зависит от площади
      const area = w * h;

      // Нейросвязи
      const nodeCount = clamp(Math.floor((area / 18000) * intensity), 30, 120);
      for (let i = 0; i < nodeCount; i += 1) {
        state.nodes.push({
          x: rand(0, w),
          y: rand(0, h),
          vx: rand(-0.25, 0.25),
          vy: rand(-0.25, 0.25),
          r: rand(1.2, 2.6),
          c: i % 3 === 0 ? fx1 : i % 3 === 1 ? fx2 : fx3,
        });
      }

      // Пыль/кометы
      const dustCount = clamp(Math.floor((area / 14000) * intensity), 40, 200);
      for (let i = 0; i < dustCount; i += 1) {
        const fast = Math.random() < 0.12;
        state.dust.push({
          x: rand(0, w),
          y: rand(0, h),
          vx: fast ? rand(-1.2, -0.3) : rand(-0.25, 0.1),
          vy: fast ? rand(-0.25, 0.25) : rand(-0.08, 0.08),
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
        ctx.globalCompositeOperation = 'lighter';
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
          g.addColorStop(0, rgba(b.c, b.a));
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
        ctx.globalCompositeOperation = 'lighter';

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
            ctx.strokeStyle = rgba(p.c, p.a);
            ctx.lineWidth = 1.25;
            ctx.beginPath();
            ctx.moveTo(oldX, oldY);
            ctx.lineTo(p.x, p.y);
            ctx.stroke();
          }

          ctx.fillStyle = rgba(p.c, p.a);
          ctx.beginPath();
          ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
          ctx.fill();
        }

        ctx.restore();
        return;
      }

      // 2: Нейросвязи
      if (preset === 2) {
        // фон — лёгкая дымка
        ctx.save();
        ctx.globalCompositeOperation = 'source-over';
        ctx.fillStyle = isDarkTheme() ? 'rgba(0,0,0,0.08)' : 'rgba(255,255,255,0.18)';
        ctx.fillRect(0, 0, w, h);
        ctx.restore();

        // движение
        for (const n of state.nodes) {
          n.x += n.vx * (dt * 60);
          n.y += n.vy * (dt * 60);
          bounce(n);
        }

        // линии
        ctx.save();
        ctx.globalCompositeOperation = 'lighter';
        const maxDist = 140;
        for (let i = 0; i < state.nodes.length; i += 1) {
          const a = state.nodes[i];
          for (let j = i + 1; j < state.nodes.length; j += 1) {
            const b = state.nodes[j];
            const dx = a.x - b.x;
            const dy = a.y - b.y;
            const d = Math.hypot(dx, dy);
            if (d > maxDist) continue;
            const k = 1 - d / maxDist;
            // цвет — смесь, но просто берём a
            // на светлой теме бустим альфу, иначе линии почти не видны
            ctx.strokeStyle = rgbaB(a.c, 0.06 + k * 0.18);
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(a.x, a.y);
            ctx.lineTo(b.x, b.y);
            ctx.stroke();
          }
        }

        // узлы
        for (const n of state.nodes) {
          ctx.fillStyle = rgbaB(n.c, 0.22);
          ctx.beginPath();
          ctx.arc(n.x, n.y, n.r * 2.2, 0, Math.PI * 2);
          ctx.fill();

          ctx.fillStyle = rgbaB(n.c, 0.70);
          ctx.beginPath();
          ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
          ctx.fill();
        }

        // редкий "импульс" через таймер
        const pulseEvery = 2.2;
        const pulseT = state.t % pulseEvery;
        if (pulseT < 0.15) {
          const idx = Math.floor(Math.random() * state.nodes.length);
          const p = state.nodes[idx];
          const pr = 6 + (pulseT / 0.15) * 26;
          ctx.strokeStyle = rgba(fx2, 0.25 * (1 - pulseT / 0.15));
          ctx.lineWidth = 2;
          ctx.beginPath();
          ctx.arc(p.x, p.y, pr, 0, Math.PI * 2);
          ctx.stroke();
        }

        ctx.restore();
        return;
      }

      // 3: Аврора
      if (preset === 3) {
        ctx.save();
        ctx.globalCompositeOperation = 'lighter';

        for (const band of state.aurora) {
          const t = state.t;
          const y0 = band.baseY + Math.sin(t * 0.7 + band.phase) * 20;
          const g = ctx.createLinearGradient(0, y0 - band.width / 2, 0, y0 + band.width / 2);
          g.addColorStop(0, rgba(band.c, 0));
          g.addColorStop(0.5, rgba(band.c, band.alpha));
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
        ctx.globalCompositeOperation = 'lighter';
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
          ctx.fillStyle = rgba(p.c, p.a);
          heartPath(ctx, 0, 0, p.s);
          ctx.fill();
          ctx.restore();
        }
        ctx.restore();
      }
    };

    let last = performance.now();
    const tick = (now) => {
      const dt = clamp((now - last) / 1000, 0.001, 0.05);
      last = now;
      step(dt);
      rafRef.current = requestAnimationFrame(tick);
    };

    const ro = new ResizeObserver(resize);
    ro.observe(canvas);
    resize();
    rafRef.current = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(rafRef.current);
      ro.disconnect();
    };
  }, [enabled, preset, intensity]);

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
