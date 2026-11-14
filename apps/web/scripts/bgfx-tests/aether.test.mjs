import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const file = new URL('../../src/components/bgfx/aetherRenderer.js', import.meta.url);
const source = await readFile(file, 'utf8');
const { AETHER_PRESET, createAetherRenderer, makeAetherThemePalette, readAetherThemePalette } = await import(`data:text/javascript,${encodeURIComponent(source)}`);

function makeHarness({ contextAvailable = true } = {}) {
  const events = new Map();
  const callbacks = new Map();
  const drawn = [];
  const floats = {};
  const vectors = {};
  const blendModes = [];
  const destroyed = [];
  let nextId = 0;

  const constants = [
    'VERTEX_SHADER', 'FRAGMENT_SHADER', 'COMPILE_STATUS', 'LINK_STATUS',
    'ARRAY_BUFFER', 'STATIC_DRAW', 'FLOAT', 'TRIANGLES', 'LINE_STRIP', 'POINTS',
    'TEXTURE_2D', 'TEXTURE0', 'TEXTURE_MIN_FILTER', 'TEXTURE_MAG_FILTER',
    'TEXTURE_WRAP_S', 'TEXTURE_WRAP_T', 'CLAMP_TO_EDGE', 'LINEAR',
    'RGBA', 'UNSIGNED_BYTE', 'FRAMEBUFFER', 'COLOR_ATTACHMENT0',
    'FRAMEBUFFER_COMPLETE', 'DEPTH_TEST', 'BLEND', 'FUNC_ADD', 'SRC_ALPHA', 'ONE', 'ONE_MINUS_SRC_ALPHA',
  ];
  const gl = Object.fromEntries(constants.map((name) => [name, name]));
  let created = 0;
  Object.assign(gl, {
    createShader: () => ({ id: ++created }),
    createProgram: () => ({ id: ++created }),
    createBuffer: () => ({ id: ++created }),
    createTexture: () => ({ id: ++created }),
    createFramebuffer: () => ({ id: ++created }),
    getShaderParameter: () => true,
    getProgramParameter: () => true,
    getAttribLocation: () => 0,
    getUniformLocation: (_program, name) => name,
    checkFramebufferStatus: () => gl.FRAMEBUFFER_COMPLETE,
    getShaderInfoLog: () => '',
    getProgramInfoLog: () => '',
    drawArrays: (mode, first, count) => drawn.push({ mode, first, count }),
    uniform1f: (name, value) => { floats[name] = value; },
    uniform3fv: (name, value) => { if (name !== null && name !== undefined) vectors[name] = Array.from(value); },
    blendFunc: (source, destination) => blendModes.push([source, destination]),
    deleteProgram: (object) => destroyed.push(object),
    deleteBuffer: (object) => destroyed.push(object),
    deleteTexture: (object) => destroyed.push(object),
    deleteFramebuffer: (object) => destroyed.push(object),
  });
  const noopMethods = [
    'shaderSource', 'compileShader', 'attachShader', 'linkProgram',
    'deleteShader', 'bindBuffer', 'bufferData', 'enableVertexAttribArray',
    'vertexAttribPointer', 'disableVertexAttribArray', 'bindTexture',
    'texParameteri', 'texImage2D', 'bindFramebuffer', 'framebufferTexture2D',
    'disable', 'enable', 'blendEquation', 'useProgram',
    'uniform2f', 'uniform2fv', 'uniform1i',
    'viewport', 'activeTexture',
  ];
  for (const method of noopMethods) gl[method] = () => {};

  const listeners = new Map();
  const window = {
    innerWidth: 1000,
    devicePixelRatio: 1,
    matchMedia: () => ({ matches: false }),
    requestAnimationFrame(callback) {
      const id = ++nextId;
      callbacks.set(id, callback);
      return id;
    },
    cancelAnimationFrame(id) { callbacks.delete(id); },
    addEventListener(type, listener) { listeners.set(type, listener); },
    removeEventListener(type, listener) {
      if (listeners.get(type) === listener) listeners.delete(type);
    },
  };
  const canvas = {
    width: 0,
    height: 0,
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 1000, height: 600 }),
    getContext: () => contextAvailable ? gl : null,
    addEventListener(type, listener) { events.set(type, listener); },
    removeEventListener(type) { events.delete(type); },
  };
  const root = {
    className: 'dark blue',
    classList: { contains(name) { return root.className.split(' ').includes(name); } },
  };
  const document = { hidden: false, documentElement: root };
  const themes = {
    blue: { '--fx-1': '14 165 233', '--fx-2': '34 211 238', '--fx-3': '167 139 250', '--page-bg': '15 23 42', '--card': '15 23 42', '--fg': '203 213 225' },
    pink: { '--fx-1': '236 72 153', '--fx-2': '168 85 247', '--fx-3': '251 113 133', '--page-bg': '28 16 26', '--card': '32 18 30', '--fg': '253 224 239' },
    apple: { '--fx-1': '16 185 129', '--fx-2': '52 211 153', '--fx-3': '167 243 208', '--page-bg': '12 22 17', '--card': '14 26 20', '--fg': '209 250 229' },
    red: { '--fx-1': '239 68 68', '--fx-2': '251 113 133', '--fx-3': '245 158 11', '--page-bg': '26 12 12', '--card': '30 14 14', '--fg': '254 226 226' },
    honey: { '--fx-1': '245 158 11', '--fx-2': '251 191 36', '--fx-3': '253 230 138', '--page-bg': '20 14 8', '--card': '24 16 10', '--fg': '254 243 199' },
    violet: { '--fx-1': '168 85 247', '--fx-2': '244 114 182', '--fx-3': '34 211 238', '--page-bg': '24 16 38', '--card': '28 18 46', '--fg': '237 233 254' },
  };
  const getComputedStyle = () => {
    const classes = root.className.split(/\s+/);
    const name = Object.keys(themes).find((key) => classes.includes(key)) || 'blue';
    const values = themes[name];
    const isNeo = classes.includes('neo-brutal');
    const isDark = classes.includes('dark');
    return { getPropertyValue: (key) => {
      if (isNeo && key === '--page-bg') return isDark ? '35 33 40' : '238 235 250';
      if (isNeo && key === '--card') return isDark ? '55 52 60' : '255 253 247';
      if (!isDark && name === 'apple' && key === '--page-bg') return '236 253 245';
      if (!isDark && name === 'apple' && key === '--card') return '248 255 252';
      return values[key] || '';
    } };
  };

  function step(now) {
    const next = [...callbacks.entries()][0];
    assert.ok(next, 'animation frame should be scheduled');
    callbacks.delete(next[0]);
    next[1](now);
  }

  return { gl, canvas, window, document, getComputedStyle, events, listeners, step, drawn, floats, vectors, blendModes, destroyed };
}

async function withHarness(harness, callback) {
  const originals = { window: globalThis.window, document: globalThis.document, getComputedStyle: globalThis.getComputedStyle, ResizeObserver: globalThis.ResizeObserver };
  globalThis.window = harness.window;
  globalThis.document = harness.document;
  globalThis.getComputedStyle = harness.getComputedStyle;
  globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
  };
  try { await callback(); }
  finally {
    globalThis.window = originals.window;
    globalThis.document = originals.document;
    globalThis.getComputedStyle = originals.getComputedStyle;
    globalThis.ResizeObserver = originals.ResizeObserver;
  }
}

test('AETHER preserves the chosen field strength and follows the active theme', () => {
  assert.deepEqual(AETHER_PRESET, {
    palette: 'theme', reactivity: 28, density: 100, bloom: 100, waves: 100,
  });
});

test('AETHER renders every layer, reacts to pointer, and releases GPU resources', async () => {
  const harness = makeHarness();
  await withHarness(harness, () => {
    const stop = createAetherRenderer(harness.canvas);
    assert.ok(harness.canvas.width > 0);
    harness.step(performance.now() + 100);

    const points = harness.drawn.filter((draw) => draw.mode === harness.gl.POINTS);
    assert.deepEqual(points.map((draw) => draw.count), [31000, 25000, 3200]);
    assert.equal(harness.drawn.filter((draw) => draw.mode === harness.gl.LINE_STRIP).length, 15);
    assert.equal(harness.floats.uWave, 1);
    assert.equal(harness.floats.uBloom, 1.85);
    assert.equal(harness.floats.uDark, 1);
    assert.deepEqual(harness.vectors.uColorA, makeAetherThemePalette({
      fx1: [14, 165, 233], fx2: [34, 211, 238], fx3: [167, 139, 250],
      page: [15, 23, 42], card: [15, 23, 42], fg: [203, 213, 225], dark: true,
    }).a);
    assert.ok(Math.abs(harness.floats.uReactivity - .42) < .00001);

    harness.listeners.get('pointermove')({ clientX: 500, clientY: 300, movementX: 12, movementY: 0 });
    harness.step(performance.now() + 300);
    assert.ok(harness.floats.uPresence > 0);
    stop();
    assert.equal(harness.listeners.size, 0);
    assert.equal(harness.events.size, 0);
    assert.ok(harness.destroyed.length >= 9);
  });
});

test('AETHER falls back without WebGL rather than crashing', async () => {
  const harness = makeHarness({ contextAvailable: false });
  await withHarness(harness, () => {
    const problems = [];
    const stop = createAetherRenderer(harness.canvas, { onFailure: (reason) => problems.push(reason) });
    assert.deepEqual(problems, ['WebGL недоступен']);
    assert.equal(typeof stop, 'function');
    stop();
  });
});

test('AETHER handles WebGL context loss and restoration without stale listeners', async () => {
  const harness = makeHarness();
  await withHarness(harness, () => {
    let restored = 0;
    const stop = createAetherRenderer(harness.canvas, {
      onRestore: () => { restored += 1; },
    });
    let prevented = 0;
    harness.events.get('webglcontextlost')({ preventDefault: () => { prevented += 1; } });
    harness.events.get('webglcontextrestored')();
    assert.equal(prevented, 1);
    assert.equal(restored, 1);
    stop();
    assert.equal(harness.listeners.size, 0);
    assert.equal(harness.events.size, 0);
  });
});

test('AETHER integration keeps 0-8 effects and registration in the existing settings system', async () => {
  const root = new URL('../../src/', import.meta.url);
  const [settings, canvas, persistent] = await Promise.all([
    readFile(new URL('features/settings/settingsModel.js', root), 'utf8'),
    readFile(new URL('components/bgfx/BgFxCanvas.jsx', root), 'utf8'),
    readFile(new URL('components/shell/PersistentBackground.jsx', root), 'utf8'),
  ]);
  for (let variant = 0; variant < 10; variant++) {
    assert.match(settings, new RegExp(`key: '${variant}'`), `variant ${variant} remains selectable`);
  }
  assert.match(settings, /fxVariant: localStorage\.getItem\('fxVariant'\) \|\| '2'/);
  assert.match(settings, /key: '9', title: 'Звёздные потоки'/);
  assert.doesNotMatch(settings, /AETHER · WebGL/);
  assert.match(canvas, /if \(preset === 9\)[\s\S]*?<BgFxAetherWebgl enabled=\{enabled\} paletteKey=\{paletteKey\}/);
  assert.match(persistent, /Math\.floor\(Math\.random\(\) \* 10\)/);
  assert.match(persistent, /aetherActive = bgFx && variant === 9/);
});

// The frontend Docker build context contains only apps/web. Validate the
// cross-service default contract only when running from a full source checkout.
// This explicitly reports a skip in Docker without hiding frontend failures.
const identityApiSource = new URL(
  '../../../services/identity/api/Services/Serialization/IdentityApiSerializationService.cs',
  new URL('../../src/', import.meta.url),
);


test('AETHER colors are derived from theme tokens in both dark and light modes', () => {
  const blue = makeAetherThemePalette({ fx1: [14, 165, 233], fx2: [34, 211, 238], fx3: [167, 139, 250] });
  const pink = makeAetherThemePalette({ fx1: [236, 72, 153], fx2: [168, 85, 247], fx3: [251, 113, 133] });
  const green = makeAetherThemePalette({ fx1: [16, 185, 129], fx2: [52, 211, 153], fx3: [167, 243, 208] });
  const red = makeAetherThemePalette({ fx1: [239, 68, 68], fx2: [251, 113, 133], fx3: [245, 158, 11] });
  const honey = makeAetherThemePalette({ fx1: [245, 158, 11], fx2: [251, 191, 36], fx3: [253, 230, 138] });
  const violet = makeAetherThemePalette({ fx1: [168, 85, 247], fx2: [244, 114, 182], fx3: [34, 211, 238] });
  assert.ok(blue.a[2] > blue.a[0], 'blue uses blue family');
  assert.ok(pink.a[0] > pink.a[1], 'pink uses pink family');
  assert.ok(green.a[1] > green.a[0], 'green uses green family');
  assert.ok(red.a[0] > red.a[1], 'red uses red family');
  assert.ok(honey.a[0] > honey.a[2], 'honey uses gold family');
  assert.ok(violet.a[2] > violet.a[1], 'violet uses violet family');
  assert.equal(new Set([blue, pink, green, red, honey, violet].map((p) => JSON.stringify(p.a))).size, 6);

  const light = makeAetherThemePalette({
    fx1: [16, 185, 129], fx2: [52, 211, 153], fx3: [167, 243, 208],
    page: [236, 253, 245], card: [248, 255, 252], fg: [148, 163, 184], dark: false,
  });
  assert.ok(light.backgroundA.every((value) => value > .86), 'light page stays light');
  assert.ok(blue.backgroundA.every((value) => value < .15), 'dark page stays dark');
  assert.ok(light.a[1] > light.a[0], 'light particles retain green tint');
});

test('AETHER updates palette and light/dark composition when HTML theme classes change', async () => {
  const harness = makeHarness();
  await withHarness(harness, () => {
    const stop = createAetherRenderer(harness.canvas);
    harness.step(performance.now() + 100);
    const original = harness.vectors.uColorA.slice();
    const originalBackground = harness.vectors.uBgA.slice();
    harness.document.documentElement.className = 'dark red';
    harness.step(performance.now() + 400);
    assert.notDeepEqual(harness.vectors.uColorA, original, 'theme color changes without replacing canvas');
    assert.notDeepEqual(harness.vectors.uBgA, originalBackground, 'background itself follows theme');
    assert.equal(harness.floats.uDark, 1);
    harness.document.documentElement.className = 'apple';
    harness.step(performance.now() + 700);
    assert.equal(harness.floats.uDark, 0);
    assert.deepEqual(harness.blendModes.at(-1), [harness.gl.SRC_ALPHA, harness.gl.ONE_MINUS_SRC_ALPHA]);
    assert.ok(harness.vectors.uBgA.every((value) => value > .86));
    assert.deepEqual(harness.vectors.uColorA, readAetherThemePalette().a);
    stop();
  });
});

test('AETHER makes the neo-brutal atmosphere visibly different in every color theme', () => {
  // In the actual neobrutal.css, --page-bg and --card are identical for every
  // theme. Verify that AETHER itself introduces the six distinct ambient tints.
  const accents = {
    blue: [[14, 165, 233], [34, 211, 238], [167, 139, 250]],
    pink: [[236, 72, 153], [168, 85, 247], [251, 113, 133]],
    apple: [[16, 185, 129], [52, 211, 153], [167, 243, 208]],
    red: [[239, 68, 68], [251, 113, 133], [245, 158, 11]],
    honey: [[245, 158, 11], [251, 191, 36], [253, 230, 138]],
    violet: [[168, 85, 247], [244, 114, 182], [34, 211, 238]],
  };
  for (const dark of [true, false]) {
    const base = dark ? [35, 33, 40] : [238, 235, 250];
    const card = dark ? [55, 52, 60] : [255, 253, 247];
    const result = Object.entries(accents).map(([name, [fx1, fx2, fx3]]) => {
      const input = { fx1, fx2, fx3, page: base, card, dark };
      const classic = makeAetherThemePalette(input);
      const neo = makeAetherThemePalette({ ...input, neo: true });
      assert.deepEqual(neo.a, classic.a, `${name} particles unchanged`);
      assert.notDeepEqual(neo.backgroundA, classic.backgroundA, `${name} richer neo space`);
      assert.notDeepEqual(neo.backgroundB, classic.backgroundB, `${name} richer neo atmosphere`);
      assert.ok(neo.backgroundA.every((v) => dark ? v < .25 : v > .73),
        `${name} must remain ${dark ? 'dark' : 'light'}`);
      assert.equal(neo.dark, dark);
      assert.equal(neo.neo, true);
      return neo;
    });
    const shades = result.map(({ backgroundA }) => backgroundA.map((value) => Math.round(value * 255)).join(','));
    assert.equal(new Set(shades).size, 6, `six different ${dark ? 'dark' : 'light'} space colors`);
    for (let i = 0; i < result.length; i++) {
      for (let j = i + 1; j < result.length; j++) {
        const delta = result[i].backgroundA.reduce((sum, value, k) => sum + Math.abs(value - result[j].backgroundA[k]), 0);
        assert.ok(delta > .035, `neo theme pair ${i}/${j} is visually distinguishable`);
      }
    }
  }
});

test('AETHER picks up neo-brutal style toggles and theme changes live without losing the WebGL canvas', async () => {
  const harness = makeHarness();
  await withHarness(harness, () => {
    const stop = createAetherRenderer(harness.canvas);
    harness.step(performance.now() + 100);
    const classic = harness.vectors.uBgA.slice();
    harness.document.documentElement.className = 'dark blue neo-brutal';
    harness.step(performance.now() + 350);
    const blueNeo = harness.vectors.uBgA.slice();
    assert.notDeepEqual(blueNeo, classic, 'enabling neo recolors the whole field');
    assert.deepEqual(blueNeo, readAetherThemePalette().backgroundA);
    harness.document.documentElement.className = 'dark pink neo-brutal';
    harness.step(performance.now() + 600);
    assert.notDeepEqual(harness.vectors.uBgA, blueNeo, 'switching color themes recolors the sky');
    harness.document.documentElement.className = 'pink neo-brutal';
    harness.step(performance.now() + 850);
    assert.equal(harness.floats.uDark, 0, 'light mode is still light');
    assert.ok(harness.vectors.uBgA.every((component) => component > .73));
    stop();
    assert.equal(harness.listeners.size, 0);
  });
});

test('AETHER keeps Identity API default background settings unchanged', {
  skip: !existsSync(identityApiSource) && 'Identity API sources are outside the frontend Docker build context',
}, async () => {
  const server = await readFile(identityApiSource, 'utf8');
  assert.match(server, /\\\"bgFx\\\":false/);
  assert.match(server, /\\\"fxVariant\\\":\\\"2\\\"/);
});
