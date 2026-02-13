// clientapp/src/components/bgfx/BgFxCanvas.jsx
// Canvas-слой фоновых эффектов (туман / пыль+кометы / нейронные связи)
// Цвета синхронизированы с темой через CSS vars (--accent/--accent2/--accent3).

import React, { useEffect, useMemo, useRef } from 'react';

function parseRgbVar(v) {
  // ожидаем формат "R G B" (как в проекте)
  const parts = String(v || '')
    .trim()
    .split(/[\s,\/]+/)
    .filter(Boolean)
    .map((n) => Number(n));
  if (parts.length >= 3 && parts.every((n) => Number.isFinite(n))) return parts.slice(0, 3);
  return [255, 255, 255];
}

function readThemeColors() {
  const cs = getComputedStyle(document.documentElement);
  const a1 = parseRgbVar(cs.getPropertyValue('--accent'));
  const a2 = parseRgbVar(cs.getPropertyValue('--accent2'));
  const a3 = parseRgbVar(cs.getPropertyValue('--accent3'));
  return { a1, a2, a3 };
}

function rgba([r, g, b], alpha) {
  return `rgba(${r},${g},${b},${alpha})`;
}

export default function BgFxCanvas({ enabled, variant }) {
  const canvasRef = useRef(null);
  const reduceMotion = useMemo(() => {
    if (typeof window === 'undefined') return false;
    return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    // запускаем для режимов 0..3
    const v = String(variant);
    const shouldRun = enabled && !reduceMotion && (v === '0' || v === '1' || v === '2' || v === '3');
    canvas.style.display = shouldRun ? 'block' : 'none';
    if (!shouldRun) return;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    let raf = 0;
    let last = performance.now();
    let w = 0;
    let h = 0;
    const dpr = Math.max(1, Math.min(2, window.devicePixelRatio || 1));

    const resize = () => {
      w = Math.max(1, window.innerWidth);
      h = Math.max(1, window.innerHeight);
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();
    window.addEventListener('resize', resize);

    // ===== состояние для режимов =====
    const colors = readThemeColors();

    // Fog: большие "пухи" + лёгкий шум (через дриф...)
    const fogPuffs = Array.from({ length: 14 }, () => {
      const baseR = Math.min(w, h) * (0.18 + Math.random() * 0.22);
      return {
        x: Math.random() * w,
        y: Math.random() * h,
        r: baseR,
        vx: (Math.random() - 0.5) * 0.07,
        vy: (Math.random() - 0.5) * 0.07,
        t: Math.random() * Math.PI * 2,
      };
    });

    // Dust + comets
    const dust = Array.from({ length: 220 }, () => {
      return {
        x: Math.random() * w,
        y: Math.random() * h,
        r: 0.7 + Math.random() * 1.8,
        s: 0.12 + Math.random() * 0.45,
        a: 0.08 + Math.random() * 0.20,
        c: Math.random() < 0.5 ? 0 : Math.random() < 0.75 ? 1 : 2,
      };
    });
    const comets = [];
    const spawnComet = () => {
      const side = Math.floor(Math.random() * 4);
      const speed = 2.5 + Math.random() * 2.2;
      let x = 0,
        y = 0,
        vx = 0,
        vy = 0;
      if (side === 0) {
        x = -50;
        y = Math.random() * h;
        vx = speed;
        vy = (Math.random() - 0.5) * 0.8;
      } else if (side === 1) {
        x = w + 50;
        y = Math.random() * h;
        vx = -speed;
        vy = (Math.random() - 0.5) * 0.8;
      } else if (side === 2) {
        x = Math.random() * w;
        y = -50;
        vx = (Math.random() - 0.5) * 0.8;
        vy = speed;
      } else {
        x = Math.random() * w;
        y = h + 50;
        vx = (Math.random() - 0.5) * 0.8;
        vy = -speed;
      }
      comets.push({ x, y, vx, vy, life: 0, max: 180 + Math.random() * 80, c: Math.random() < 0.5 ? 0 : 1 });
    };

    // Neural net
    const nodes = Array.from({ length: 38 }, () => {
      return {
        x: Math.random() * w,
        y: Math.random() * h,
        vx: (Math.random() - 0.5) * 0.18,
        vy: (Math.random() - 0.5) * 0.18,
        r: 1.4 + Math.random() * 1.4,
      };
    });

    const drawFog = (dt) => {
      // лёгкий фейд, чтобы фон был "живой", но не мерцал
      ctx.fillStyle = 'rgba(0,0,0,0.08)';
      ctx.fillRect(0, 0, w, h);
      ctx.save();
      ctx.globalCompositeOperation = 'screen';
      ctx.filter = 'blur(24px)';

      for (let i = 0; i < fogPuffs.length; i++) {
        const p = fogPuffs[i];
        p.t += dt * 0.00035;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        // мягкая "дышащая" пульсация
        const rr = p.r * (0.92 + 0.12 * Math.sin(p.t));
        if (p.x < -rr) p.x = w + rr;
        if (p.x > w + rr) p.x = -rr;
        if (p.y < -rr) p.y = h + rr;
        if (p.y > h + rr) p.y = -rr;

        const col = i % 3 === 0 ? colors.a1 : i % 3 === 1 ? colors.a2 : colors.a3;
        const g = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, rr);
        g.addColorStop(0, rgba(col, 0.16));
        g.addColorStop(0.55, rgba(col, 0.07));
        g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(p.x, p.y, rr, 0, Math.PI * 2);
        ctx.fill();
      }

      ctx.restore();

      // поверх — микро-пыль (туманная взвесь)
      ctx.save();
      ctx.globalCompositeOperation = 'lighter';
      for (let i = 0; i < 160; i++) {
        const x = (i * 997) % w;
        const y = ((i * 619) + performance.now() * 0.02) % h;
        ctx.fillStyle = 'rgba(255,255,255,0.015)';
        ctx.fillRect(x, y, 1, 1);
      }
      ctx.restore();
    };

    const drawDustComets = (dt) => {
      ctx.clearRect(0, 0, w, h);

      // пыль
      ctx.save();
      ctx.globalCompositeOperation = 'screen';
      for (const p of dust) {
        p.y += p.s * dt;
        p.x += Math.sin((p.y + p.x) * 0.002) * 0.03 * dt;
        if (p.y > h + 6) p.y = -6;
        if (p.x < -10) p.x = w + 10;
        if (p.x > w + 10) p.x = -10;
        const col = p.c === 0 ? colors.a1 : p.c === 1 ? colors.a2 : colors.a3;
        ctx.fillStyle = rgba(col, p.a);
        ctx.beginPath();
        ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.restore();

      // кометы (редко)
      if (Math.random() < 0.012) spawnComet();

      ctx.save();
      ctx.globalCompositeOperation = 'lighter';
      for (let i = comets.length - 1; i >= 0; i--) {
        const c = comets[i];
        c.life += dt;
        c.x += c.vx * dt * 0.06;
        c.y += c.vy * dt * 0.06;
        const t = Math.min(1, c.life / 350);
        const alpha = 0.55 * (1 - t);
        const col = c.c === 0 ? colors.a1 : colors.a2;

        // хвост
        const tail = 120;
        const tx = c.x - c.vx * tail;
        const ty = c.y - c.vy * tail;
        const grad = ctx.createLinearGradient(c.x, c.y, tx, ty);
        grad.addColorStop(0, rgba(col, alpha));
        grad.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.strokeStyle = grad;
        ctx.lineWidth = 2.2;
        ctx.beginPath();
        ctx.moveTo(c.x, c.y);
        ctx.lineTo(tx, ty);
        ctx.stroke();

        // головка
        const g = ctx.createRadialGradient(c.x, c.y, 0, c.x, c.y, 22);
        g.addColorStop(0, rgba(col, Math.min(0.85, alpha + 0.25)));
        g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(c.x, c.y, 22, 0, Math.PI * 2);
        ctx.fill();

        if (c.life > c.max || c.x < -200 || c.x > w + 200 || c.y < -200 || c.y > h + 200) {
          comets.splice(i, 1);
        }
      }
      ctx.restore();
    };

    const drawNeural = (dt) => {
      ctx.clearRect(0, 0, w, h);
      for (const n of nodes) {
        n.x += n.vx * dt;
        n.y += n.vy * dt;
        if (n.x < 0) n.x = w;
        if (n.x > w) n.x = 0;
        if (n.y < 0) n.y = h;
        if (n.y > h) n.y = 0;
      }

      const maxDist = Math.min(w, h) * 0.22;
      ctx.save();
      ctx.globalCompositeOperation = 'screen';
      ctx.lineWidth = 1;

      // связи
      for (let i = 0; i < nodes.length; i++) {
        for (let j = i + 1; j < nodes.length; j++) {
          const a = nodes[i];
          const b = nodes[j];
          const dx = a.x - b.x;
          const dy = a.y - b.y;
          const d = Math.hypot(dx, dy);
          if (d < maxDist) {
            const k = 1 - d / maxDist;
            const col = i % 3 === 0 ? colors.a1 : i % 3 === 1 ? colors.a2 : colors.a3;
            ctx.strokeStyle = rgba(col, 0.10 + 0.18 * k);
            ctx.beginPath();
            ctx.moveTo(a.x, a.y);
            ctx.lineTo(b.x, b.y);
            ctx.stroke();
          }
        }
      }

      // узлы
      for (let i = 0; i < nodes.length; i++) {
        const n = nodes[i];
        const col = i % 3 === 0 ? colors.a1 : i % 3 === 1 ? colors.a2 : colors.a3;
        const g = ctx.createRadialGradient(n.x, n.y, 0, n.x, n.y, 18);
        g.addColorStop(0, rgba(col, 0.32));
        g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(n.x, n.y, 18, 0, Math.PI * 2);
        ctx.fill();

        ctx.fillStyle = rgba(col, 0.35);
        ctx.beginPath();
        ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
        ctx.fill();
      }

      ctx.restore();
    };

    // Aurora blobs: самый заметный режим для пользователей.
    // Мягкие большие шары света, двигаются медленно и "дышат".
    const aurora = Array.from({ length: 7 }, (_, i) => {
      const baseR = Math.min(w, h) * (0.26 + Math.random() * 0.18);
      return {
        x: Math.random() * w,
        y: Math.random() * h,
        r: baseR,
        vx: (Math.random() - 0.5) * 0.06,
        vy: (Math.random() - 0.5) * 0.06,
        t: Math.random() * Math.PI * 2,
        c: i % 3,
      };
    });

    const drawAurora = (dt) => {
      // слегка чистим, чтобы не было "грязного" хвоста
      ctx.fillStyle = 'rgba(0,0,0,0.22)';
      ctx.fillRect(0, 0, w, h);

      ctx.save();
      ctx.globalCompositeOperation = 'screen';
      ctx.filter = 'blur(56px)';

      for (const p of aurora) {
        p.t += dt * 0.00028;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        const rr = p.r * (0.90 + 0.14 * Math.sin(p.t));

        if (p.x < -rr) p.x = w + rr;
        if (p.x > w + rr) p.x = -rr;
        if (p.y < -rr) p.y = h + rr;
        if (p.y > h + rr) p.y = -rr;

        const col = p.c === 0 ? colors.a1 : p.c === 1 ? colors.a2 : colors.a3;
        const g = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, rr);
        g.addColorStop(0, rgba(col, 0.22));
        g.addColorStop(0.55, rgba(col, 0.10));
        g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(p.x, p.y, rr, 0, Math.PI * 2);
        ctx.fill();
      }

      ctx.restore();
    };

    const tick = (now) => {
      const dt = now - last;
      last = now;

      // если тема изменилась — обновим цвета раз в секунду (дёшево)
      if (Math.floor(now / 1000) !== Math.floor((now - dt) / 1000)) {
        const c = readThemeColors();
        colors.a1 = c.a1;
        colors.a2 = c.a2;
        colors.a3 = c.a3;
      }

      if (v === '0') drawFog(dt);
      else if (v === '1') drawDustComets(dt);
      else if (v === '2') drawNeural(dt);
      else drawAurora(dt);

      raf = requestAnimationFrame(tick);
    };

    // старт: заполняем чёрным, иначе холст будет прозрачным
    if (v === '0' || v === '3') {
      ctx.fillStyle = 'rgba(0,0,0,1)';
      ctx.fillRect(0, 0, w, h);
    }
    raf = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener('resize', resize);
    };
  }, [enabled, variant, reduceMotion]);

  return <canvas ref={canvasRef} className="bgfx-canvas" aria-hidden="true" />;
}
