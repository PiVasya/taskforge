import React, { useEffect, useMemo, useRef } from 'react';
import BgFxNeuralWebgl from './BgFxNeuralWebgl';
import BgFxSolarWebgl from './BgFxSolarWebgl';












function cssVar(name, fallback) {
  if (typeof window === 'undefined') return fallback;
  const v = getComputedStyle(document.documentElement).getPropertyValue(name);
  return (v || fallback || '').trim();
}

function parseRgbTriplet(s, fallback = [255, 255, 255]) {
  
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

export default function BgFxCanvas({ enabled, variant, intensity = 1, paletteKey = 'default' }) {
  const canvasRef = useRef(null);
  const rafRef = useRef(0);

  const preset = useMemo(() => {
    if (variant === 'random') {
      
      return Math.floor(Math.random() * 9);
    }
    const v = Number(variant);
    return Number.isFinite(v) ? v : 0;
  }, [variant]);

  useEffect(() => {
    if (!enabled || preset === 2 || preset === 8) return;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d', { alpha: true });
    if (!ctx) return;

    
    const isDarkTheme = () => document.documentElement.classList.contains('dark');
    const alphaBoost = () => (isDarkTheme() ? 1.0 : 1.8);

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
      pulses: [],       
      pulseTimer: 0,    
      matrix: [],       
      honey: {
        phaseA: rand(0, 9999),
        phaseB: rand(0, 9999),
        
        driftA: rand(-0.00008, 0.00008),
        driftB: rand(-0.00008, 0.00008),
        nextJitter: rand(6, 14),
        grain: null,
      },

      smoke: {
        
        NX: 0, NY: 0, N: 0,
        u: null, v: null, u0: null, v0: null,
        dens: null, dens0: null,
        p: null, div: null,
        curl: null, curlAbs: null, fx: null, fy: null,
        small: null, sctx: null, imgData: null, imgArr: null,
        _fpsAcc: 0,
      },
    };

    
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
    } catch (e) {
      
    }

    
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

    
    const pointer = { x: w/2, y: h/2, vx: 0, vy: 0, down: false, rdown: false, has: false };
    
    const setPointer = (px, py) => {
      pointer.has = true;
      
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
      if (preset === 6) dpr = 1;
      if (preset === 7) dpr = 1;
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

      
      state.nodes = [];
      state.dust = [];
      state.blobs = [];
      state.aurora = [];
      state.hearts = [];

      
      state.matrix = [];

      
      state.honey.hexR = 0;
      state.honey.hexPts = null;
      state.honey._fpsAcc = 0;
      state.honey._fpsNow = 0;

      state.smoke._fpsAcc = 0;

      
      const area = w * h;

      
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
          
          c: hueIdx === 0 ? fx1 : hueIdx === 1 ? fx2 : fx3,
          core: rand(0.65, 1.0),      
          wob: rand(0, Math.PI * 2),  
          wobSp: rand(0.002, 0.01),   
          mass: rand(0.5, 1.6) * (1/s),
        });
      }
      
      state.pulses = [];
      state.pulseTimer = 0;

      
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
        } catch (e) {
          
        }

        
        if (!pointer.has) {
          pointer.has = true;
          pointer.x = (w * 0.5) * dpr;
          pointer.y = (h * 0.55) * dpr;
        }
      }

    };

    
    const TAU = Math.PI * 2;

    const step = (dt) => {
      state.t += dt;

      
      ctx.clearRect(0, 0, w, h);

      
      if (preset === 0) {
        ctx.save();
        
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';
        ctx.filter = 'blur(40px)';
        for (const b of state.blobs) {
          b.x += b.vx * (dt * 60);
          b.y += b.vy * (dt * 60);
          
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

      
      if (preset === 5) {
        
        const chars = 'アイウエオカキクケコサシスセソタチツテト0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ$@%#&*+=<>[]{}|';
        
        ctx.save();
        
        ctx.fillStyle = isDarkTheme() ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.08)';
        ctx.fillRect(0, 0, w, h);
        
        ctx.font = '14px monospace';
        ctx.globalCompositeOperation = isDarkTheme() ? 'lighter' : 'multiply';
        
        for (const drop of state.matrix) {
          
          drop.y += drop.speed * (dt * 60);
          
          
          if (drop.y > h + drop.length * 16) {
            drop.y = rand(-h * 0.5, 0);
            drop.x = Math.floor(rand(0, Math.floor(w / 18))) * 18 + rand(-4, 4);
            drop.speed = rand(0.4, 1.2);
            drop.length = Math.floor(rand(8, 25));
          }
          
          
          for (let i = 0; i < drop.length; i += 1) {
            const y = drop.y - i * 16;
            if (y < 0 || y > h) continue;
            
            
            const alpha = (1 - i / drop.length) * 0.8;
            
            
            if (i === 0) {
              ctx.fillStyle = isDarkTheme() 
                ? `rgba(${fg[0]}, ${fg[1]}, ${fg[2]}, ${alpha * 1.2})`
                : `rgba(${fx1[0]}, ${fx1[1]}, ${fx1[2]}, ${alpha * 0.9})`;
            } else {
              
              const [r, g, b] = isDarkTheme() ? [100, 255, 150] : fx1;
              ctx.fillStyle = `rgba(${r}, ${g}, ${b}, ${alpha * (isDarkTheme() ? 0.85 : 0.6)})`;
            }
            
            
            const char = chars[Math.floor(Math.random() * chars.length)];
            ctx.fillText(char, drop.x, y);
          }
        }
        
        ctx.restore();
        return;
      }

      
      if (preset === 6) {
        const now = state.t;

        
        
        

        state.honey.nextJitter -= dt;
        if (state.honey.nextJitter <= 0) {
          state.honey.nextJitter = rand(10, 18);
          state.honey.driftA = rand(-0.00003, 0.00003);
          state.honey.phaseA = rand(0, 9999);
        }
        state.honey.phaseA += state.honey.driftA * dt;

        const [hue] = rgbToHsl(fx1);

        ctx.save();

        
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

        
        const px = pointer.has ? (pointer.x - w * 0.5) : 0;
        const py = pointer.has ? (pointer.y - h * 0.5) : 0;
        const ox = -px * 0.02;
        const oy = -py * 0.02;

        
        
        const r = isDarkTheme()
          ? clamp(Math.sqrt(w * h) / 26, 30, 60)
          : clamp(Math.sqrt(w * h) / 18, 60, 120);
        const ww = Math.sqrt(3) * r;
        const hh = 2 * r;
        const rowStep = 1.5 * r;

        
        if (!state.honey.hexPts || state.honey.hexR !== r) {
          state.honey.hexR = r;
          const pts = [];
          for (let i = 0; i < 6; i += 1) {
            const a = TAU * (i / 6) + Math.PI / 6; 
            pts.push({ x: Math.cos(a) * (r * 0.94), y: Math.sin(a) * (r * 0.94) });
          }
          state.honey.hexPts = pts;
        }

        
        
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

            
            const wave = Math.sin((hx * 0.014 + hy * 0.011) - now * 0.00018 + state.honey.phaseA) * 0.5 + 0.5;

            
            const dx = hx - cx;
            const dy = hy - cy;
            const d2 = dx * dx + dy * dy;
            
            
            const ring = pointer.has ? (1 / (1 + d2 / falloff2)) : 0.12;

            
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
    
    const arr = sm.imgArr;
    for (let j = 0; j < NY; j++) {
      for (let i = 0; i < NX; i++) {
        const id = idx(i, j);
        const d = clamp(sm.dens[id], 0, 1.35);

        
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

  
  const VISC = 0.00012;
  const DIFF = 0.00007;
  const DISSIP = 0.9935;
  const VEL_DAMP = 0.995;
  const VORTICITY = 32.0 * (0.7 + intensity * 0.7);

  const dt2 = clamp(dt, 0.001, 0.03);

  
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
      
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mousedown', handleMouseDown);
      window.removeEventListener('mouseup', handleMouseUp);
      window.removeEventListener('touchstart', handleTouchStart);
      window.removeEventListener('touchmove', handleTouchMove);
      window.removeEventListener('touchend', handleTouchEnd);
    };
  }, [enabled, preset, intensity, paletteKey]);

  if (!enabled) return null;

  if (preset === 2) {
    return <BgFxNeuralWebgl enabled={enabled} intensity={intensity} paletteKey={paletteKey} />;
  }

  if (preset === 8) {
    return <BgFxSolarWebgl enabled={enabled} intensity={intensity} paletteKey={paletteKey} />;
  }

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
        
        opacity: 1,
      }}
    />
  );
}
