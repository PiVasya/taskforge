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
      pulses: [],       // для нейросвязей
      pulseTimer: 0,    // таймер создания импульсов
    };

    // Pointer tracking для нейросвязей (интерактивность)
    const pointer = { x: w/2, y: h/2, vx: 0, vy: 0, down: false, has: false };
    
    const setPointer = (px, py) => {
      pointer.has = true;
      const rect = canvas.getBoundingClientRect();
      const nx = (px - rect.left) * dpr;
      const ny = (py - rect.top) * dpr;
      pointer.vx = nx - pointer.x;
      pointer.vy = ny - pointer.y;
      pointer.x = nx;
      pointer.y = ny;
    };
    
    const handleMouseMove = (e) => {
      setPointer(e.clientX, e.clientY);
    };
    const handleMouseDown = () => { pointer.down = true; };
    const handleMouseUp = () => { pointer.down = false; };
    
    const handleTouchStart = (e) => {
      pointer.down = true;
      if (e.touches[0]) {
        setPointer(e.touches[0].clientX, e.touches[0].clientY);
      }
    };
    const handleTouchMove = (e) => {
      if (e.touches[0]) {
        setPointer(e.touches[0].clientX, e.touches[0].clientY);
      }
    };
    const handleTouchEnd = () => { pointer.down = false; };
    
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

      // 2: Нейросвязи (Neural Beauty) — полная реализация по примеру Hello.html
      if (preset === 2) {
        const TAU = Math.PI * 2;
        
        // Фон с виньеткой
        ctx.save();
        ctx.fillStyle = isDarkTheme() ? 'rgba(5,6,10,0.95)' : 'rgba(248,250,252,0.95)';
        ctx.fillRect(0, 0, w, h);
        
        // Виньетка (мягкое свечение от центра)
        const gx = ctx.createRadialGradient(w*0.5, h*0.55, 0, w*0.5, h*0.55, Math.max(w,h)*0.75);
        if (isDarkTheme()) {
          gx.addColorStop(0, 'rgba(35,50,120,0.18)');
          gx.addColorStop(0.35, 'rgba(20,30,80,0.10)');
          gx.addColorStop(1, 'rgba(0,0,0,0)');
        } else {
          gx.addColorStop(0, 'rgba(200,220,255,0.12)');
          gx.addColorStop(0.35, 'rgba(180,200,240,0.06)');
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
      // Отписываемся от событий
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mousedown', handleMouseDown);
      window.removeEventListener('mouseup', handleMouseUp);
      window.removeEventListener('touchstart', handleTouchStart);
      window.removeEventListener('touchmove', handleTouchMove);
      window.removeEventListener('touchend', handleTouchEnd);
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
