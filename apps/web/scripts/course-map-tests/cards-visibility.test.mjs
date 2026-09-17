import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/course-assignments/courseAssignmentsModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('learner cards expose only entities present in the learner map projection', () => {
  const learningMap = {
    document: {
      nodes: [
        { id: 'course-root-node', type: 'course', entityId: 'course-root' },
        { id: 'course-open-node', type: 'course', entityId: 'course-open' },
        { id: 'task-open-node', type: 'code-test', entityId: 'task-open' },
        { id: 'locked-node', type: 'locked', entityId: 'task-secret' },
      ],
    },
  };

  const visibility = model.collectLearnerVisibleEntityIds(learningMap);
  assert.deepEqual([...visibility.courseIds].sort(), ['course-open', 'course-root']);
  assert.deepEqual([...visibility.assignmentIds], ['task-open']);

  const courses = [
    { id: 'course-open', parentCourseId: 'course-root' },
    { id: 'course-locked', parentCourseId: 'course-root' },
    { id: 'course-other-parent', parentCourseId: 'other' },
  ];
  const assignments = [
    { id: 'task-open' },
    { id: 'task-secret' },
  ];

  assert.deepEqual(
    model.filterLearnerCardCourses(courses, 'course-root', visibility.courseIds).map((row) => row.id),
    ['course-open'],
  );
  assert.deepEqual(
    model.filterLearnerCardAssignments(assignments, visibility.assignmentIds).map((row) => row.id),
    ['task-open'],
  );
});

test('learner card filtering fails closed when projection visibility is missing', () => {
  assert.deepEqual(model.filterLearnerCardCourses([{ id: 'course-a', parentCourseId: 'root' }], 'root', []), []);
  assert.deepEqual(model.filterLearnerCardAssignments([{ id: 'task-a' }], []), []);
});

test('learner cards expose scoped locked continuation without leaking hidden target identity', () => {
  const learningMap = {
    document: {
      nodes: [
        { id: 'root-course', type: 'course', entityId: 'course-root' },
        { id: 'root-task', type: 'code-test', entityId: 'task-open' },
        {
          id: 'root-lock',
          type: 'locked',
          entityId: 'task-secret',
          position: { x: 400, y: 20 },
          settings: {
            title: 'Продолжение закрыто',
            requirement: 'Решите «Ввод», чтобы открыть следующее задание.',
          },
        },
        { id: 'child-course', type: 'course', entityId: 'course-child' },
        { id: 'child-task', type: 'test', entityId: 'task-child-open' },
        {
          id: 'child-lock',
          type: 'locked',
          position: { x: 800, y: 20 },
          settings: { requirement: 'Решите предыдущее задание, чтобы открыть продолжение.' },
        },
      ],
      edges: [
        { id: 'e1', source: 'root-course', target: 'root-task' },
        { id: 'e2', source: 'root-task', target: 'root-lock' },
        { id: 'e3', source: 'root-task', target: 'child-course' },
        { id: 'e4', source: 'child-course', target: 'child-task' },
        { id: 'e5', source: 'child-task', target: 'child-lock' },
      ],
    },
  };

  const rootLocks = model.collectLearnerCardLocks(learningMap, 'course-root');
  assert.equal(rootLocks.length, 1);
  assert.equal(rootLocks[0].id, 'root-lock');
  assert.equal(rootLocks[0].title, 'Продолжение закрыто');
  assert.equal(rootLocks[0].requirement, 'Решите «Ввод», чтобы открыть следующее задание.');
  assert.deepEqual(rootLocks[0].sourceEntityIds, ['task-open']);
  assert.equal('entityId' in rootLocks[0], false);
  assert.equal(JSON.stringify(rootLocks).includes('task-secret'), false);

  const childLocks = model.collectLearnerCardLocks(learningMap, 'course-child');
  assert.equal(childLocks.length, 1);
  assert.equal(childLocks[0].id, 'child-lock');
  assert.deepEqual(childLocks[0].sourceEntityIds, ['task-child-open']);
});

test('locked card item is non-content metadata and keeps only the safe lock message', () => {
  const entry = model.makeLockedContentItem({
    id: 'opaque-lock',
    title: 'Продолжение закрыто',
    requirement: 'Решите предыдущее задание, чтобы открыть продолжение.',
    sourceEntityIds: ['task-open'],
  }, 3.5);

  assert.equal(entry.kind, 'locked');
  assert.equal(entry.sort, 3.5);
  assert.equal(entry.title, 'Продолжение закрыто');
  assert.equal(entry.description, 'Решите предыдущее задание, чтобы открыть продолжение.');
  assert.equal(entry.assignment, undefined);
  assert.equal(entry.course, undefined);
});

test('identical course progress snapshots reuse state instead of forcing another render', () => {
  const previous = {
    'course-a': { total: 3, solved: 2, percent: 67, isComplete: false, loading: false },
    'course-b': { total: 1, solved: 1, percent: 100, isComplete: true, loading: false },
  };
  const identical = {
    'course-a': { total: 3, solved: 2, percent: 67, isComplete: false, loading: false },
    'course-b': { total: 1, solved: 1, percent: 100, isComplete: true, loading: false },
  };
  const reused = model.reuseProgressMapIfEqual(previous, identical);
  assert.equal(reused, previous);

  const changed = model.reuseProgressMapIfEqual(previous, {
    ...identical,
    'course-a': { ...identical['course-a'], solved: 3, percent: 100, isComplete: true },
  });
  assert.notEqual(changed, previous);
  assert.equal(changed['course-a'].solved, 3);
});
