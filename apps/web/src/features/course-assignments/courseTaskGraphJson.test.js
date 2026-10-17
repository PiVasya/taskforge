import { buildTaskGraphImportDiff } from './courseTaskGraphJson';

const id = '11111111-1111-4111-8111-111111111111';

function graph(type) {
  return {
    schemaVersion: 5,
    format: 'taskforge-task-graph',
    scopes: ['ids', 'content', 'checks', 'visibility', 'connections', 'connectionAccess'],
    courses: [],
    tasks: [{
      key: 'task-1',
      course: '$course',
      id,
      type,
      title: 'Задание',
      isVisible: true,
    }],
    datasets: [],
    connections: [{ from: '$course', to: 'task-1', access: { hidden: 'inherit', sequential: 'inherit' } }],
  };
}

test('unchanged graph has no import work even if tasks are present', () => {
  const current = graph('test');
  const diff = buildTaskGraphImportDiff(current, current);

  expect(diff.total).toBe(0);
  expect(diff.unchangedCount).toBe(1);
  expect(diff.hasImportWork).toBe(false);
});

test('identical titles inside the incoming batch do not silently merge distinct task keys', () => {
  const incoming = graph('test');
  incoming.tasks = [
    { ...incoming.tasks[0], id: '33333333-3333-4333-8333-333333333333', key: 'ceoi-match', title: 'ENGLISH' },
    { ...incoming.tasks[0], id: '44444444-4444-4444-8444-444444444444', key: 'ceoi-router', title: 'ENGLISH' },
  ];
  incoming.connections = [
    { from: '$course', to: 'ceoi-match' },
    { from: '$course', to: 'ceoi-router' },
  ];
  const current = graph('test');
  current.tasks = [];
  current.connections = [];

  const diff = buildTaskGraphImportDiff(incoming, current);

  expect(diff.createCount).toBe(2);
  expect(diff.rows.map((row) => row.key)).toEqual(['ceoi-match', 'ceoi-router']);
  expect(diff.duplicateTitleCount).toBe(1);
  expect(diff.repeatedTitleInBatchCount).toBe(1);
  expect(diff.requiresDuplicateConfirmation).toBe(true);
  expect(diff.previewMap.nodes.filter((node) => node.type === 'test')).toHaveLength(2);
});

test('course preview reports unreachable tasks by stable key and follows task chains', () => {
  const incoming = graph('test');
  incoming.courses = [{ key: 'coci-2006', id: '22222222-2222-4222-8222-222222222222', title: 'COCI Online round 1' }];
  incoming.tasks = [
    { ...incoming.tasks[0], id: '33333333-3333-4333-8333-333333333333', key: 'modulo', title: 'MODULO', course: 'coci-2006' },
    { ...incoming.tasks[0], id: '44444444-4444-4444-8444-444444444444', key: 'herman', title: 'HERMAN', course: 'coci-2006' },
    { ...incoming.tasks[0], id: '55555555-5555-4555-8555-555555555555', key: 'okviri', title: 'OKVIRI', course: 'coci-2006' },
  ];
  incoming.connections = [
    { from: '$course', to: 'coci-2006' },
    { from: 'coci-2006', to: 'modulo' },
    { from: 'modulo', to: 'herman' },
  ];
  const current = graph('test');
  current.tasks = [];
  current.connections = [];

  const diff = buildTaskGraphImportDiff(incoming, current);

  expect(diff.courseRows).toMatchObject([{
    key: 'coci-2006', taskCount: 3, createCount: 3,
    directLinkCount: 1, reachableCount: 2, unreachableKeys: ['okviri'],
  }]);
  expect(diff.warnings.some((warning) => warning.includes('COCI Online round 1'))).toBe(true);
});

test('nested course with task links but without a root link is called out in preview', () => {
  const incoming = graph('test');
  incoming.tasks[0].course = 'sub';
  incoming.courses = [{ key: 'sub', id: '22222222-2222-4222-8222-222222222222', title: 'Nested' }];
  incoming.connections = [{ from: 'sub', to: 'task-1' }];
  const current = graph('test');
  current.tasks = [];
  current.connections = [];
  const diff = buildTaskGraphImportDiff(incoming, current);
  expect(diff.courseRows[0].reachableFromRoot).toBe(false);
  expect(diff.warnings.join(' ')).toMatch(/не достижим от \$course/);
});

test('a course with matching title but a different UUID requires confirmation', () => {
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];
  incoming.courses = [{ key: 'contest-new', id: '22222222-2222-4222-8222-222222222222', title: 'Contest' }];
  const current = graph('test');
  current.tasks = [];
  current.connections = [];
  current.courses = [{ key: 'contest-existing', id: '55555555-5555-4555-8555-555555555555', title: 'Contest' }];
  const diff = buildTaskGraphImportDiff(incoming, current);
  expect(diff.duplicateCourseTitleCount).toBe(1);
  expect(diff.requiresDuplicateConfirmation).toBe(true);
  expect(buildTaskGraphImportDiff(incoming, current, { allowDuplicates: true }).requiresDuplicateConfirmation).toBe(false);
});

test('import diff blocks an in-place SQL assignment type change', () => {
  const diff = buildTaskGraphImportDiff(graph('test'), graph('sql-test'));

  expect(diff.updateCount).toBe(1);
  expect(diff.validationErrorCount).toBe(1);
  expect(diff.graphIssues[0].path).toBe('$.tasks[0].type');
  expect(diff.graphIssues[0].message).toMatch(/SQL-задания нельзя менять/);
  expect(diff.rows[0].issues).toHaveLength(1);
});

test('import diff allows ordinary non-SQL type changes', () => {
  const diff = buildTaskGraphImportDiff(graph('math'), graph('test'));

  expect(diff.validationErrorCount).toBe(0);
  expect(diff.rows[0].issues).toHaveLength(0);
});

test('explicit deleteTasks requires a separate confirmation and counts deletion', () => {
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];
  incoming.deleteTasks = [id];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'));

  expect(diff.validationErrorCount).toBe(0);
  expect(diff.deleteRequestCount).toBe(1);
  expect(diff.deleteCount).toBe(1);
  expect(diff.requiresDeleteConfirmation).toBe(true);
  expect(diff.deleteRows[0].action).toBe('delete');

  const confirmed = buildTaskGraphImportDiff(incoming, graph('test'), { allowDeletes: true });
  expect(confirmed.requiresDeleteConfirmation).toBe(false);
});

test('deleteTasks does not infer deletion from tasks omitted from the JSON', () => {
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'));

  expect(diff.deleteRequestCount).toBe(0);
  expect(diff.deleteCount).toBe(0);
  expect(diff.requiresDeleteConfirmation).toBe(false);
});

test('deleteTasks cannot contain the same id as tasks', () => {
  const incoming = graph('test');
  incoming.deleteTasks = [id];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'), { allowDeletes: true });

  expect(diff.validationErrorCount).toBeGreaterThan(0);
  expect(diff.graphIssues.some((issue) => issue.path === '$.deleteTasks[0]')).toBe(true);
});

test('deleting an SQL assignment with retained SQL spec is blocked in preview', () => {
  const current = graph('sql-test');
  current.tasks[0].sql = { dataset: 'dataset-1', mode: 'result', targets: [] };
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];
  incoming.deleteTasks = [id];

  const diff = buildTaskGraphImportDiff(incoming, current, { allowDeletes: true });

  expect(diff.validationErrorCount).toBe(1);
  expect(diff.graphIssues[0].path).toBe('$.deleteTasks[0]');
  expect(diff.graphIssues[0].message).toMatch(/историей/);
});


test('deleteTasks is rejected on older task-graph schemas', () => {
  const incoming = graph('test');
  incoming.schemaVersion = 4;
  incoming.tasks = [];
  incoming.connections = [];
  incoming.deleteTasks = [id];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'), { allowDeletes: true });

  expect(diff.validationErrorCount).toBeGreaterThan(0);
  expect(diff.graphIssues.some((issue) => issue.path === '$.schemaVersion' && /deleteTasks/.test(issue.message))).toBe(true);
});


test('deleting a task previews mandatory incident-edge cleanup even when connection import is disabled', () => {
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];
  incoming.deleteTasks = [id];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'), {
    allowDeletes: true,
    updateConnections: false,
  });

  expect(diff.connectionRemovedCount).toBe(1);
  expect(diff.removedConnectionRows).toHaveLength(1);
  expect(diff.connectionUnchangedCount).toBe(0);
});


test('course-only canonical import is considered applicable work', () => {
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];
  incoming.courses = [{
    key: 'nested-course',
    id: '22222222-2222-4222-8222-222222222222',
    title: 'Вложенный курс',
    isPublic: true,
    isHiddenFromStudents: false,
    visibleGroupIds: [],
  }];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'));

  expect(diff.total).toBe(1);
  expect(diff.courseCount).toBe(1);
  expect(diff.hasImportWork).toBe(true);
});


test('GUID matching is case-insensitive in deletion preview', () => {
  const incoming = graph('test');
  incoming.tasks = [];
  incoming.connections = [];
  incoming.deleteTasks = [id.toUpperCase()];

  const diff = buildTaskGraphImportDiff(incoming, graph('test'));

  expect(diff.deleteCount).toBe(1);
  expect(diff.deleteMissingCount).toBe(0);
  expect(diff.requiresDeleteConfirmation).toBe(true);
});
