import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/devtools/frontendDiagnosticsModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('frontend log limit is bounded and defaults safely', () => {
  assert.equal(model.clampFrontendLogLimitMb(undefined), 100);
  assert.equal(model.clampFrontendLogLimitMb(1), 25);
  assert.equal(model.clampFrontendLogLimitMb(500), 500);
  assert.equal(model.clampFrontendLogLimitMb(9999), 500);
});

test('diagnostic sanitizer redacts credentials and source/answers', () => {
  const value = model.sanitizeDiagnosticValue({
    authorization: 'Bearer abc',
    password: 'p',
    accessToken: 't',
    email: 'a@example.com',
    sourceCode: 'int main(){}',
    answer: '42',
    safe: 'hello',
    nested: { status: 500 },
  });
  assert.equal(value.authorization, '[redacted]');
  assert.equal(value.password, '[redacted]');
  assert.equal(value.accessToken, '[redacted]');
  assert.equal(value.email, '[redacted]');
  assert.equal(value.sourceCode, '[redacted]');
  assert.equal(value.answer, '[redacted]');
  assert.equal(value.safe, 'hello');
  assert.equal(value.nested.status, 500);
  const stringValue = model.sanitizeDiagnosticValue('Authorization: Bearer secret-token user=a@example.com');
  assert.doesNotMatch(stringValue, /secret-token|a@example\.com/);
  assert.match(stringValue, /\[redacted/);
});

test('diagnostic URL keeps routing context but redacts secret query values', () => {
  const result = model.sanitizeDiagnosticUrl('/assignment/123?tab=tests&token=abc&email=a%40b.test#secret');
  assert.match(result, /^\/assignment\/123\?/);
  assert.match(result, /tab=tests/);
  assert.match(result, /token=%5Bredacted%5D/);
  assert.match(result, /email=%5Bredacted%5D/);
  assert.match(result, /#…$/);
  assert.doesNotMatch(result, /abc|a%40b/);
});

test('prefetch cache is garbage-collected even when it never had a subscriber', async () => {
  let source = await fs.readFile(new URL('../../src/data/queryClient.js', import.meta.url), 'utf8');
  source = source.replace("import { logFrontendEvent } from '../devtools/frontendDiagnostics';", "const logFrontendEvent = () => {};");
  const query = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
  const client = new query.QueryClient({ defaultGcTime: 15 });
  await client.prefetchQuery({ queryKey: ['course-bundle', 'child'], queryFn: async () => ({ heavy: true }), staleTime: 1000 });
  assert.deepEqual(client.getQueryData(['course-bundle', 'child']), { heavy: true });
  await new Promise((resolve) => setTimeout(resolve, 35));
  assert.equal(client.getQueryData(['course-bundle', 'child']), undefined);
});
