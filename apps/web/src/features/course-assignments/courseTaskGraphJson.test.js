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

  expect(diff.total).toBe(0);
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
