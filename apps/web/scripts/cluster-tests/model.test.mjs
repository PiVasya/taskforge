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

test('Primary switch client preflight only enables a fully ready standby', () => {
  const active = { id: 'A', role: 'primary', trafficReady: true, bundleRevision: '57', edge: { configured: true } };
  const ready = {
    id: 'B', role: 'standby', leader: 'A', canBePrimary: true, online: true, telemetryFresh: true,
    reconciled: true, controlPlaneFenced: false, hotStartReady: true, postgres: { healthy: true },
    bundleRevision: '57', edge: { configured: true },
  };
  assert.deepEqual(model.primarySwitchEligibility(ready, active, true), { ready: true, reasons: [] });
  const bad = { ...ready, id: 'C', hotStartReady: false, postgres: { healthy: false }, edge: { configured: false } };
  const result = model.primarySwitchEligibility(bad, active, true);
  assert.equal(result.ready, false);
  assert.ok(result.reasons.includes('not-hot-ready'));
  assert.ok(result.reasons.includes('postgres-not-ready'));
  assert.ok(result.reasons.includes('edge-not-configured'));
});

test('Primary switch is complete only after target traffic and current Cloudflare HTTPS proof', () => {
  const target = {
    id: 'B', role: 'primary', trafficReady: true, edge: { configured: true, state: {
      success: true, phase: 'ready', dns_synced: true, route_ready: true, tls_ready: true, target_node: 'B',
    } },
  };
  assert.equal(model.primarySwitchComplete([target], 'B', 'B'), true);
  assert.equal(model.primarySwitchComplete([{ ...target, edge: { ...target.edge, state: { ...target.edge.state, phase: 'route-check' } } }], 'B', 'B'), false);
  assert.equal(model.primarySwitchComplete([{ ...target, trafficReady: false }], 'B', 'B'), false);
  assert.equal(model.primarySwitchComplete([target], 'A', 'B'), false);
});

test('lost Primary POST becomes an explicit observed-no-transition state instead of silent WAIT', () => {
  const nodes = [
    { id: 'A', role: 'primary', online: true, telemetryFresh: true },
    { id: 'B', role: 'standby', leader: 'A', online: true, telemetryFresh: true, applicationsActive: false, trafficReady: false, edge: { configured: true, state: { phase: 'standby', route_confirmations: 0, route_required: 3 } } },
  ];
  const pending = { from: 'A', target: 'B', uncertain: true, acceptedAt: 1_000 };
  const progress = model.primarySwitchProgress(nodes, 'A', pending, 31_000);
  assert.equal(progress.stableNoTransition, true);
  assert.equal(progress.allowDismiss, true);
  assert.match(progress.summary, /Primary остаётся A/);
  assert.equal(progress.stages[0].state, 'warn');
  assert.equal(progress.stages[1].detail, 'Сейчас A');
});

test('Primary progress exposes role, apps, DNS, route and HTTPS as separate live stages', () => {
  const nodes = [
    { id: 'A', role: 'standby', online: true, telemetryFresh: true },
    { id: 'B', role: 'primary', online: true, telemetryFresh: true, applicationsActive: true, trafficReady: false, edge: { configured: true, state: {
      phase: 'route-check', dns_synced: true, target_node: 'B', route_ready: false,
      route_confirmations: 1, route_required: 3, tls_ready: false,
    } } },
  ];
  const progress = model.primarySwitchProgress(nodes, 'B', { from: 'A', target: 'B', uncertain: true, acceptedAt: 0 }, 5_000);
  assert.equal(progress.targetPrimary, true);
  assert.equal(progress.appsReady, true);
  assert.equal(progress.dnsReady, true);
  assert.equal(progress.routeReady, false);
  assert.equal(progress.tlsReady, false);
  assert.equal(progress.stages.find(x => x.id === 'request').detail, 'Подтверждён фактом');
  assert.equal(progress.stages.find(x => x.id === 'dns').state, 'done');
  assert.equal(progress.stages.find(x => x.id === 'route').detail, '1/3');
  assert.match(progress.summary, /Проверяется публичный маршрут/);
});
