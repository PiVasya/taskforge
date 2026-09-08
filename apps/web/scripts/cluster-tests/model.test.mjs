import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/features/cluster/clusterModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('missing metrics stay missing; zero is a valid measurement', () => {
  for (const missing of [null, undefined, '', 'not-a-number', NaN]) {
    assert.equal(model.number(missing), null);
    assert.equal(model.bytes(missing), model.DASH);
  }
  assert.equal(model.bytes(0), '0 \u0411');
  assert.equal(model.percent(null, 100), null);
  assert.equal(model.percent(0, 100), 0);
  assert.equal(model.percent(5, 0), null);
  assert.equal(model.percent(150, 100), 100);
  assert.equal(model.duration(null), model.DASH);
  assert.equal(model.countPair(null, 33), model.DASH);
});
test('running healthy containers are green with no invented restart count', () => {
  assert.equal(model.containerTone({ status: 'running', health: 'healthy' }, { online: true }), 'good');
  assert.equal(model.containerTone({ state: 'running', health: 'none' }, { online: true }), 'good');
  assert.equal(model.containerTone({ state: 'running', health: 'unhealthy' }, { online: true }), 'bad');
  assert.equal(model.containerTone({ state: 'running', oomKilled: true }, { online: true }), 'bad');
});
test('prepared standby is not a failed service; stale samples are not healthy proof', () => {
  assert.equal(model.containerTone({ state: 'exited' }, { online: true, role: 'standby' }), 'muted');
  assert.equal(model.containerTone({ state: 'exited' }, { online: true, role: 'primary' }), 'warn');
  assert.equal(model.containerTone({ state: 'running', health: 'healthy' }, { online: true, telemetryFresh: false }), 'muted');
});
test('automatic placement separates any number of real card rectangles', () => {
  for (let count = 1; count <= 10; count++) {
    const nodes = Array.from({ length: count }, (_, i) => ({ id: `N${i}` }));
    for (const primary of [nodes[0].id, nodes.at(-1).id, null]) {
      const p = model.autoPositions(nodes, primary, true);
      for (const [i, a] of nodes.entries()) for (const b of nodes.slice(i + 1)) {
        const overlapX = Math.abs(p[a.id].x - p[b.id].x) < model.NODE_WIDTH;
        const overlapY = Math.abs(p[a.id].y - p[b.id].y) < model.NODE_HEIGHT;
        assert.ok(!(overlapX && overlapY), `${a.id} collides with ${b.id}`);
      }
      assert.ok(p[model.EDGE_ID]);
    }
  }
});
test('layout survives storage, rejects malformed coordinates, and handles denied storage', () => {
  const memory = new Map();
  const storage = { getItem: k => memory.get(k), setItem: (k, v) => memory.set(k, v) };
  assert.equal(model.saveLayout(storage, 'layout', { A: { x: 100, y: 300 } }, { x: 20, y: 15, zoom: 0.8 }), true);
  assert.deepEqual(model.readLayout(storage, 'layout').positions.A, { x: 100, y: 300 });
  assert.equal(model.readLayout(storage, 'layout').viewport.zoom, 0.8);
  storage.setItem('layout', '{broken');
  assert.deepEqual(model.readLayout(storage, 'layout').positions, {});
  storage.setItem('layout', JSON.stringify({ positions: { A: { x: '100', y: 0 }, B: { x: 1e20, y: 0 } }, viewport: { x: 0, y: 0, zoom: 99 } }));
  assert.deepEqual(model.readLayout(storage, 'layout'), { positions: {}, viewport: null });
  const denied = { getItem() { throw Error('denied'); }, setItem() { throw Error('denied'); } };
  assert.equal(model.saveLayout(denied, 'k', {}, null), false);
  assert.deepEqual(model.readLayout(denied, 'k'), { positions: {}, viewport: null });
});
test('service matrix includes legacy/modern data and never fabricates equality', () => {
  const rows = model.servicesMatrix([{ id: 'A', services: ['old-string', { service: 'gateway', imageStatus: 'unverified' }] }, { id: 'C', services: [{ service: 'gateway', imageStatus: 'unverified' }, { service: 'api', imageStatus: 'same' }] }]);
  assert.deepEqual(rows.map(r => r.name), ['api', 'gateway']);
  assert.equal(rows[1].cells.A.imageStatus, 'unverified');
  assert.equal(rows[0].cells.A, undefined);
});
test('primary role and timestamps tolerate null/unknown input', () => {
  assert.equal(model.primaryRole('primary'), true);
  assert.equal(model.primaryRole('MASTER'), true);
  assert.equal(model.primaryRole('standby'), false);
  assert.equal(model.primaryRole(null), false);
  assert.equal(model.dateTime('not a date'), model.DASH);
});
test('complete assignment list identifies excluded lite services without inventing missing images', () => {
  const lite = { online: true, services: [{ service: 'gateway', assigned: true }], docker: { assignedAppCount: 1 } };
  assert.equal(model.imageCell(lite, null).imageStatus, 'not-assigned');
  assert.equal(model.imageCell({ ...lite, telemetryFresh: false }, null), null);
  assert.equal(model.imageCell({ ...lite, docker: { assignedAppCount: 2 } }, null), null);
});
test('boolean/whitespace/object metrics are not measurements', () => {
  for (const input of [true, false, '   ', [], {}]) assert.equal(model.number(input), null);
  assert.equal(model.bytes(0.5).includes('undefined'), false);
});
test('an unconfirmed primary cannot silently appear as a healthy standby', () => {
  assert.equal(model.nodeTone({ online: true, healthy: true, role: 'primary', isActive: false }), 'warn');
});
