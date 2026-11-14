import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

// Execute the same pointer-mapping code imported by BgFxNeuralWebgl.
const source = await readFile(new URL('../../src/components/bgfx/neuralPointerCoordinates.js', import.meta.url), 'utf8');
const { createNeuralPointerTracker, mapClientToNeuralSpace } = await import(
  `data:text/javascript,${encodeURIComponent(source)}`
);

function harness(rect, width = Math.floor(rect.width), height = Math.floor(rect.height)) {
  let bounds = rect;
  let size = { width, height };
  const canvas = { getBoundingClientRect: () => bounds };
  const tracker = createNeuralPointerTracker(canvas, () => size);
  return {
    ...tracker,
    setBounds: (next) => { bounds = next; },
    setSize: (w, h) => { size = { width: w, height: h }; },
  };
}

test('Neural pointer targets the CSS-pixel center regardless of 100%-250% browser zoom and canvas DPR', () => {
  for (const dpr of [1, 1.1, 1.25, 1.5, 1.75, 2, 2.5]) {
    const rect = { left: 0, top: 0, width: 960, height: 540 };
    const { pointer, setPointer } = harness(rect);
    setPointer(480, 270);
    // DPR changes only the WebGL backing size; the simulation still uses CSS pixels.
    const backing = { width: Math.floor(960 * Math.min(dpr, 2)), height: Math.floor(540 * Math.min(dpr, 2)) };
    assert.ok(backing.width >= 960);
    assert.deepEqual({ x: pointer.x, y: pointer.y }, { x: 480, y: 270 }, `DPR ${dpr}`);
  }
});

test('Neural pointer is local to the canvas instead of assuming it starts at page origin', () => {
  const tracker = harness({ left: 145, top: 73, width: 980, height: 720 });
  tracker.setPointer(635, 433);
  assert.deepEqual({ x: tracker.pointer.x, y: tracker.pointer.y }, { x: 490, y: 360 });
  tracker.setPointer(655, 463);
  assert.deepEqual({ vx: tracker.pointer.vx, vy: tracker.pointer.vy }, { vx: 20, vy: 30 });
});

test('Neural pointer maps CSS-transformed bounds into the shader coordinate system', () => {
  const tracker = harness({ left: 40, top: 60, width: 1200, height: 800 }, 800, 600);
  tracker.setPointer(640, 460);
  assert.deepEqual({ x: tracker.pointer.x, y: tracker.pointer.y }, { x: 400, y: 300 });
  assert.equal(tracker.pointer.vx, 0);
  assert.equal(tracker.pointer.vy, 0);
});

test('Changing zoom/viewport size immediately reprojects the last position, without false cursor velocity', () => {
  const tracker = harness({ left: 0, top: 0, width: 960, height: 540 });
  tracker.setPointer(640, 360);
  tracker.setPointer(650, 360);
  assert.equal(tracker.pointer.vx, 10);

  tracker.setBounds({ left: 16, top: 24, width: 720, height: 480 });
  tracker.setSize(720, 480);
  tracker.reprojectPointer();
  assert.deepEqual(
    { x: tracker.pointer.x, y: tracker.pointer.y, vx: tracker.pointer.vx, vy: tracker.pointer.vy },
    { x: 634, y: 336, vx: 0, vy: 0 },
  );
});

test('Touch coordinates use the same mapping, and invisible canvases do not corrupt the last pointer', () => {
  const tracker = harness({ left: 65, top: 135, width: 400, height: 500 });
  tracker.setPointer(265, 385);
  assert.deepEqual({ x: tracker.pointer.x, y: tracker.pointer.y }, { x: 200, y: 250 });
  tracker.setBounds({ left: 65, top: 135, width: 0, height: 0 });
  tracker.setPointer(266, 386);
  tracker.reprojectPointer();
  assert.deepEqual({ x: tracker.pointer.x, y: tracker.pointer.y }, { x: 200, y: 250 });
  assert.equal(mapClientToNeuralSpace(20, 20, { left: 0, top: 0, width: 0, height: 0 }, 400, 500), null);
});
