import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/course-assignments/courseMapLearnerViewModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function course(id, solved, total) {
  return { id, type: 'course', entityId: id, data: { entity: { id, title: id }, progress: { solved, total } } };
}

function task(id, solved = true) {
  return {
    id,
    type: 'code-test',
    entityId: id,
    data: { entity: { id, title: id, solvedByCurrentUser: solved } },
  };
}

function edge(id, source, target) {
  return { id, source, target };
}

test('completed course hides its solved task segment and reconnects to the next course', () => {
  const nodes = [course('course-a', 2, 2), task('task-1'), task('task-2'), course('course-b', 0, 2)];
  const edges = [
    edge('e1', 'course-a', 'task-1'),
    edge('e2', 'task-1', 'task-2'),
    edge('e3', 'task-2', 'course-b'),
  ];

  const view = model.buildCompletedCourseLearnerView(nodes, edges);
  assert.deepEqual(view.nodes.map((node) => node.id), ['course-a', 'course-b']);
  assert.equal(view.collapsedCourseIds.has('course-a'), true);
  assert.equal(view.hiddenCountByCourseId.get('course-a'), 2);
  assert.equal(view.edges.length, 1);
  assert.equal(view.edges[0].source, 'course-a');
  assert.equal(view.edges[0].target, 'course-b');
  assert.equal(view.edges[0].data.learnerCollapsed, true);
});

test('expanded completed course restores the original task nodes and edges', () => {
  const nodes = [course('course-a', 2, 2), task('task-1'), task('task-2'), course('course-b', 0, 1)];
  const edges = [edge('e1', 'course-a', 'task-1'), edge('e2', 'task-1', 'task-2'), edge('e3', 'task-2', 'course-b')];

  const view = model.buildCompletedCourseLearnerView(nodes, edges, { expandedCourseIds: ['course-a'] });
  assert.equal(view.nodes.length, 4);
  assert.equal(view.edges.length, 3);
  assert.equal(view.collapsedCourseIds.size, 0);
});

test('shared merge nodes stay visible when another branch still depends on them', () => {
  const nodes = [course('course-a', 1, 1), task('inside'), task('outside', false), task('merge', false), course('course-b', 0, 1)];
  const edges = [
    edge('e1', 'course-a', 'inside'),
    edge('e2', 'inside', 'merge'),
    edge('e3', 'outside', 'merge'),
    edge('e4', 'merge', 'course-b'),
  ];

  const view = model.buildCompletedCourseLearnerView(nodes, edges);
  assert.equal(view.hiddenNodeIds.has('inside'), true);
  assert.equal(view.hiddenNodeIds.has('merge'), false);
  assert.equal(view.nodes.some((node) => node.id === 'merge'), true);
  assert.equal(view.edges.some((row) => row.source === 'course-a' && row.target === 'merge'), true);
  assert.equal(view.edges.some((row) => row.source === 'outside' && row.target === 'merge'), true);
});

test('course action opens first unsolved descendant, then falls back to the last task', () => {
  const nodes = [course('course-a', 1, 2), task('task-1', true), task('task-2', false), task('task-3', true)];
  const edges = [edge('e1', 'course-a', 'task-1'), edge('e2', 'task-1', 'task-2'), edge('e3', 'task-2', 'task-3')];

  assert.deepEqual(model.findCourseLearnerAssignmentAction('course-a', nodes, edges), {
    assignmentId: 'task-2',
    hasUnsolved: true,
  });

  const solvedNodes = nodes.map((node) => node.type === 'course' ? node : { ...node, data: { entity: { ...node.data.entity, solvedByCurrentUser: true } } });
  assert.deepEqual(model.findCourseLearnerAssignmentAction('course-a', solvedNodes, edges), {
    assignmentId: 'task-3',
    hasUnsolved: false,
  });
});

test('stale 100 percent course does not collapse an unsolved task', () => {
  const nodes = [course('course-a', 2, 2), task('task-1', true), task('task-2', false), course('course-b', 0, 1)];
  const edges = [edge('e1', 'course-a', 'task-1'), edge('e2', 'task-1', 'task-2'), edge('e3', 'task-2', 'course-b')];

  const view = model.buildCompletedCourseLearnerView(nodes, edges);
  assert.equal(view.collapsedCourseIds.has('course-a'), false);
  assert.equal(view.nodes.length, 4);
  assert.equal(view.edges.length, 3);
});

test('locked continuation prevents compaction and course action never jumps through the lock', () => {
  const locked = {
    id: 'locked-next',
    type: 'locked',
    data: { settings: { title: 'Продолжение закрыто' } },
  };
  const nodes = [course('course-a', 1, 1), task('task-1', true), locked, task('task-2', false)];
  const edges = [edge('e1', 'course-a', 'task-1'), edge('e2', 'task-1', 'locked-next'), edge('e3', 'locked-next', 'task-2')];

  const view = model.buildCompletedCourseLearnerView(nodes, edges);
  assert.equal(view.collapsedCourseIds.has('course-a'), false);
  assert.equal(view.nodes.length, 4);
  assert.deepEqual(model.findCourseLearnerAssignmentAction('course-a', nodes, edges), {
    assignmentId: 'task-1',
    hasUnsolved: false,
  });
});

test('course action follows visual order when a course branches', () => {
  const left = { ...task('left', false), position: { x: 200, y: 0 } };
  const right = { ...task('right', false), position: { x: 500, y: 0 } };
  const root = { ...course('course-a', 0, 2), position: { x: 0, y: 0 } };
  const nodes = [root, right, left];
  const edges = [edge('right-edge', 'course-a', 'right'), edge('left-edge', 'course-a', 'left')];

  assert.deepEqual(model.findCourseLearnerAssignmentAction('course-a', nodes, edges), {
    assignmentId: 'left',
    hasUnsolved: true,
  });
});

test('internal converging branches cannot disguise an unsolved task as an external merge', () => {
  const root = course('course-a', 3, 3);
  const left = task('left', true);
  const right = task('right', true);
  const merge = task('merge', false);
  const nodes = [root, left, right, merge];
  const edges = [
    edge('e1', 'course-a', 'left'),
    edge('e2', 'course-a', 'right'),
    edge('e3', 'left', 'merge'),
    edge('e4', 'right', 'merge'),
  ];

  const view = model.buildCompletedCourseLearnerView(nodes, edges);
  assert.equal(view.collapsedCourseIds.has('course-a'), false);
  assert.equal(view.nodes.length, 4);
});
