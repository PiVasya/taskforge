import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const file = new URL('../../src/components/bgfx/aetherRenderer.js', import.meta.url);
const source = await readFile(file, 'utf8');
const { AETHER_PRESET, createAetherRenderer } = await import(`data:text/javascript,${encodeURIComponent(source)}`);

function makeHarness({ contextAvailable = true } = {}) {
  const events = new Map();
  const callbacks = new Map();
  const drawn = [];
  const floats = {};
  const destroyed = [];
  let nextId = 0;

  const constants = [
    'VERTEX_SHADER', 'FRAGMENT_SHADER', 'COMPILE_STATUS', 'LINK_STATUS',
    'ARRAY_BUFFER', 'STATIC_DRAW', 'FLOAT', 'TRIANGLES', 'LINE_STRIP', 'POINTS',
    'TEXTURE_2D', 'TEXTURE0', 'TEXTURE_MIN_FILTER', 'TEXTURE_MAG_FILTER',
    'TEXTURE_WRAP_S', 'TEXTURE_WRAP_T', 'CLAMP_TO_EDGE', 'LINEAR',
    'RGBA', 'UNSIGNED_BYTE', 'FRAMEBUFFER', 'COLOR_ATTACHMENT0',
    'FRAMEBUFFER_COMPLETE', 'DEPTH_TEST', 'BLEND', 'FUNC_ADD', 'SRC_ALPHA', 'ONE',
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
    'disable', 'enable', 'blendEquation', 'blendFunc', 'useProgram',
    'uniform2f', 'uniform2fv', 'uniform3fv', 'uniform1i',
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
  const document = { hidden: false };

  function step(now) {
    const next = [...callbacks.entries()][0];
    assert.ok(next, 'animation frame should be scheduled');
    callbacks.delete(next[0]);
    next[1](now);
  }

  return { gl, canvas, window, document, events, listeners, step, drawn, floats, destroyed };
}

async function withHarness(harness, callback) {
  const originals = { window: globalThis.window, document: globalThis.document, ResizeObserver: globalThis.ResizeObserver };
  globalThis.window = harness.window;
  globalThis.document = harness.document;
  globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
  };
  try { await callback(); }
  finally {
    globalThis.window = originals.window;
    globalThis.document = originals.document;
    globalThis.ResizeObserver = originals.ResizeObserver;
  }
}

test('AETHER has the exact selected Arctic preset', () => {
  assert.deepEqual(AETHER_PRESET, {
    palette: 'arctic', reactivity: 28, density: 100, bloom: 100, waves: 100,
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
  assert.match(canvas, /if \(preset === 9\)[\s\S]*?<BgFxAetherWebgl enabled=\{enabled\}/);
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
test('AETHER keeps Identity API default background settings unchanged', {
  skip: !existsSync(identityApiSource) && 'Identity API sources are outside the frontend Docker build context',
}, async () => {
  const server = await readFile(identityApiSource, 'utf8');
  assert.match(server, /\\\"bgFx\\\":false/);
  assert.match(server, /\\\"fxVariant\\\":\\\"2\\\"/);
});
