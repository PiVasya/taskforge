import { applyServerGraphBindings } from './taskGraphPreviewSnapshot';

test('uses server stable identities without editing the original JSON or explicit UUIDs', () => {
  const raw = { schemaVersion: 5, format: 'taskforge-task-graph', scopes: ['content'],
    courses: [{ key: 'a' }], tasks: [{ key: 'task' }, { key: 'user-id', id: '77777777-7777-4777-8777-777777777777' }] };
  const a = '11111111-1111-4111-8111-111111111111';
  const b = '22222222-2222-4222-8222-222222222222';
  const preflight = { bindings: { courses: [{ key: 'a', id: a }], tasks: [
    { key: 'task', id: b }, { key: 'user-id', id: '33333333-3333-4333-8333-333333333333' }] } };
  const materialized = applyServerGraphBindings(raw, preflight);
  expect(materialized.courses[0].id).toBe(a);
  expect(materialized.tasks[0].id).toBe(b);
  expect(materialized.tasks[1].id).toBe(raw.tasks[1].id);
  expect(raw.tasks[0].id).toBeUndefined();
  expect(materialized.scopes).toContain('ids');
  expect(applyServerGraphBindings(materialized, preflight)).toEqual(materialized);
});
