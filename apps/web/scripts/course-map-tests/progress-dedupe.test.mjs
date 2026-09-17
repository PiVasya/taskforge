import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

let source = await fs.readFile(new URL('../../src/api/assignments.js', import.meta.url), 'utf8');
source = source.replace(
  "import api from './http';",
  "const api = { post: (...args) => { globalThis.__tfProgressPostCalls += 1; return globalThis.__tfProgressPost(...args); } };",
);
const assignments = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('course progress requests with the same ids share one in-flight HTTP request', async () => {
  globalThis.__tfProgressPostCalls = 0;
  let resolvePost;
  globalThis.__tfProgressPost = async () => new Promise((resolve) => { resolvePost = resolve; });

  const first = assignments.getCourseProgressByCourses(['b', 'a', 'a']);
  const second = assignments.getCourseProgressByCourses(['a', 'b']);
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(globalThis.__tfProgressPostCalls, 1);

  resolvePost({ data: [{ courseId: 'a', total: 1, solved: 1 }] });
  assert.deepEqual(await first, [{ courseId: 'a', total: 1, solved: 1 }]);
  assert.deepEqual(await second, [{ courseId: 'a', total: 1, solved: 1 }]);

  globalThis.__tfProgressPost = async () => ({ data: [] });
  await assignments.getCourseProgressByCourses(['a', 'b']);
  assert.equal(globalThis.__tfProgressPostCalls, 2);
});
