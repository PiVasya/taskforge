import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const original = await fs.readFile(new URL('../../src/features/course-assignments/courseMapDebug.js', import.meta.url), 'utf8');
const source = original.replace(
  "import { getFrontendDiagnosticsConfig, logFrontendEvent } from '../../devtools/frontendDiagnostics';",
  `const getFrontendDiagnosticsConfig = () => ({ enabled: globalThis.__taskforgeDebugEnabled });
   const logFrontendEvent = (...args) => { globalThis.__taskforgeDebugEvents.push(args); return true; };`,
);
const debug = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('course map events go into developer mode diagnostics, not browser console', () => {
  globalThis.__taskforgeDebugEvents = [];
  globalThis.__taskforgeDebugEnabled = false;
  debug.courseMapConsole('DIRTY_SET', { version: 73 }, 'warn');
  assert.equal(globalThis.__taskforgeDebugEvents.length, 0);

  globalThis.__taskforgeDebugEnabled = true;
  const called = debug.courseMapConsole('DIRTY_SET', { version: 73, reason: 'node-change' }, 'warn');
  assert.equal(called, true);
  assert.equal(globalThis.__taskforgeDebugEvents.length, 1);
  assert.equal(globalThis.__taskforgeDebugEvents[0][0], 'course-map');
  assert.equal(globalThis.__taskforgeDebugEvents[0][1], 'DIRTY_SET');
  assert.equal(globalThis.__taskforgeDebugEvents[0][2].version, 73);
  assert.equal(globalThis.__taskforgeDebugEvents[0][2].traceRevision, 'map-dev-diagnostics-314-r1');
  assert.equal(globalThis.__taskforgeDebugEvents[0][3], 'warn');
});

test('graph logging sends bounded shape, not task content or full documents', () => {
  globalThis.__taskforgeDebugEnabled = true;
  globalThis.__taskforgeDebugEvents = [];
  const graph = {
    nodes: [{ id: 'n1', title: 'do-not-export', type: 'code-test' }, { id: 'n2', type: 'locked' }],
    edges: [{ id: 'e1', settings: { synthetic: true } }],
  };
  debug.courseMapConsoleGraph('SOURCE_APPLIED', { ...graph, version: 80 });
  const [, event, detail] = globalThis.__taskforgeDebugEvents[0];
  assert.equal(event, 'SOURCE_APPLIED');
  assert.equal(detail.nodes, 2);
  assert.equal(detail.edges, 1);
  assert.equal(detail.lockedNodes, 1);
  assert.equal(detail.syntheticEdges, 1);
  assert.doesNotMatch(JSON.stringify(detail), /do-not-export/);
});
