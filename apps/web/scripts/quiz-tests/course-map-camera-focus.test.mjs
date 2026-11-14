import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../../src/features/course-assignments/courseMapCameraFocus.js', import.meta.url), 'utf8');
const {
  rememberSolvedForCourseMap,
  readRecentlySolvedForCourseMap,
  rememberCourseMapReturn,
  readCourseMapReturn,
  clearCourseMapReturn,
  chooseLearnerCameraTarget,
  getAssignmentCameraNode,
  collapsedCoursesHidingNode,
} = await import(`data:text/javascript,${encodeURIComponent(source)}`);

const originalWindow = globalThis.window;
const fakeStorage = () => {
  const records = new Map();
  return {
    records,
    getItem: (key) => records.get(key) ?? null,
    setItem: (key, value) => { records.set(String(key), String(value)); },
    removeItem: (key) => records.delete(String(key)),
  };
};
const localStorage = fakeStorage();
const sessionStorage = fakeStorage();

globalThis.window = { localStorage, sessionStorage };

const course = (id) => ({ id: `course:${id}`, type: 'course', entityId: id });
const task = (id, solved = false, type = 'code-test') => ({
  id: `assignment:${id}`, type, entityId: id,
  data: { entityId: id, entity: { id, solvedByCurrentUser: solved } },
});
const edge = (from, to) => ({ id: `${from}->${to}`, source: from, target: to });
const nodes = [course('root'), course('basics'), course('loops'), task('b1', true), task('b2', true), task('l1', true), task('l2', false)];
const edges = [
  edge('course:root', 'course:basics'), edge('course:root', 'course:loops'),
  edge('course:basics', 'assignment:b1'), edge('assignment:b1', 'assignment:b2'),
  edge('course:loops', 'assignment:l1'), edge('assignment:l1', 'assignment:l2'),
];

const focus = (props = {}) => chooseLearnerCameraTarget({ nodes, edges, rootCourseId: 'root', ...props });

test('returning from a task takes priority over solved progress and never selects another course', () => {
  assert.deepEqual(focus({ returnAssignmentId: 'l2', localSolved: [{ assignmentId: 'b2', at: Date.now() }] }),
    { nodeId: 'assignment:l2', reason: 'return-to-assignment' });
  assert.equal(focus({ returnAssignmentId: 'other', allowGraphFallback: false }), null);
  assert.equal(getAssignmentCameraNode(nodes, 'l2')?.id, 'assignment:l2');
});

test('last solved task is chosen by real local timestamps, not first unsolved BFS', () => {
  const now = Date.now();
  assert.deepEqual(focus({ localSolved: [
    { assignmentId: 'b2', at: now - 10_000 },
    { assignmentId: 'l1', at: now - 100 },
  ] }), { nodeId: 'assignment:l1', reason: 'last-accepted' });
});

test('last solved on another device uses server submission timestamps', () => {
  const now = Date.now();
  assert.deepEqual(focus({ serverSolved: [
    { assignmentId: 'b1', at: now - 600_000 },
    { assignmentId: 'b2', at: now - 400_000 },
    { assignmentId: 'l1', at: now - 150_000 },
    { assignmentId: 'other-course', at: now },
  ] }), { nodeId: 'assignment:l1', reason: 'last-accepted' });
});

test('server-confirmed accepted task outranks stale unsolved flags, but foreign tasks cannot hijack camera', () => {
  const now = Date.now();
  assert.deepEqual(focus({ serverSolved: [
    { assignmentId: 'l2', at: now },
    { assignmentId: 'outside', at: now + 1 },
    { assignmentId: 'b1', at: now - 30 },
  ] }), { nodeId: 'assignment:l2', reason: 'last-accepted' });
});

test('locally just confirmed success is trusted while projection is catching up', () => {
  const draftNodes = [course('root'), task('new', false)];
  const match = chooseLearnerCameraTarget({
    nodes: draftNodes, edges: [edge('course:root', 'assignment:new')], rootCourseId: 'root',
    localSolved: [{ assignmentId: 'new', at: Date.now(), localConfirmed: true }], allowGraphFallback: false,
  });
  assert.equal(match?.nodeId, 'assignment:new');
  assert.equal(chooseLearnerCameraTarget({
    nodes: draftNodes, edges: [edge('course:root', 'assignment:new')], rootCourseId: 'root',
    localSolved: [{ assignmentId: 'new', at: Date.now() - 8 * 60_000, localConfirmed: true }],
    allowGraphFallback: false,
  }), null);
});

test('with no dated history, graph falls back to solved task, then course start', () => {
  assert.deepEqual(focus(), { nodeId: 'assignment:b2', reason: 'solved-graph-fallback' });
  const emptyNodes = [course('empty'), task('first', false)];
  assert.deepEqual(chooseLearnerCameraTarget({ nodes: emptyNodes, edges: [edge('course:empty', 'assignment:first')], rootCourseId: 'empty' }),
    { nodeId: 'course:empty', reason: 'course-start' });
});

test('completed branch is expanded only if it actually hides camera target', () => {
  assert.deepEqual(collapsedCoursesHidingNode('assignment:b2', nodes, edges, new Set(['course:basics', 'course:loops'])), ['course:basics']);
  assert.deepEqual(collapsedCoursesHidingNode('assignment:l1', nodes, edges, new Set(['course:basics', 'course:loops'])), ['course:loops']);
  assert.deepEqual(collapsedCoursesHidingNode('assignment:b1', nodes, edges, new Set()), []);
});

test('local solved markers are user-scoped and survive a reload', () => {
  localStorage.records.clear();
  const now = Date.now();
  rememberSolvedForCourseMap({ userId: 'alice', courseId: 'basics', assignmentId: 'b1', at: now - 300 });
  rememberSolvedForCourseMap({ userId: 'alice', courseId: 'loops', assignmentId: 'l1', at: now - 150 });
  rememberSolvedForCourseMap({ userId: 'bob', courseId: 'other', assignmentId: 'secret', at: now - 60 });
  assert.deepEqual(readRecentlySolvedForCourseMap('alice').map((entry) => entry.assignmentId), ['l1', 'b1']);
  assert.deepEqual(readRecentlySolvedForCourseMap('bob').map((entry) => entry.assignmentId), ['secret']);
  assert.equal(readRecentlySolvedForCourseMap('charlie').length, 0);
});

test('return hint only matches original React Router map history key and is consumed', () => {
  sessionStorage.records.clear();
  rememberCourseMapReturn({ userId: 'alice', rootCourseId: 'root', mapLocationKey: 'old-map-key', assignmentId: 'b2' });
  assert.equal(readCourseMapReturn({ userId: 'alice', rootCourseId: 'root', mapLocationKey: 'old-map-key' }), 'b2');
  assert.equal(readCourseMapReturn({ userId: 'alice', rootCourseId: 'root', mapLocationKey: 'new-entry-from-courses' }), '');
  assert.equal(readCourseMapReturn({ userId: 'bob', rootCourseId: 'root', mapLocationKey: 'old-map-key' }), '');
  clearCourseMapReturn({ userId: 'alice', rootCourseId: 'root', mapLocationKey: 'old-map-key' });
  assert.equal(readCourseMapReturn({ userId: 'alice', rootCourseId: 'root', mapLocationKey: 'old-map-key' }), '');
});

const remoteSource = await readFile(new URL('../../src/features/course-assignments/courseMapRecentSolved.js', import.meta.url), 'utf8');
const fakeHistory = { solutions: [], image: [], test: [], math: [], calls: [] };
globalThis.__taskforgeCameraMock = fakeHistory;
const importReplacements = [
  'const getMySolutions = async (arg) => { globalThis.__taskforgeCameraMock.calls.push("solutions"); return globalThis.__taskforgeCameraMock.solutions; };',
  'const getMyImageSolutions = async () => { globalThis.__taskforgeCameraMock.calls.push("image"); return globalThis.__taskforgeCameraMock.image; };',
  'const getMyMathAttempts = async () => { globalThis.__taskforgeCameraMock.calls.push("math"); return globalThis.__taskforgeCameraMock.math; };',
  'const getMyTaskTestAttempts = async () => { globalThis.__taskforgeCameraMock.calls.push("test"); return globalThis.__taskforgeCameraMock.test; };',
];
const remoteWithMocks = remoteSource.replace(/^import .+;$/gm, '');
const { fetchRecentSolvedForCourseMap } = await import(`data:text/javascript,${encodeURIComponent(importReplacements.join('\n') + '\n' + remoteWithMocks)}`);

test('backend solution history accepts code/SQL/image/math/quiz and sorts across kinds', async () => {
  const now = new Date().toISOString();
  fakeHistory.solutions = [
    { assignmentId: 'b1', status: 'WrongAnswer', submittedAt: now },
    { assignmentId: 'b2', status: 'Accepted', createdAtUtc: '2026-09-01T00:00:00Z' },
  ];
  fakeHistory.image = [{ assignmentId: 'img', passed: true, createdAtUtc: '2026-09-02T00:00:00Z' }];
  fakeHistory.test = [{ taskAssignmentId: 'quiz', passed: true, submittedAt: '2026-09-03T00:00:00Z' }];
  fakeHistory.math = [{ taskAssignmentId: 'math1', passed: true, submittedAt: '2026-09-04T00:00:00Z' }];
  fakeHistory.calls = [];
  const history = await fetchRecentSolvedForCourseMap(['code-test', 'sql-test', 'image-code', 'math', 'test']);
  assert.deepEqual(history.map((item) => item.assignmentId), ['math1', 'quiz', 'img', 'b2']);
  assert.deepEqual(fakeHistory.calls.sort(), ['image', 'math', 'solutions', 'test']);
});

test('a course with code only never requests unrelated histories', async () => {
  fakeHistory.calls = [];
  await fetchRecentSolvedForCourseMap(['code-test']);
  assert.deepEqual(fakeHistory.calls, ['solutions']);
});

test('normal and browser-back routes retain task IDs without changing editor view', async () => {
  const flowSource = await readFile(new URL('../../src/features/course-assignments/components/CourseFlowEditor.jsx', import.meta.url), 'utf8');
  const solveSource = await readFile(new URL('../../src/features/assignment-solve/components/AssignmentSolvePresentation.jsx', import.meta.url), 'utf8');
  const nextSource = await readFile(new URL('../../src/features/assignment-solve/useAssignmentNextNavigation.js', import.meta.url), 'utf8');
  const progressSource = await readFile(new URL('../../src/features/course-assignments/courseProgressionFreshness.js', import.meta.url), 'utf8');
  assert.match(flowSource, /rememberCourseMapReturn\(/);
  assert.match(flowSource, /readCourseMapReturn\(/);
  assert.match(flowSource, /completedCourseLearnerView\.collapsedCourseIds/);
  assert.match(flowSource, /validReturnAssignmentId = getAssignmentCameraNode\(nodes, returnAssignmentId\)/);
  assert.match(flowSource, /if \(!learnerProjectionReady\) return;/);
  assert.match(flowSource, /if \(editorMode \|\| focusCourseId/);
  assert.doesNotMatch(flowSource, /learnerEntryFocusCandidate/);
  assert.match(solveSource, /courseMapReturnAssignmentId: assignmentId/);
  assert.match(solveSource, /focusAssignment=/);
  assert.match(nextSource, /courseMapEntryKey: context\.courseMapEntryKey/);
  assert.match(progressSource, /rememberSolvedForCourseMap\(/);
});

test.after(() => {
  globalThis.window = originalWindow;
  delete globalThis.__taskforgeCameraMock;
});
