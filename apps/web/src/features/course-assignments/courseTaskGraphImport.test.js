import { applyTaskGraphImport } from './courseTaskGraphImport';

const deletedId = '11111111-1111-4111-8111-111111111111';
const keptId = '22222222-2222-4222-8222-222222222222';
const courseId = '33333333-3333-4333-8333-333333333333';

test('task graph import removes explicitly deleted assignment node and its edges even when topology import is disabled', () => {
  const nodes = [
    { id: `course:${courseId}`, type: 'course', entityId: courseId, position: { x: 0, y: 0 } },
    { id: `assignment:${deletedId}`, type: 'assignment', entityId: deletedId, position: { x: 300, y: 0 } },
    { id: `assignment:${keptId}`, type: 'assignment', entityId: keptId, position: { x: 600, y: 0 } },
  ];
  const edges = [
    { id: 'e1', source: `course:${courseId}`, target: `assignment:${deletedId}` },
    { id: 'e2', source: `assignment:${deletedId}`, target: `assignment:${keptId}` },
  ];
  const taskGraph = {
    tasks: [],
    courses: [],
    deleteTasks: [deletedId],
    connections: [],
    apply: { connections: false, connectionAccess: false, layout: false },
  };

  const result = applyTaskGraphImport({
    nodes,
    edges,
    taskGraph,
    taskMappings: [],
    assignments: [{ id: keptId, type: 'test' }],
    courseId,
  });

  expect(result.deletedNodeCount).toBe(1);
  expect(result.nodes.some((node) => node.entityId === deletedId)).toBe(false);
  expect(result.edges).toHaveLength(0);
});
