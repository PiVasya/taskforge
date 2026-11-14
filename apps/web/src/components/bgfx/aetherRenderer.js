// Pure WebGL renderer; the React component owns its lifecycle.
// Colors come from the same CSS theme tokens used by the other background effects.

export const AETHER_PRESET = Object.freeze({
  palette: 'theme',
  reactivity: 28,
  density: 100,
  bloom: 100,
  waves: 100,
});

function mixRgb(a, b, amount) {
  return a.map((value, i) => value + (b[i] - value) * amount);
}

function lighten(rgb, amount) { return mixRgb(rgb, [255, 255, 255], amount); }
function darken(rgb, amount) { return mixRgb(rgb, [0, 0, 0], amount); }
function rgb01(rgb) { return rgb.map((value) => Math.max(0, Math.min(1, value / 255))); }

// On a pale surface luminous accent colors disappear. In light mode retain
// their hue but give every particle/filament a minimum contrast against the
// actual AETHER sky. This is deliberately not applied to dark-mode colors.
function luminance(rgb) {
  const [r, g, b] = rgb.map((value) => {
    const s = Math.max(0, Math.min(1, value / 255));
    return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function inkWithContrast(rgb, background, minimumRatio) {
  const sky = luminance(background);
  const contrast = (color) => (sky + 0.05) / (luminance(color) + 0.05);
  if (contrast(rgb) >= minimumRatio) return rgb;
  let lo = 0;
  let hi = 1;
  // Binary search for the least darkening that still passes the threshold.
  for (let i = 0; i < 14; i += 1) {
    const mid = (lo + hi) / 2;
    if (contrast(darken(rgb, mid)) >= minimumRatio) hi = mid;
    else lo = mid;
  }
  return darken(rgb, hi);
}

function cssRgb(styles, name, fallback) {
  const source = styles?.getPropertyValue?.(name) || '';
  const values = source.replace(/,/g, ' ').trim().split(/\s+/).map(Number);
  return values.length >= 3 && values.slice(0, 3).every((x) => Number.isFinite(x) && x >= 0 && x <= 255)
    ? values.slice(0, 3) : fallback;
}

export function makeAetherThemePalette({
  fx1 = [14, 165, 233], fx2 = [34, 211, 238], fx3 = [167, 139, 250],
  page = [15, 23, 42], card = [15, 23, 42], fg = [203, 213, 225], dark = true, neo = false,
} = {}) {
  // Keep the original dark-mode palette pixel-for-pixel unchanged.
  // Neo-brutal intentionally uses the SAME paper/ink backgrounds in every
  // color theme. A tiny 4-5% tint made the full-screen field look blue/gray
  // for all six themes. Only AETHER gets a richer ambient tint; the actual
  // neo-brutal CSS surfaces, buttons and all other effects stay untouched.
  const backgroundA = neo
    ? (dark
      ? darken(mixRgb(page, mixRgb(fx1, fx2, .14), .32), .55)
      : mixRgb(page, fx1, .18))
    : (dark ? darken(mixRgb(page, fx1, .045), .55) : mixRgb(page, fx1, .025));
  const backgroundB = neo
    ? (dark
      ? darken(mixRgb(card, mixRgb(fx1, fx2, .50), .30), .52)
      : mixRgb(card, mixRgb(fx1, fx2, .30), .15))
    : (dark ? darken(mixRgb(card, fx2, .055), .48) : mixRgb(card, fx2, .032));
  // The lighter of the two sky shades is not the problem: use the darker
  // one so accents remain legible across the whole viewport.
  const inkBackdrop = luminance(backgroundA) < luminance(backgroundB) ? backgroundA : backgroundB;
  const primary = dark ? lighten(fx1, .20)
    : inkWithContrast(darken(fx1, .22), inkBackdrop, 3.25);
  const secondary = dark ? lighten(mixRgb(fx2, fx3, .32), .16)
    : inkWithContrast(darken(mixRgb(fx2, fx3, .32), .30), inkBackdrop, 3.15);
  const tertiary = dark ? lighten(mixRgb(fx3, fg, .34), .12)
    : inkWithContrast(darken(mixRgb(fx3, fx1, .26), .34), inkBackdrop, 3.15);
  const star = dark ? lighten(mixRgb(fg, fx2, .38), .08)
    : inkWithContrast(darken(mixRgb(fg, fx1, .65), .20), inkBackdrop, 4.10);
  return {
    a: rgb01(primary), b: rgb01(secondary), c: rgb01(tertiary),
    backgroundA: rgb01(backgroundA), backgroundB: rgb01(backgroundB),
    star: rgb01(star), dark, neo,
  };
}

export function readAetherThemePalette() {
  const root = typeof document !== 'undefined' ? document.documentElement : null;
  const isDark = root?.classList?.contains?.('dark') ?? true;
  const styles = root && typeof getComputedStyle === 'function' ? getComputedStyle(root) : null;
  return makeAetherThemePalette({
    fx1: cssRgb(styles, '--fx-1', [14, 165, 233]),
    fx2: cssRgb(styles, '--fx-2', [34, 211, 238]),
    fx3: cssRgb(styles, '--fx-3', [167, 139, 250]),
    page: cssRgb(styles, '--page-bg', isDark ? [15, 23, 42] : [248, 250, 252]),
    card: cssRgb(styles, '--card', isDark ? [15, 23, 42] : [255, 255, 255]),
    fg: cssRgb(styles, '--fg', isDark ? [203, 213, 225] : [100, 116, 139]),
    dark: isDark,
    neo: root?.classList?.contains?.('neo-brutal') ?? false,
  });
}

export function createAetherRenderer(canvas, { onFailure, onRestore } = {}) {
  const gl = canvas.getContext('webgl2', {
    antialias: false,
    alpha: false,
    premultipliedAlpha: false,
    powerPreference: 'high-performance',
  }) || canvas.getContext('webgl', {
    antialias: false,
    alpha: false,
    premultipliedAlpha: false,
    powerPreference: 'high-performance',
  });

  if (!gl) {
    onFailure?.('WebGL недоступен');
    return () => {};
  }

const vertexQuad = `
    attribute vec2 aPosition;
    varying vec2 vUv;
    void main(){ vUv=aPosition*.5+.5; gl_Position=vec4(aPosition,0.,1.); }
  `;

const fragmentSpace = `
    precision highp float;
    varying vec2 vUv;
    uniform vec2 uResolution, uMouse;
    uniform float uTime, uWave, uPresence, uEnergy, uPulse;
    uniform vec3 uColorA, uColorB, uColorC, uBgA, uBgB, uStarColor;
    uniform float uDark;
    float hash(vec2 p) {
      vec3 v=fract(vec3(p.x,p.y,p.x)*.1031);
      v+=dot(v,v.yzx+33.33);
      return fract((v.x+v.y)*v.z);
    }
    float noise2(vec2 p) {
      vec2 i=floor(p),f=fract(p);
      f=f*f*(3.-2.*f);
      return mix(mix(hash(i),hash(i+vec2(1.,0.)),f.x),
                 mix(hash(i+vec2(0.,1.)),hash(i+vec2(1.,1.)),f.x),f.y);
    }
    float smoky(vec2 p) {
      return noise2(p)*.68 + noise2(p*2.12+4.3)*.32;
    }
    void main() {
      float aspect=uResolution.x/uResolution.y;
      vec2 p=(vUv-.5)*vec2(aspect,1.)*4.06;
      float time=uTime;
      // No sphere-ray intersection, radial contour or circular mask.
      vec3 color=mix(uBgA,uBgB,vUv.y*.56+.22);
      float grain=noise2(vUv*uResolution*.18);
      color+=vec3(.003,.004,.009)*grain*uDark;
      vec2 atLeft=p-vec2(-3.0,1.5), atRight=p-vec2(2.9,.9);
      color+=uColorA*.0068*exp(-dot(atLeft,atLeft)*.12)*uDark;
      color+=uColorB*.0100*exp(-dot(atRight,atRight)*.11)*uDark;
      // Broad, dark, translucent 3D-like ribbons through the entire viewport.
      // They remain low contrast while particles carry the sparkle.
      vec3 atmosphere=vec3(0.);
      for(int i=0;i<5;i++){
        float k=float(i);
        float depth=k*.37;
        vec2 q=p*(.78+depth*.18);
        float wind=uTime*(.065+depth*.045);
        float wave1=.31*sin(q.x*(.66+.07*k)+wind+depth*2.0);
        float wave2=.18*sin(q.x*1.48-wind*.83+depth*3.2);
        float wave3=.10*sin(q.x*2.7+wind*.37+k*1.7);
        float center=(k-2.)*.59 +wave1+wave2+wave3;
        float d=q.y-center;
        float band=exp(-d*d*(2.4+depth*.6));
        float vein=pow(.5+.5*sin(q.x*3.75+q.y*3.05+depth*6.1-wind*1.8),3.);
        float smoke=smoky(q*vec2(.67,1.25)+vec2(wind*.34,depth*4.7));
        float haze=(.23+.47*smoke+.22*vein)*band;
        float folded=exp(-pow(d*9.4+1.45*sin(q.x*2.2-depth*4.0+wind),2.))*.15;
        vec3 tint=mix(uColorA,uColorB,.2+.63*(k/4.));
        atmosphere+=tint*(haze+folded)*(.39-.038*k);
      }
      // An atmospheric field, not the surface of a drawn planet.
      if(uDark>.5) {
        color += atmosphere*uWave*.255;
      } else {
        // Pale screens need ink-like translucent waves, not a 2% tint.
        // The mask is smoothly driven by the existing volumetric field.
        float haze=clamp(dot(atmosphere,vec3(.333))*.78*uWave,0.,.26);
        color=mix(color,mix(uColorA,uColorB,.42),haze);
      }
      // Faint independent star dust all over the screen, independent of waves.
      vec2 starCell=floor(vUv*uResolution/19.);
      vec2 starLocal=fract(vUv*uResolution/19.);
      float s=hash(starCell);
      vec2 starPos=vec2(hash(starCell+11.9),hash(starCell+28.7));
      float tiny=1.-smoothstep(.01,.075,length(starLocal-starPos));
      float stardust=tiny*step(.984,s)*(.24+.37*hash(starCell+17.));
      if(uDark>.5) color+=uStarColor*stardust*.23;
      else color=mix(color,uStarColor,stardust*.62);
      // Soft, short-lived cursor energy. Crucially no cutout or empty spot.
      float radius=length((p-uMouse));
      float interaction=exp(-radius*radius*1.55)*uPresence;
      if(uDark>.5) color+=uColorC*interaction*(.005+.008*uEnergy+.005*uPulse);
      else color=mix(color,uColorC,interaction*(.028+.018*uEnergy+.015*uPulse));
      gl_FragColor=vec4(clamp(color,0.,1.),1.);
    }
  `;

const vertexCloud = `
    precision highp float;
    attribute vec3 aPos;
    attribute vec2 aMeta;
    uniform float uTime, uAspect, uDpr, uPresence, uEnergy, uPulse, uReactivity, uDark;
    uniform vec2 uMouse, uView;
    varying float vAlpha, vSeed, vDepth, vKind, vField;
    mat2 rot(float a){float c=cos(a),s=sin(a);return mat2(c,-s,s,c);}
    void main(){
      float seed=aMeta.x, kind=aMeta.y;
      vec3 p=aPos;
      float depth=clamp((p.z+2.5)/5.0,0.,1.);
      // Continuous particle field in depth, not a spherical shell.
      float drift=.016+.028*depth;
      p.x+=drift*sin(uTime*.23+p.y*1.64+seed*5.4);
      p.y+=drift*sin(uTime*.18+p.x*.97+seed*4.2);
      p.z+=.095*sin(uTime*.25+p.x*.65+seed*4.2);
      // Perspective/parallax from dragging: near stars shift more than far stars.
      p.xy+=uView*(.13+.37*depth);
      p.x+=p.z*uView.x*.115;
      p.y+=p.z*uView.y*.087;
      vec2 delta=p.xy-uMouse;
      float dist2=dot(delta,delta);
      float influence=exp(-dist2*1.35)*uPresence*uReactivity;
      // Swirl is tangential. It never pushes everything out of the cursor's center.
      float twist=influence*(.24+.22*uEnergy+.18*uPulse)*(kind<1.5?1.:.46);
      p.xy=uMouse+rot(twist)*delta;
      p.z+=influence*(.14+.07*uPulse);
      // Perspective across several depth planes.
      float perspective=3.25/(3.45-p.z*.20);
      vec2 screen=p.xy*perspective;
      gl_Position=vec4(screen/vec2(uAspect*2.02,2.02),clamp(p.z*.035,-.5,.5),1.);
      depth=clamp((p.z+2.5)/5.0,0.,1.);
      vKind=kind;vSeed=seed;vDepth=depth;vField=influence;
      float ribbon=step(.5,kind)*(1.-step(1.5,kind));
      float stellar=step(1.5,kind);
      vAlpha=mix(.20+.25*depth, .31+.35*depth, ribbon);
      vAlpha=mix(vAlpha,.43+.39*depth,stellar);
      vAlpha*=.55+.45*pow(.5+.5*sin(p.y*1.45+p.x*.55+seed*6.8),1.7);
      // Slightly denser ink points on pale screens; dark mode is unchanged.
      vAlpha*=mix(1.18,1.,uDark);
      vAlpha+=influence*(.16+.13*uEnergy);
      float size=mix(.9+1.3*depth,1.15+1.75*depth,ribbon);
      size=mix(size,1.55+2.6*depth,stellar);
      gl_PointSize=min(9.,uDpr*size*(.86+.24*perspective)*(1.+influence*.45)*mix(1.28,1.,uDark));
    }
  `;

const fragmentCloud = `
    precision mediump float;
    varying float vAlpha,vSeed,vDepth,vKind,vField;
    uniform vec3 uColorA,uColorB,uColorC,uStarColor;
    uniform highp float uDark;
    void main(){
      vec2 p=gl_PointCoord*2.-1.;
      float r2=dot(p,p);
      if(r2>1.)discard;
      float hot=pow(1.-r2,3.3);
      float glow=exp(-r2*3.85);
      float alpha=vAlpha*(.18*glow+.90*hot)*mix(.98,1.,uDark);
      vec3 col=mix(uColorA,uColorB,smoothstep(.07,.93,vSeed));
      col=mix(col,uColorC,vDepth*.23+vField*.16);
      col=mix(col,uStarColor,smoothstep(.85,1.,vDepth)*.23);
      if(vKind>1.5)col=mix(col,uStarColor,.37);
      gl_FragColor=vec4(col,alpha);
    }
  `;

const fragmentStreaks = `
    precision mediump float;
    varying float vAlpha,vSeed,vDepth,vKind,vField;
    uniform vec3 uColorA,uColorB,uColorC;
    uniform highp float uDark;
    void main(){
      vec3 col=mix(uColorA,uColorB,vSeed);
      col=mix(col,uColorC,.22);
      gl_FragColor=vec4(col,vAlpha*mix(.26,.105,uDark)*(.45+.55*vDepth));
    }
  `;

const fragmentPost = `
    precision highp float;
    varying vec2 vUv;
    uniform sampler2D uScene;
    uniform vec2 uResolution;
    uniform float uBloom,uDark;
    vec3 read(vec2 uv){return texture2D(uScene,clamp(uv,0.,1.)).rgb;}
    vec3 bright(vec3 p){return max(p-vec3(.16),vec3(0.));}
    void main(){
      vec3 col=read(vUv);
      // Preserve the actual page background in light mode; dark-space bloom
      // would otherwise turn white backgrounds muddy grey.
      if(uDark<.5){
        gl_FragColor=vec4(clamp(col,0.,1.),1.);
        return;
      }
      vec2 px=1./uResolution;
      vec3 bloom=vec3(0.);
      bloom+=bright(read(vUv+vec2(px.x*4.,0.)));
      bloom+=bright(read(vUv-vec2(px.x*4.,0.)));
      bloom+=bright(read(vUv+vec2(0.,px.y*4.)));
      bloom+=bright(read(vUv-vec2(0.,px.y*4.)));
      bloom+=bright(read(vUv+vec2(px.x*11.,px.y*8.)))*.75;
      bloom+=bright(read(vUv-vec2(px.x*11.,px.y*8.)))*.75;
      bloom+=bright(read(vUv+vec2(px.x*11.,-px.y*8.)))*.75;
      bloom+=bright(read(vUv+vec2(-px.x*11.,px.y*8.)))*.75;
      col+=bloom*uBloom*.09;
      col=1.-exp(-col*1.2);
      col=pow(col,vec3(.90));
      vec2 v=(vUv-.5)*vec2(1.2,.86);
      col*=1.-.15*smoothstep(.3,.78,length(v));
      gl_FragColor=vec4(col,1.);
    }
  `;


  function compile(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const message = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error(message);
    }
    return shader;
  }

  function makeProgram(vertex, fragment) {
    const program = gl.createProgram();
    const vs = compile(gl.VERTEX_SHADER, vertex);
    const fs = compile(gl.FRAGMENT_SHADER, fragment);
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      const message = gl.getProgramInfoLog(program);
      gl.deleteProgram(program);
      throw new Error(message);
    }
    return program;
  }

  const programs = {};
  try {
    programs.background = makeProgram(vertexQuad, fragmentSpace);
    programs.particles = makeProgram(vertexCloud, fragmentCloud);
    programs.streaks = makeProgram(vertexCloud, fragmentStreaks);
    programs.post = makeProgram(vertexQuad, fragmentPost);
  } catch (error) {
    Object.values(programs).forEach((program) => gl.deleteProgram(program));
    onFailure?.(String(error?.message || error));
    return () => {};
  }

  function locations(program, names, attributes = false) {
    const result = {};
    for (const name of names) {
      result[name] = attributes
        ? gl.getAttribLocation(program, name)
        : gl.getUniformLocation(program, name);
    }
    return result;
  }

  const attrs = {
    background: locations(programs.background, ['aPosition'], true),
    post: locations(programs.post, ['aPosition'], true),
    particles: locations(programs.particles, ['aPos', 'aMeta'], true),
    streaks: locations(programs.streaks, ['aPos', 'aMeta'], true),
  };
  const uniforms = {
    background: locations(programs.background, [
      'uResolution', 'uMouse', 'uTime', 'uWave', 'uPresence', 'uEnergy',
      'uPulse', 'uColorA', 'uColorB', 'uColorC',
      'uBgA', 'uBgB', 'uStarColor', 'uDark',
    ]),
    particles: locations(programs.particles, [
      'uTime', 'uAspect', 'uDpr', 'uPresence', 'uEnergy', 'uPulse',
      'uReactivity', 'uMouse', 'uView', 'uColorA', 'uColorB', 'uColorC',
      'uStarColor', 'uDark',
    ]),
    streaks: locations(programs.streaks, [
      'uTime', 'uAspect', 'uDpr', 'uPresence', 'uEnergy', 'uPulse',
      'uReactivity', 'uMouse', 'uView', 'uColorA', 'uColorB', 'uColorC',
      'uStarColor', 'uDark',
    ]),
    post: locations(programs.post, ['uScene', 'uResolution', 'uBloom', 'uDark']),
  };

  const quad = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([
    -1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1,
  ]), gl.STATIC_DRAW);

  function drawQuad(attribute) {
    gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    gl.enableVertexAttribArray(attribute);
    gl.vertexAttribPointer(attribute, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
    gl.disableVertexAttribArray(attribute);
  }


  // Stable seeded sampling across several depth planes.

  let rng=0x51a7f0;
  function random(){rng=(Math.imul(rng,1664525)+1013904223)>>>0;return rng/4294967296;}
  function gaussian(){return Math.sqrt(-2*Math.log(Math.max(.00001,random())))*Math.cos(6.2831853*random());}
  const dustCount=31000,streamCount=25000,starCount=3200;
  const particles=new Float32Array((dustCount+streamCount+starCount)*5);
  let filled=0;
  function push(arr,x,y,z,seed,kind){arr[filled++]=x;arr[filled++]=y;arr[filled++]=z;arr[filled++]=seed;arr[filled++]=kind;}
  for(let i=0;i<dustCount;i++){
    const x=(random()*2-1)*6.2,y=(random()*2-1)*3.55,z=(random()*2-1)*2.5;
    push(particles,x,y,z,random(),0);
  }
  // Long, layered filaments travel across the screen and continue beyond its edges.
  for(let i=0;i<streamCount;i++){
    const lane=Math.floor(random()*23),x=(random()*2-1)*6.3;
    const z=(lane%8-3.5)*.59 +gaussian()*.16;
    const baseY=(lane-11)*.16;
    const y=baseY +.31*Math.sin(x*(.59+.04*(lane%4))+lane*.67)
      +.16*Math.sin(x*1.38-lane*.39) +gaussian()*(.065+.045*random());
    push(particles,x,y,z,random(),1);
  }
  for(let i=0;i<starCount;i++){
    const x=(random()*2-1)*6.35,y=(random()*2-1)*3.5,z=(random()*2-1)*2.5;
    push(particles,x,y,z,random(),2);
  }
  const partBuffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,partBuffer);gl.bufferData(gl.ARRAY_BUFFER,particles,gl.STATIC_DRAW);
  // Curved traces cross the viewport, not closed ellipses or orbital circles.
  const curvePoints=[];const ranges=[];
  for(let lane=0;lane<15;lane++){
    const first=curvePoints.length/5,n=245;
    const offset=(lane-7)*.23;
    for(let j=0;j<n;j++){
      const x=(j/(n-1)*2-1)*6.35;
      const y=offset+.30*Math.sin(x*(.62+.03*(lane%5))+lane*.83)
        +.14*Math.sin(x*1.56-lane*.37);
      const z=(lane%6-2.5)*.68+.15*Math.sin(x*.6+lane);
      curvePoints.push(x,y,z,lane/16+.07,3);
    }
    ranges.push([first,n]);
  }
  const lineBuffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,lineBuffer);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(curvePoints),gl.STATIC_DRAW);



  let palette = readAetherThemePalette();
  let themeSignature = typeof document !== 'undefined' ? (document.documentElement?.className || '') : '';

  function refreshThemePalette() {
    const nextSignature = typeof document !== 'undefined' ? (document.documentElement?.className || '') : '';
    if (nextSignature !== themeSignature) {
      themeSignature = nextSignature;
      palette = readAetherThemePalette();
    }
  }
  const mouse = {
    pos: [0, 0], goal: [0, 0], presence: 0, goalPresence: 0,
    energy: 0, pulse: 0, drag: false, lastX: 0, lastY: 0,
    view: [0, 0], viewGoal: [0, 0],
  };

  const texture = gl.createTexture();
  const framebuffer = gl.createFramebuffer();
  const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches || false;
  let ratio = 1;
  let width = 0;
  let height = 0;
  let disposed = false;
  let lost = false;
  let frame = 0;
  let time = 0;
  let last = performance.now();
  let lastDraw = -1000;

  function clamp(value, min, max) { return Math.max(min, Math.min(max, value)); }

  function resize() {
    if (disposed || lost) return;
    const rect = canvas.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    const dprLimit = rect.width < 700 ? 1.1 : 1.5;
    const dpr = Math.min(window.devicePixelRatio || 1, dprLimit);
    ratio = dpr * Math.sqrt(Math.min(1, 1850000 / Math.max(1, rect.width * rect.height * dpr * dpr)));
    const nextWidth = Math.max(1, Math.round(rect.width * ratio));
    const nextHeight = Math.max(1, Math.round(rect.height * ratio));
    if (nextWidth === width && nextHeight === height) return;

    width = nextWidth;
    height = nextHeight;
    canvas.width = width;
    canvas.height = height;
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
    const complete = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    if (!complete) throw new Error('AETHER framebuffer incomplete');
  }

  function trackPointer(event) {
    const rect = canvas.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    const x = (event.clientX - rect.left) / rect.width;
    const y = (event.clientY - rect.top) / rect.height;
    mouse.goal[0] = (x - .5) * 4.04 * (rect.width / rect.height);
    mouse.goal[1] = (.5 - y) * 4.04;
    mouse.goalPresence = 1;
    mouse.energy = clamp(mouse.energy + Math.hypot(event.movementX || 0, event.movementY || 0) * .009, 0, 1);
    if (mouse.drag) {
      mouse.viewGoal[0] = clamp(mouse.viewGoal[0] + (event.clientX - mouse.lastX) * .0035, -1.1, 1.1);
      mouse.viewGoal[1] = clamp(mouse.viewGoal[1] - (event.clientY - mouse.lastY) * .0035, -.85, .85);
    }
    mouse.lastX = event.clientX;
    mouse.lastY = event.clientY;
  }

  function pointerDown(event) {
    if (event.button !== 0) return;
    mouse.pulse = 1;
    // Avoid treating text editing, form controls and navigation as a depth drag.
    if (event.target?.closest?.('button, a, input, textarea, select, [contenteditable="true"], [role="slider"]')) return;
    mouse.drag = true;
    mouse.lastX = event.clientX;
    mouse.lastY = event.clientY;
  }

  function pointerUp() { mouse.drag = false; }
  function pointerLeave(event) {
    if (event.type === 'pointerout' && event.relatedTarget) return;
    mouse.goalPresence = 0;
    mouse.drag = false;
  }
  function focusLost() { mouse.goalPresence = 0; mouse.drag = false; }
  function contextLost(event) {
    event.preventDefault();
    lost = true;
    window.cancelAnimationFrame(frame);
  }
  function contextRestored() {
    // The WebGL resources must be recreated after a device/context reset.
    lost = true;
    onRestore?.();
  }

  window.addEventListener('pointermove', trackPointer, { passive: true });
  window.addEventListener('pointerdown', pointerDown, { passive: true });
  window.addEventListener('pointerup', pointerUp, { passive: true });
  window.addEventListener('pointercancel', pointerUp, { passive: true });
  window.addEventListener('pointerout', pointerLeave, { passive: true });
  window.addEventListener('blur', focusLost);
  canvas.addEventListener('webglcontextlost', contextLost);
  canvas.addEventListener('webglcontextrestored', contextRestored);

  const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(resize) : null;
  if (observer) observer.observe(canvas);
  else window.addEventListener('resize', resize);

  function colors(locations) {
    gl.uniform3fv(locations.uColorA, palette.a);
    gl.uniform3fv(locations.uColorB, palette.b);
    gl.uniform3fv(locations.uColorC, palette.c);
    if (locations.uStarColor !== undefined && locations.uStarColor !== null) {
      gl.uniform3fv(locations.uStarColor, palette.star);
    }
    if (locations.uDark !== undefined && locations.uDark !== null) {
      gl.uniform1f(locations.uDark, palette.dark ? 1 : 0);
    }
  }

  function bindCloud(program, buffer, attribute) {
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.enableVertexAttribArray(attribute.aPos);
    gl.vertexAttribPointer(attribute.aPos, 3, gl.FLOAT, false, 20, 0);
    gl.enableVertexAttribArray(attribute.aMeta);
    gl.vertexAttribPointer(attribute.aMeta, 2, gl.FLOAT, false, 20, 12);
  }

  function unbindCloud(attribute) {
    gl.disableVertexAttribArray(attribute.aPos);
    gl.disableVertexAttribArray(attribute.aMeta);
  }

  function cloudUniforms(locations) {
    gl.uniform1f(locations.uTime, time);
    gl.uniform1f(locations.uAspect, width / height);
    gl.uniform1f(locations.uDpr, ratio);
    gl.uniform1f(locations.uPresence, mouse.presence);
    gl.uniform1f(locations.uEnergy, mouse.energy);
    gl.uniform1f(locations.uPulse, mouse.pulse);
    gl.uniform1f(locations.uReactivity, AETHER_PRESET.reactivity / 100 * 1.5);
    gl.uniform2fv(locations.uMouse, mouse.pos);
    gl.uniform2fv(locations.uView, mouse.view);
    colors(locations);
  }

  function render(now) {
    if (disposed || lost) return;
    frame = window.requestAnimationFrame(render);
    if (document.hidden || !width || !height) { last = now; return; }
    const minFrameTime = window.innerWidth < 700 ? 1000 / 30 : 1000 / 50;
    if (now - lastDraw < minFrameTime) return;
    lastDraw = now;
    const dt = clamp((now - last) * .001, 0, .05);
    last = now;
    if (!reducedMotion) time += dt;

    for (let k = 0; k < 2; k++) {
      mouse.pos[k] += (mouse.goal[k] - mouse.pos[k]) * .105;
      mouse.view[k] += (mouse.viewGoal[k] - mouse.view[k]) * .08;
    }
    mouse.presence += (mouse.goalPresence - mouse.presence) * .1;
    mouse.energy *= .93;
    mouse.pulse *= .944;
    refreshThemePalette();

    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.viewport(0, 0, width, height);
    gl.disable(gl.BLEND);
    gl.useProgram(programs.background);
    const bg = uniforms.background;
    gl.uniform2f(bg.uResolution, width, height);
    gl.uniform2fv(bg.uMouse, mouse.pos);
    gl.uniform1f(bg.uTime, time);
    gl.uniform1f(bg.uWave, AETHER_PRESET.waves / 100);
    gl.uniform1f(bg.uPresence, mouse.presence);
    gl.uniform1f(bg.uEnergy, mouse.energy);
    gl.uniform1f(bg.uPulse, mouse.pulse);
    gl.uniform3fv(bg.uBgA, palette.backgroundA);
    gl.uniform3fv(bg.uBgB, palette.backgroundB);
    colors(bg);
    drawQuad(attrs.background.aPosition);

    gl.enable(gl.BLEND);
    gl.blendEquation(gl.FUNC_ADD);
    if (palette.dark) gl.blendFunc(gl.SRC_ALPHA, gl.ONE);
    else gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    bindCloud(programs.streaks, lineBuffer, attrs.streaks);
    cloudUniforms(uniforms.streaks);
    for (const [start, count] of ranges) gl.drawArrays(gl.LINE_STRIP, start, count);
    unbindCloud(attrs.streaks);

    bindCloud(programs.particles, partBuffer, attrs.particles);
    cloudUniforms(uniforms.particles);
    const density = AETHER_PRESET.density / 100;
    gl.drawArrays(gl.POINTS, 0, Math.round(dustCount * density));
    gl.drawArrays(gl.POINTS, dustCount, Math.round(streamCount * density));
    gl.drawArrays(gl.POINTS, dustCount + streamCount, Math.round(starCount * density));
    unbindCloud(attrs.particles);

    gl.disable(gl.BLEND);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, width, height);
    gl.useProgram(programs.post);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    const post = uniforms.post;
    gl.uniform1i(post.uScene, 0);
    gl.uniform2f(post.uResolution, width, height);
    gl.uniform1f(post.uBloom, AETHER_PRESET.bloom / 100 * 1.85);
    gl.uniform1f(post.uDark, palette.dark ? 1 : 0);
    drawQuad(attrs.post.aPosition);
  }

  try {
    resize();
    frame = window.requestAnimationFrame(render);
  } catch (error) {
    onFailure?.(String(error?.message || error));
    lost = true;
  }

  return () => {
    disposed = true;
    window.cancelAnimationFrame(frame);
    observer?.disconnect();
    window.removeEventListener('resize', resize);
    window.removeEventListener('pointermove', trackPointer);
    window.removeEventListener('pointerdown', pointerDown);
    window.removeEventListener('pointerup', pointerUp);
    window.removeEventListener('pointercancel', pointerUp);
    window.removeEventListener('pointerout', pointerLeave);
    window.removeEventListener('blur', focusLost);
    canvas.removeEventListener('webglcontextlost', contextLost);
    canvas.removeEventListener('webglcontextrestored', contextRestored);
    if (!lost) {
      Object.values(programs).forEach((program) => gl.deleteProgram(program));
      [quad, partBuffer, lineBuffer].forEach((buffer) => gl.deleteBuffer(buffer));
      gl.deleteTexture(texture);
      gl.deleteFramebuffer(framebuffer);
    }
  };
}
