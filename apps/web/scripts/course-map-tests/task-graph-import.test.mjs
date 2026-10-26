import test from 'node:test';
import assert from 'node:assert/strict';
import { loadSourceModule } from '../test-utils/load-source-module.mjs';

const json = await loadSourceModule(new URL('../../src/features/course-assignments/courseTaskGraphJson.js', import.meta.url));
const model = await loadSourceModule(new URL('../../src/features/course-assignments/courseTaskGraphImport.js', import.meta.url));
const ids = await loadSourceModule(new URL('../../src/features/course-assignments/courseMapModel.js', import.meta.url));
const snapshots = await loadSourceModule(new URL('../../src/features/course-assignments/taskGraphPreviewSnapshot.js', import.meta.url));
const uid = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const rootId = uid(999);
const root = () => ({ id: ids.courseNodeId(rootId), entityId: rootId, type: 'course', position: { x: 0, y: 0 } });
const task = (key, extra = {}) => ({ key, title: key, type: 'code-test', language: 'python', testCases: [{ input: '', expectedOutput: 'ok' }], ...extra });
const graph = (extra = {}) => ({ schemaVersion: 5, format: 'taskforge-task-graph', scopes: [...json.TASK_GRAPH_SCOPES], courses: [], tasks: [], connections: [], ...extra });
const diff = (input, before = graph(), options = {}, preflight = {}) => json.buildTaskGraphImportDiff(input, before, options, preflight);
const commit = (input) => ({
  taskGraph: {
    ...structuredClone(input),
    courses: input.courses.map((row, i) => ({ ...row, id: row.id || uid(200 + i) })),
    tasks: input.tasks.map((row, i) => ({ ...row, assignmentId: row.id || uid(1 + i) })),
  },
  assignments: input.tasks.map((row, i) => ({ ...row, id: row.id || uid(1 + i) })),
});
const prepare = (request, state = {}) => model.prepareTaskGraphImport({ request, nodes: [root()], edges: [], assignments: [], courseId: rootId, ...state });
const connected = graph({ tasks: [task('a', { id: uid(1) }), task('b', { id: uid(2) }), task('c', { id: uid(3) })],
  connections: [{ from: '$course', to: 'a' }, { from: 'a', to: 'b' }, { from: 'b', to: 'c' }],
  layout: { positions: { $course: { x: 0, y: 0 }, a: { x: 540, y: 0 }, b: { x: 900, y: 0 }, c: { x: 1260, y: 0 } } } });

test('server bindings are the preview and apply identity for a key without source UUID', () => {
  const source = graph({ scopes: ['content', 'connections'], courses: [{ key: 'contest', title: 'Contest' }],
    tasks: [task('example', { course: 'contest' })], connections: [{ from: '$course', to: 'contest' }, { from: 'contest', to: 'example' }] });
  const bound = snapshots.applyServerGraphBindings(source, { bindings: {
    courses: [{ key: 'contest', id: uid(200) }], tasks: [{ key: 'example', id: uid(1) }],
  } });
  assert.equal(bound.courses[0].id, uid(200));
  assert.equal(bound.tasks[0].id, uid(1));
  assert.equal(source.tasks[0].id, undefined);
  assert.ok(bound.scopes.includes('ids'));
  const repeated = diff(bound, bound);
  assert.equal(repeated.createCount, 0);
  assert.equal(repeated.courseCreateCount, 0);
  assert.equal(repeated.connectionAddedCount, 0);
});

test('additive connections are the safe default when importing another batch', () => {
  const input = graph({ scopes: ['ids', 'connections'], tasks: [{ key: 'a', id: uid(1) }, { key: 'b', id: uid(2) }],
    connections: [{ from: '$course', to: 'b' }] });
  const result = diff(input, connected);
  assert.equal(result.connectionRemovedCount, 0);
  assert.equal(result.connectionAddedCount, 1);
});

test('official mega: response DTOs place new entities despite a stale/empty tree', () => {
  const request = commit(json.TASK_GRAPH_MEGA_EXAMPLE);
  const result = prepare(request);
  assert.equal(result.error, undefined);
  assert.equal(result.pending, undefined);
  assert.equal(result.edges.length, 17);
  assert.equal(result.nodes.length, 14);
  assert.equal(result.assignments.length, 13);
  assert.equal(result.unplacedAssignmentIds.length, 1);
  for (const [key, position] of Object.entries(request.taskGraph.layout.positions)) {
    const nodeId = key === '$course' ? rootId : (request.taskGraph.tasks.find(x => x.key === key)?.assignmentId || request.taskGraph.courses.find(x => x.key === key)?.id);
    assert.deepEqual(result.nodes.find(x => x.entityId === nodeId)?.position, position);
  }
});

test('official mega preview agrees with one-pass application', () => {
  const preview = diff(json.TASK_GRAPH_MEGA_EXAMPLE);
  assert.equal(preview.validationErrorCount, 0, JSON.stringify(preview.graphIssues));
  assert.equal(preview.createCount, 13);
  assert.equal(preview.courseCreateCount, 1);
  assert.equal(preview.connectionAddedCount, 17);
  assert.equal(preview.unplacedCount, 1);
  assert.equal(preview.placements.filter(x => x.placed).length, 13);
});

test('repeat only the map stage with resolved IDs: no duplicate nodes or edges', () => {
  const request = commit(json.TASK_GRAPH_MEGA_EXAMPLE);
  const first = prepare(request);
  const second = prepare(request, first);
  assert.equal(second.error, undefined);
  assert.equal(second.nodes.length, first.nodes.length);
  assert.equal(second.edges.length, 17);
  assert.equal(new Set(second.nodes.map(x => x.id)).size, second.nodes.length);
  assert.equal(new Set(second.edges.map(x => `${x.source}/${x.target}`)).size, 17);
});

test('missing committed entity is a terminal diagnosable result, not an endless wait', () => {
  const request = commit(graph({ tasks: [task('a')], connections: [{ from: '$course', to: 'a' }] }));
  request.assignments = [];
  const result = prepare(request);
  assert.equal(result.error, 'missing-assignments');
  assert.deepEqual(result.missingAssignmentIds, [uid(1)]);
});

test('unresolved key does not silently discard a new node', () => {
  const result = prepare({ assignments: [], taskGraph: graph({ tasks: [task('a')] }) });
  assert.equal(result.error, 'unresolved-keys');
  assert.deepEqual(result.unresolvedKeys, ['a']);
});

test('new branch from a course with outgoing edges keeps unmentioned existing branch', () => {
  const existing = prepare(commit(graph({ tasks: [task('old', { id: uid(70) })], connections: [{ from: '$course', to: 'old' }] })));
  const request = commit(graph({ tasks: [task('new')], connections: [{ from: '$course', to: 'new' }] }));
  const result = prepare(request, existing);
  assert.equal(result.edges.length, 2);
  assert.equal(result.nodes.length, 3);
  assert.equal(result.assignments.length, 2);
});

test('new branch from an existing nested course keeps its unmentioned outgoing edge', () => {
  const courses = [{ key: 'sub', id: uid(200), title: 'Nested' }];
  const old = graph({ courses, tasks: [task('old', { id: uid(70), course: 'sub' })], connections: [{ from: '$course', to: 'sub' }, { from: 'sub', to: 'old' }] });
  const existing = prepare(commit(old));
  const input = graph({ courses, tasks: [task('new', { course: 'sub' })], connections: [{ from: '$course', to: 'sub' }, { from: 'sub', to: 'new' }] });
  const result = prepare(commit(input), existing);
  assert.equal(result.edges.length, 3);
  assert.equal(result.nodes.length, 4);
});

test('automatic placement respects measured root width and does not overlap it', () => {
  const wideRoot = { ...root(), width: 790, height: 350 };
  const result = prepare(commit(graph({ tasks: [task('a')], connections: [{ from: '$course', to: 'a' }] })), { nodes: [wideRoot] });
  const child = result.nodes.find(x => x.entityId === uid(1));
  assert.ok(child.position.x >= 790 + 60, JSON.stringify(child.position));
});

test('automatic positions do not overlap reserved explicit positions', () => {
  const request = commit(graph({ tasks: [task('a'), task('b')], connections: [{ from: '$course', to: 'a' }, { from: '$course', to: 'b' }], layout: { positions: { a: { x: 536, y: 0 } } } }));
  const result = prepare(request);
  const a = result.nodes.find(x => x.entityId === uid(1));
  const b = result.nodes.find(x => x.entityId === uid(2));
  const size = model.taskGraphNodeSize(a);
  assert.ok(b.position.x >= a.position.x + size.width || b.position.y >= a.position.y + size.height || b.position.y + model.taskGraphNodeSize(b).height <= a.position.y);
});

test('disabled connections and absent datasets do not require placeholder arrays', () => {
  const input = graph({ scopes: ['content', 'checks'], tasks: [task('new')] });
  delete input.connections;
  assert.deepEqual(json.validateTaskGraphPayload(input).issues, []);
  const result = diff(input, connected);
  assert.equal(result.connectionRemovedCount, 0);
  assert.equal(result.connectionUnchangedCount, 3);
  assert.equal(result.unplacedCount, 1);
});

test('active topology requires connections; disabled switches allow absence', () => {
  const input = graph(); delete input.connections;
  assert.ok(json.validateTaskGraphPayload(input).issues.some(x => x.path === '$.connections'));
  assert.deepEqual(json.validateTaskGraphPayload(input, { updateConnections: false, updateConnectionAccess: false }).issues, []);
});

test('v3 empty datasets is legal, and explicit scopes are respected', () => {
  const input = graph({ schemaVersion: 3, scopes: ['content', 'checks'], datasets: [], tasks: [task('a')] });
  delete input.connections;
  const result = json.validateTaskGraphPayload(input);
  assert.deepEqual(result.issues, []);
  assert.deepEqual(result.graph.scopes, ['content', 'checks']);
});

test('v3 real SQL resources still require v5', () => {
  const input = graph({ schemaVersion: 3, scopes: ['content'], datasets: [{ key: 'db' }] });
  assert.ok(json.validateTaskGraphPayload(input).issues.some(x => x.path === '$.datasets'));
});

test('layout-only root movement is a real operation', () => {
  const input = graph({ scopes: ['layout'], layout: { positions: { $course: { x: -140, y: 0 } } } });
  const result = diff(input, connected);
  assert.equal(result.validationErrorCount, 0);
  assert.equal(result.total, 1);
  assert.equal(result.layoutPositionCount, 1);
  assert.equal(Boolean(result.hasImportWork), true);
  assert.equal(result.connectionUnchangedCount, 3);
});

test('viewport-only import is counted, and turning layout off removes it', () => {
  const input = graph({ scopes: ['layout'], layout: { viewport: { x: 10, y: 20, zoom: 0.8 } } });
  assert.equal(diff(input, connected).total, 1);
  assert.equal(diff(input, connected, { updateLayout: false }).total, 0);
});

test('disabled topology counts actual placement, not connectivity in the input JSON', () => {
  const input = graph({ scopes: ['ids'], tasks: [{ key: 'a', id: uid(1) }, { key: 'b', id: uid(2) }] });
  const result = diff(input, connected);
  assert.equal(result.validationErrorCount, 0, JSON.stringify(result.graphIssues));
  assert.equal(result.unplacedCount, 0);
  assert.equal(result.connectionUnchangedCount, 3);
  assert.equal(result.connectionRemovedCount, 0);
});

test('replace removes edges between included nodes, not arbitrary cross-boundary edges', () => {
  const input = graph({ scopes: ['ids', 'connections'], tasks: [{ key: 'a', id: uid(1) }, { key: 'b', id: uid(2) }], connections: [{ from: '$course', to: 'a' }, { from: '$course', to: 'b' }] });
  const result = diff(input, connected, { connectionMode: 'replace' });
  assert.equal(result.connectionAddedCount, 1);
  assert.equal(result.connectionRemovedCount, 1);
  assert.equal(result.connectionUnchangedCount, 2);
  assert.equal(result.requiresConnectionDeleteConfirmation, true);
  assert.equal(result.removedConnectionRows[0].from, 'a');
  assert.equal(result.removedConnectionRows[0].to, 'b');
  assert.equal(diff(input, connected, { allowConnectionDeletes: true, connectionMode: 'replace' }).requiresConnectionDeleteConfirmation, false);
});

test('merge is additive and preserves nodes absent from incoming connections', () => {
  const input = graph({ scopes: ['ids', 'connections'], tasks: [{ key: 'a', id: uid(1) }, { key: 'b', id: uid(2) }], connections: [{ from: '$course', to: 'b' }] });
  const result = diff(input, connected, { connectionMode: 'merge' });
  assert.equal(result.validationErrorCount, 0);
  assert.equal(result.connectionAddedCount, 1);
  assert.equal(result.connectionRemovedCount, 0);
  assert.equal(result.connectionUnchangedCount, 3);
  assert.equal(result.unplacedCount, 0);
});

test('cycle through retained cross-boundary edges blocks both modes', () => {
  const input = graph({ scopes: ['ids', 'connections'], tasks: [{ key: 'a', id: uid(1) }, { key: 'c', id: uid(3) }], connections: [{ from: '$course', to: 'a' }, { from: 'c', to: 'a' }] });
  for (const connectionMode of ['merge', 'replace']) {
    const result = diff(input, connected, { connectionMode });
    assert.ok(result.graphIssues.some(x => x.path === '$.connections'), connectionMode);
  }
});

test('effects-only import changes an existing edge but not topology', () => {
  const input = graph({ scopes: ['ids', 'connectionAccess'], tasks: [{ key: 'a', id: uid(1) }], connections: [{ from: '$course', to: 'a', access: { hidden: 'start' } }] });
  const result = diff(input, connected);
  assert.equal(result.connectionAddedCount, 0);
  assert.equal(result.connectionRemovedCount, 0);
  assert.equal(result.connectionUnchangedCount, 3);
  assert.equal(result.connectionAccessChangedCount, 1);
});

test('effects disabled: a repeated topology import retains previous effects', () => {
  const before = structuredClone(connected);
  before.connections[0].access = { hidden: 'start', sequential: 'start' };
  const result = diff(connected, before, { updateConnectionAccess: false });
  assert.equal(result.connectionAccessChangedCount, 0);
  assert.equal(result.connectionRemovedCount, 0);
  assert.equal(result.connectionUnchangedCount, 3);
});

test('explicit deletion removes incident edges even when topology is off', () => {
  const result = diff(graph({ scopes: ['ids'], deleteTasks: [uid(2)] }), connected);
  assert.equal(result.deleteCount, 1);
  assert.equal(result.connectionRemovedCount, 2);
  assert.equal(result.requiresDeleteConfirmation, true);
  assert.equal(result.requiresConnectionDeleteConfirmation, true);
});

test('server preflight UUID conflicts block preview without exposing foreign metadata', () => {
  const issue = { path: '$.tasks[0].id', message: 'UUID conflict' };
  const result = diff(graph({ tasks: [task('a', { id: uid(50) })] }), connected, {}, { issues: [issue] });
  assert.equal(result.validationErrorCount, 1);
  assert.deepEqual(result.graphIssues, [issue]);
});

test('existing course metadata is explicitly reported as ignored', () => {
  const before = graph({ courses: [{ key: 'sub', id: uid(200), title: 'Old' }] });
  const input = graph({ scopes: ['ids', 'content'], courses: [{ key: 'sub', id: uid(200), title: 'New' }] });
  const result = diff(input, before);
  assert.equal(result.courseCreateCount, 0);
  assert.equal(result.total, 0);
  assert.ok(result.warnings.some(x => x.includes('title')));
});

test('existing task retains its actual parent when course is omitted', () => {
  const before = graph({ courses: [{ key: 'sub', id: uid(200), title: 'Subcourse' }], tasks: [task('a', { id: uid(1), course: 'sub' })] });
  const input = graph({ scopes: ['ids'], tasks: [{ key: 'ref', id: uid(1) }] });
  const result = diff(input, before);
  assert.equal(result.rows[0].courseLabel, 'Subcourse');
  assert.equal(result.validationErrorCount, 0);
});

test('export without IDs and content is blocked, partial ID exports remain valid', () => {
  assert.ok(json.taskGraphExportIssue({ includeIds: false, includeContent: false }));
  assert.equal(json.taskGraphExportIssue({ includeIds: true, includeContent: false }), '');
  assert.equal(json.taskGraphExportIssue({ includeIds: false, includeContent: true }), '');
});

test('name-only repeated import is a new entity and requires duplicate confirmation', () => {
  const result = diff(graph({ tasks: [task('a')] }), connected);
  assert.equal(result.createCount, 1);
  assert.equal(result.duplicateTitleCount, 1);
  assert.equal(result.requiresDuplicateConfirmation, true);
  assert.equal(diff(graph({ tasks: [task('a')] }), connected, { allowDuplicates: true }).requiresDuplicateConfirmation, false);
});

test('250 new nodes: preview and one-pass model application keep all branches', () => {
  const input = graph({ tasks: Array.from({ length: 250 }, (_, i) => task(`t${i}`)), connections: Array.from({ length: 250 }, (_, i) => ({ from: '$course', to: `t${i}` })) });
  const result = diff(input, connected);
  assert.equal(result.validationErrorCount, 0);
  assert.equal(result.connectionAddedCount, 250);
  assert.equal(result.connectionUnchangedCount, 3);
  const applied = prepare(commit(input));
  assert.equal(applied.nodes.length, 251);
  assert.equal(applied.edges.length, 250);
  assert.equal(applied.unplacedAssignmentIds.length, 0);
});

test('embedded examples validate with no fixed course UUID in mega', () => {
  assert.equal(json.TASK_GRAPH_MEGA_EXAMPLE.courses[0].id, undefined);
  for (const example of json.TASK_GRAPH_EXAMPLES) {
    const payload = JSON.parse(json.taskGraphExampleToText(example));
    assert.equal(json.validateTaskGraphPayload(payload).issues.length, 0, example.title);
  }
});

test('a pending import cannot be applied to a different course after navigation', () => {
  const request = commit(graph({ tasks: [task('a')], connections: [{ from: '$course', to: 'a' }] }));
  request.courseId = uid(888);
  assert.equal(prepare(request).error, 'course-changed');
});

test('UUID case does not create duplicate map identities or missed deletions', () => {
  const mixedId = 'abcdefab-abcd-4abc-8abc-abcdefabcdef';
  const before = graph({ tasks: [task('a', { id: mixedId })], connections: [{ from: '$course', to: 'a' }] });
  const repeated = structuredClone(before);
  repeated.tasks[0].id = mixedId.toUpperCase();
  const result = diff(repeated, before);
  assert.equal(result.createCount, 0);
  assert.equal(result.connectionAddedCount, 0);
  assert.equal(result.connectionRemovedCount, 0);
  assert.equal(result.unplacedCount, 0);
  const deleted = diff(graph({ scopes: ['ids'], deleteTasks: [mixedId.toUpperCase()] }), before);
  assert.equal(deleted.deleteCount, 1);
  assert.equal(deleted.connectionRemovedCount, 1);
});

test('JSON parser diagnostics do not leak English engine messages or input excerpts', () => {
  const text='{\n broken';
  const message=json.taskGraphJsonParseMessage(new SyntaxError('Unexpected token SECRET at position 3'),text);
  assert.ok(message.includes('2')); assert.ok(!message.includes('SECRET')); assert.ok(!message.includes('Unexpected'));
  assert.ok(!json.taskGraphJsonParseMessage(new SyntaxError('Unexpected end of JSON input')).includes('Unexpected'));
});

test('preview does not trim test strings, reorder cases, or alter Unicode', () => {
  const tests=[{input:'\u0416\r\n  ',expectedOutput:'\u4e2d\n\t'},{input:'',expectedOutput:'ok  '}];
  const input=graph({tasks:[task('bytes',{testCases:structuredClone(tests)})]});const before=JSON.stringify(input);
  const result=diff(input);assert.equal(result.validationErrorCount,0);assert.equal(JSON.stringify(input),before);
  const applied=prepare(commit(input));assert.deepEqual(applied.assignments[0].testCases,tests);
});

test('layout preview uses preserved paths even when incoming keys differ and no connections are sent', () => {
  const before = graph({ courses: [{ key: 'old-year', id: uid(200), title: 'Contest 2025' }],
    tasks: [task('old-task', { id: uid(1), course: 'old-year' })],
    connections: [{ from: '$course', to: 'old-year' }, { from: 'old-year', to: 'old-task' }],
    layout: { positions: { $course: { x: 0, y: 0 }, 'old-year': { x: 500, y: 0 }, 'old-task': { x: 1000, y: 0 } } } });
  const input = { schemaVersion: 5, format: 'taskforge-task-graph', scopes: ['ids', 'layout'],
    courses: [{ key: 'year', id: uid(200) }], tasks: [{ key: 'task', id: uid(1) }],
    layout: { positions: { year: { x: 700, y: 100 }, task: { x: 1200, y: 100 } } } };
  const result = diff(input, before);
  assert.equal(result.validationErrorCount, 0, JSON.stringify(result.graphIssues));
  assert.equal(result.courseRows[0].reachableFromRoot, true);
  assert.equal(result.courseRows[0].taskCount, 1);
  assert.equal(result.courseRows[0].directLinkCount, 1);
  assert.equal(result.courseRows[0].reachableCount, 1);
  assert.deepEqual(result.courseRows[0].unreachableKeys, []);
  assert.equal(result.connectionAddedCount, 0); assert.equal(result.connectionRemovedCount, 0);
});
