// Shader-based particle scene used by the ranked-user levitation display.
// Rendering is continuous and deliberately ignores all hover / pointer input.
export function mountLeaderboardParticles({ scene, canvas, winners }) {
      const stage = scene.closest('.tf-top3-stage');
      const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
      let gl, program, buffer, vertexCount = 0, raf = 0, observer, resizeObserver, broken = false;
      let timeBase = performance.now();
      let visible = true;
      let uniforms;

      const vertexShader = `
        precision highp float;
        attribute vec4 aConfig;  // particle kind, angle, distance, birth phase
        attribute vec4 aVisual;  // size, velocity, opacity, depth layer
        attribute float aGroup;
        uniform vec2 uResolution;
        uniform vec2 uCenters[3];
        uniform float uRadius[3];
        uniform float uShown[3];
        uniform float uTime;
        uniform float uDpr;
        uniform float uLight;
        varying vec3 vColor;
        varying float vOpacity;
        varying float vSoftness;
        void main() {
          float mode = aConfig.x;
          float angle = aConfig.y;
          float radial = aConfig.z;
          float seed = aConfig.w;
          float size = aVisual.x;
          float speed = aVisual.y;
          float opacity = aVisual.z;
          float depth = aVisual.w;
          vec2 center = uCenters[0];
          float radius = uRadius[0];
          float shown = uShown[0];
          vec3 colorDark = vec3(.72,.81,1.0);
          vec3 colorLight = vec3(.31,.46,.71);
          if (aGroup > .5 && aGroup < 1.5) {
            center=uCenters[1];radius=uRadius[1];shown=uShown[1];
            colorDark=vec3(1.0,.72,.33);colorLight=vec3(.70,.44,.13);
          } else if (aGroup > 1.5) {
            center=uCenters[2];radius=uRadius[2];shown=uShown[2];
            colorDark=vec3(1.0,.65,.53);colorLight=vec3(.69,.36,.28);
          }
          vColor=mix(colorDark,colorLight,uLight);
          float t=uTime;
          vec2 pos=vec2(0.0);
          float fade=1.0;
          if (mode < .5) {
            // Multiple concentric, slowly rotating tilted elliptical particle orbits.
            float a=angle+t*speed;
            float lane=floor(depth*4.0);
            float r=radius*radial*(.91+lane*.038);
            pos.x=cos(a)*r;
            pos.y=sin(a)*r*(.24+lane*.017)-(lane-1.5)*5.0 + sin(t*.6+seed*6.28)*1.7;
            fade=mix(.75,1.0,step(0.0,sin(a)));
          } else if (mode < 1.5) {
            // Dust detaches from the orbit and rises in a loosely braided vortex.
            float life=fract(seed+t*speed);
            float a=angle+life*8.6-t*.49;
            float r=radius*(.22+.63*(1.0-life))*radial;
            pos=vec2(cos(a)*r, -life*(125.0+depth*37.0)+sin(a)*r*.24+17.0);
            fade=smoothstep(0.0,.10,life)*(1.0-smoothstep(.79,1.0,life));
          } else if (mode < 2.5) {
            // Sporadic little flecks drift across the entire volume.
            float life=fract(seed+t*speed*.65);
            float a=angle+t*.10;
            pos.x=cos(a)*radius*radial+sin(t*.43+seed*7.0)*7.0;
            pos.y=28.0-life*(145.0+depth*43.0)+sin(a)*radius*radial*.13;
            fade=smoothstep(.0,.12,life)*(1.0-smoothstep(.84,1.0,life))*.63;
          } else {
            // Shimmering beads travel around the outer edge of an invisible disc.
            float a=angle+t*speed;
            float r=radius*radial;
            pos=vec2(cos(a)*r,sin(a)*r*.26-4.0);
            fade=(.68+.32*sin(t*1.15+seed*17.0))*1.04;
          }
          vOpacity=opacity*fade*shown*(1.0-uLight*.09);
          vSoftness=mode;
          gl_PointSize=max(1.0,min(16.0,size*uDpr*(1.0+0.12*sin(t*.9+seed*19.0))));
          vec2 pixel=center+pos;
          gl_Position=vec4(pixel.x/uResolution.x*2.0-1.0,1.0-pixel.y/uResolution.y*2.0,0.0,1.0);
        }
      `;
      const fragmentShader = `
        precision mediump float;
        varying vec3 vColor;
        varying float vOpacity;
        varying float vSoftness;
        void main() {
          vec2 p=gl_PointCoord-vec2(.5);
          float dist=length(p)*2.0;
          float core=1.0-smoothstep(.05,.70,dist);
          float fog=1.0-smoothstep(.35,1.0,dist);
          float alpha=(core*.72+fog*.28)*vOpacity;
          if (alpha < .008) discard;
          gl_FragColor=vec4(vColor,alpha);
        }
      `;

      function makeShader(type, src) {
        const s=gl.createShader(type);gl.shaderSource(s,src);gl.compileShader(s);
        if(!gl.getShaderParameter(s,gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)||'Shader compilation failed');
        return s;
      }
      function randomGenerator(seed) {
        return () => {seed|=0;seed=seed+0x6D2B79F5|0;let t=Math.imul(seed^seed>>>15,1|seed);t=t+Math.imul(t^t>>>7,61|t)^t;return ((t^t>>>14)>>>0)/4294967296;};
      }
      function makeVertices() {
        const rand=randomGenerator(20261114), result=[];
        const narrow=innerWidth<610;
        const count=(n)=>Math.round(n*(narrow?.67:1));
        const add=(g,k,a,r,p,s,v,o,d)=>result.push(k,a,r,p,s,v,o,d,g);
        for(let g=0;g<3;g++){
          for(let i=0,n=count(800);i<n;i++){
            const a=2*Math.PI*(i/n)+(rand()-.5)*.08;
            add(g,0,a,.69+rand()*.39,rand(),1.7+rand()*2.1,(rand()>.5?1:-1)*(.09+rand()*.21),.28+rand()*.54,rand());
          }
          for(let i=0,n=count(770);i<n;i++){
            add(g,1,rand()*Math.PI*2,.55+rand()*.67,rand(),1.3+rand()*3.1,.045+rand()*.087,.22+rand()*.61,rand());
          }
          for(let i=0,n=count(290);i<n;i++){
            add(g,2,rand()*Math.PI*2,rand()*1.24,rand(),1.0+rand()*2.9,.03+rand()*.08,.16+rand()*.43,rand());
          }
          for(let i=0,n=count(155);i<n;i++){
            add(g,3,rand()*Math.PI*2,.89+rand()*.25,rand(),2.6+rand()*3.6,(rand()>.5?1:-1)*(.12+rand()*.21),.37+rand()*.57,rand());
          }
        }
        return new Float32Array(result);
      }

      // Canvas2D fallback for browsers which explicitly block WebGL (including
      // this preview environment). WebGL remains the primary rendering path.
      let ctx2d=null, fallbackData=null, fallbackLast=0;
      function startCanvasFallback() {
        try {ctx2d=canvas.getContext('2d',{alpha:true});}catch{}
        if(!ctx2d){canvas.dataset.renderer='css-fallback';return;}
        fallbackData=makeVertices();
        canvas.dataset.renderer='canvas2d-fallback';
        stage.classList.add('tf-top3-stage--canvas');
        refresh();
      }
      function drawFallback(ts) {
        raf=0;
        if(!ctx2d)return;
        if(!reducedMotion.matches && ts-fallbackLast<30){
          if(visible && document.visibilityState==='visible')raf=requestAnimationFrame(drawFallback);
          return;
        }
        fallbackLast=ts;
        const width=scene.clientWidth, height=scene.clientHeight;
        const dpr=Math.min(devicePixelRatio||1,1.8);
        const bw=Math.round(width*dpr),bh=Math.round(height*dpr);
        if(canvas.width!==bw||canvas.height!==bh){canvas.width=bw;canvas.height=bh;}
        ctx2d.setTransform(dpr,0,0,dpr,0,0);
        ctx2d.clearRect(0,0,width,height);
        const {centers,radii,shown}=anchors();
        const t=reducedMotion.matches?2.85:Math.max(0,(ts-timeBase)/1000);
        const light=!document.documentElement.classList.contains('dark');
        const palette=light?[[69,104,172],[170,107,39],[168,86,71]]:[[176,210,255],[255,191,94],[255,157,129]];
        ctx2d.globalCompositeOperation=light?'source-over':'lighter';
        for(let g=0;g<3;g++){
          if(!shown[g])continue;
          const x=centers[g*2],y=centers[g*2+1],radius=radii[g];
          const c=palette[g],rgb=c.join(',');
          const halo=ctx2d.createRadialGradient(x,y,0,x,y,radius*1.20);
          halo.addColorStop(0,`rgba(${rgb},${light?.16:.18})`);
          halo.addColorStop(.55,`rgba(${rgb},${light?.08:.09})`);
          halo.addColorStop(1,`rgba(${rgb},0)`);
          ctx2d.save();ctx2d.translate(x,y);ctx2d.scale(1,.34);ctx2d.translate(-x,-y);
          ctx2d.fillStyle=halo;ctx2d.fillRect(x-radius*1.22,y-radius*1.22,radius*2.44,radius*2.44);
          ctx2d.restore();
          for(let lane=0;lane<3;lane++){
            ctx2d.beginPath();ctx2d.ellipse(x,y+(lane-1)*5,radius*(.78+lane*.07),radius*(.20+lane*.019),0,0,Math.PI*2);
            ctx2d.strokeStyle=`rgba(${rgb},${light?.19:.22})`;
            ctx2d.lineWidth=lane===1?1.25:.75;ctx2d.stroke();
          }
        }
        const v=fallbackData;
        let previous=-1;
        for(let i=0;i<v.length;i+=18){ // Every second vertex: cap CPU use.
          const mode=v[i],angle=v[i+1],radial=v[i+2],seed=v[i+3];
          const size=v[i+4],speed=v[i+5],opacity=v[i+6],depth=v[i+7],g=Math.round(v[i+8]);
          if(!shown[g])continue;
          if(g!==previous){previous=g;const c=palette[g];ctx2d.fillStyle=`rgb(${c[0]},${c[1]},${c[2]})`;}
          const radius=radii[g];
          let px=0,py=0,fade=1;
          if(mode<.5){
            const a=angle+t*speed,lane=Math.floor(depth*4),r=radius*radial*(.91+lane*.038);
            px=Math.cos(a)*r;py=Math.sin(a)*r*(.24+lane*.017)-(lane-1.5)*5+Math.sin(t*.6+seed*6.28)*1.7;
            fade=Math.sin(a)>0?1:.75;
          }else if(mode<1.5){
            const life=(seed+t*speed)%1,a=angle+life*8.6-t*.49,r=radius*(.22+.63*(1-life))*radial;
            px=Math.cos(a)*r;py=-life*(125+depth*37)+Math.sin(a)*r*.24+17;
            fade=Math.max(0,Math.min(1,life/.10))*Math.max(0,Math.min(1,(1-life)/.21));
          }else if(mode<2.5){
            const life=(seed+t*speed*.65)%1,a=angle+t*.10;
            px=Math.cos(a)*radius*radial+Math.sin(t*.43+seed*7)*7;
            py=28-life*(145+depth*43)+Math.sin(a)*radius*radial*.13;
            fade=Math.max(0,Math.min(1,life/.12))*Math.max(0,Math.min(1,(1-life)/.16))*.63;
          }else{
            const a=angle+t*speed,r=radius*radial;
            px=Math.cos(a)*r;py=Math.sin(a)*r*.26-4;
            fade=(.68+.32*Math.sin(t*1.15+seed*17))*1.04;
          }
          ctx2d.globalAlpha=Math.min(1,opacity*fade*(light?.85:1.0));
          const sz=Math.max(1,size*.64);
          const posx=centers[g*2]+px, posy=centers[g*2+1]+py;
          if(sz<2.4)ctx2d.fillRect(posx,posy,sz,sz);
          else{ctx2d.beginPath();ctx2d.arc(posx,posy,sz*.56,0,Math.PI*2);ctx2d.fill();}
        }
        ctx2d.globalAlpha=1;ctx2d.globalCompositeOperation='source-over';
        if(!reducedMotion.matches && visible && document.visibilityState==='visible')raf=requestAnimationFrame(drawFallback);
      }

      function anchors() {
        const box=scene.getBoundingClientRect();
        const centers=[],radii=[],shown=[];
        winners.forEach(w=>{
          const a=w.querySelector('.tf-top3-ether-anchor').getBoundingClientRect();
          centers.push(a.left-box.left+a.width*.5,a.top-box.top+a.height*.5);
          radii.push(Math.max(30,Math.min(134,w.getBoundingClientRect().width*.43)));
          shown.push(w.hidden?0:1);
        });
        return {centers,radii,shown};
      }
      function fit() {
        if(!gl)return;
        const w=Math.max(1,scene.clientWidth),h=Math.max(1,scene.clientHeight);
        const dpr=Math.min(devicePixelRatio||1,1.8);
        const bw=Math.round(w*dpr),bh=Math.round(h*dpr);
        if(canvas.width!==bw || canvas.height!==bh){canvas.width=bw;canvas.height=bh;gl.viewport(0,0,bw,bh)}
        return {w,h,dpr};
      }
      function draw(ts) {
        raf=0;
        if(!gl || broken)return;
        const fitSize=fit();if(!fitSize)return;
        const positions=anchors();
        gl.clearColor(0,0,0,0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.useProgram(program);
        gl.uniform2f(uniforms.resolution,fitSize.w,fitSize.h);
        gl.uniform2fv(uniforms.centers,new Float32Array(positions.centers));
        gl.uniform1fv(uniforms.radii,new Float32Array(positions.radii));
        gl.uniform1fv(uniforms.shown,new Float32Array(positions.shown));
        gl.uniform1f(uniforms.time,reducedMotion.matches?2.85:Math.max(0,(ts-timeBase)/1000));
        gl.uniform1f(uniforms.dpr,fitSize.dpr);
        gl.uniform1f(uniforms.light,!document.documentElement.classList.contains('dark')?1:0);
        gl.drawArrays(gl.POINTS,0,vertexCount);
        if(!reducedMotion.matches && visible && document.visibilityState==='visible')raf=requestAnimationFrame(draw);
      }
      function refresh() {
        if(broken || (!gl && !ctx2d))return;
        if(raf)cancelAnimationFrame(raf);
        raf=requestAnimationFrame(gl?draw:drawFallback);
      }
      function init() {
        if(broken)return;
        try {
          gl=canvas.getContext('webgl',{alpha:true,antialias:false,depth:false,stencil:false,premultipliedAlpha:false,preserveDrawingBuffer:false,powerPreference:'low-power'});
          if(!gl){startCanvasFallback();return;}
          const vs=makeShader(gl.VERTEX_SHADER,vertexShader),fs=makeShader(gl.FRAGMENT_SHADER,fragmentShader);
          program=gl.createProgram();gl.attachShader(program,vs);gl.attachShader(program,fs);gl.linkProgram(program);
          if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw new Error(gl.getProgramInfoLog(program)||'Program link failed');
          gl.deleteShader(vs);gl.deleteShader(fs);
          gl.useProgram(program);
          const data=makeVertices();vertexCount=data.length/9;
          buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buffer);gl.bufferData(gl.ARRAY_BUFFER,data,gl.STATIC_DRAW);
          const stride=9*4;
          [['aConfig',4,0],['aVisual',4,16],['aGroup',1,32]].forEach(([name,size,offset])=>{
            const idx=gl.getAttribLocation(program,name);
            if(idx<0)throw new Error(name+' attribute unavailable');
            gl.enableVertexAttribArray(idx);gl.vertexAttribPointer(idx,size,gl.FLOAT,false,stride,offset);
          });
          uniforms={
            resolution:gl.getUniformLocation(program,'uResolution'),
            centers:gl.getUniformLocation(program,'uCenters[0]'),
            radii:gl.getUniformLocation(program,'uRadius[0]'),
            shown:gl.getUniformLocation(program,'uShown[0]'),
            time:gl.getUniformLocation(program,'uTime'),
            dpr:gl.getUniformLocation(program,'uDpr'),
            light:gl.getUniformLocation(program,'uLight')
          };
          gl.enable(gl.BLEND);
          gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);
          gl.disable(gl.DEPTH_TEST);
          stage.classList.add('tf-top3-stage--webgl');
          canvas.dataset.renderer='webgl';
          refresh();
        }catch(err){
          console.warn('TaskForge particles: WebGL unavailable, using CSS fallback', err);
          broken=true;gl=null;stage.classList.remove('tf-top3-stage--webgl');canvas.dataset.renderer='fallback';
        }
      }

      const onLost = (event) => {
        event.preventDefault();
        if (raf) cancelAnimationFrame(raf);
        raf = 0;
        gl = null;
        stage.classList.remove('tf-top3-stage--webgl');
      };
      const onRestored = () => { broken = false; init(); };
      const onVisibility = () => {
        if (document.visibilityState === 'visible') refresh();
        else if (raf) { cancelAnimationFrame(raf); raf = 0; }
      };
      const onIntersection = (entries) => {
        visible = entries[0]?.isIntersecting ?? true;
        if (visible) refresh();
        else if (raf) { cancelAnimationFrame(raf); raf = 0; }
      };
      canvas.addEventListener('webglcontextlost', onLost);
      canvas.addEventListener('webglcontextrestored', onRestored);
      if ('ResizeObserver' in window) {
        resizeObserver = new ResizeObserver(refresh);
        resizeObserver.observe(scene);
      }
      window.addEventListener('resize', refresh, { passive: true });
      document.addEventListener('visibilitychange', onVisibility);
      if ('IntersectionObserver' in window) {
        observer = new IntersectionObserver(onIntersection, { threshold: 0.01 });
        observer.observe(scene);
      }
      if (reducedMotion.addEventListener) reducedMotion.addEventListener('change', refresh);
      const themeObserver = new MutationObserver(refresh);
      themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
      init();

      return () => {
        if (raf) cancelAnimationFrame(raf);
        themeObserver.disconnect();
        resizeObserver?.disconnect();
        observer?.disconnect();
        window.removeEventListener('resize', refresh);
        document.removeEventListener('visibilitychange', onVisibility);
        canvas.removeEventListener('webglcontextlost', onLost);
        canvas.removeEventListener('webglcontextrestored', onRestored);
        if (reducedMotion.removeEventListener) reducedMotion.removeEventListener('change', refresh);
        if (gl) {
          if (buffer) gl.deleteBuffer(buffer);
          if (program) gl.deleteProgram(program);
        }
      };
}
