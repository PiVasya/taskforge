import test from 'node:test';
import assert from 'node:assert/strict';
import { loadSourceModule } from '../test-utils/load-source-module.mjs';

const { createGraphTransferRunner, transferFailure } = await loadSourceModule(new URL('../../src/api/graphTransferProtocol.js', import.meta.url));
const { TRANSFER_EXPORT_PRESETS, previewExportOptions, mergeTransferStatuses, transferImportPercent, withPreviewMetadata } =
  await loadSourceModule(new URL('../../src/features/course-assignments/graphTransferModel.js', import.meta.url));
const job = (state, fields = {}) => ({ transferId: 'job-1', courseId: 'course-1', userId: 'user-1', kind: 'export', state,
  percent: state === 'succeeded' ? 100 : 0, createdAt: '2026-10-26T12:00:00Z', updatedAt: '2026-10-26T12:01:00Z', ...fields });
const error = (status) => Object.assign(new Error('transport'), status ? { response: { status } } : {});
const runner = (api, options = {}) => createGraphTransferRunner({ api, idFactory: () => 'job-1', delay: async () => {}, ...options });

test('transfer: an accepted POST with a lost response is reconciled, not submitted twice', async () => {
  let starts = 0, ids = 0, results = 0;
  const seen = [];
  const api = { start: async (_, body) => { starts++; assert.equal(body.transferId, 'job-1'); throw error(504); },
    get: async (_, id) => { assert.equal(id, 'job-1'); return job('succeeded'); },
    result: async () => { results++; return { ok: true }; } };
  const result = await runner(api, { idFactory: () => { ids++; return 'job-1'; }, notify: (j) => seen.push(j) }).run('course-1', 'export', {});
  assert.deepEqual(result, { ok: true }); assert.equal(starts, 1); assert.equal(ids, 1); assert.equal(results, 1);
  assert.equal(seen.at(-1).state, 'succeeded');
});

test('transfer: failure before admission resends the identical job and import operation IDs', async () => {
  const sent = []; let lookup = 0;
  const api = { start: async (_, body) => { sent.push(structuredClone(body)); if (sent.length === 1) throw error(502); return job('queued'); },
    get: async () => { if (++lookup === 1) throw error(404); return job('succeeded'); }, result: async () => 123 };
  assert.equal(await runner(api).run('course-1', 'import', { graph: { tasks: [] } }, { operationId: 'op-1' }), 123);
  assert.equal(sent.length, 2); assert.deepEqual(sent[0], sent[1]); assert.equal(sent[0].operationId, 'op-1');
});

test('transfer: admission validation failures are not blindly retried', async () => {
  let starts = 0, reads = 0;
  await assert.rejects(runner({ start: async () => { starts++; throw error(400); }, get: async () => { reads++; } })
    .run('course-1', 'import', {}), (e) => e.response.status === 400 && e.transferId === 'job-1');
  assert.equal(starts, 1); assert.equal(reads, 0);
});

test('transfer: unavailable admission never generates another job ID', async () => {
  let ids = 0, starts = 0;
  await assert.rejects(runner({ start: async () => { starts++; throw error(503); }, get: async () => { throw error(404); } },
    { idFactory: () => { ids++; return 'job-1'; } }).run('course-1', 'import', {}),
  (e) => e.code === 'TRANSFER_STATUS_UNKNOWN' && e.transferId === 'job-1');
  assert.equal(ids, 1); assert.equal(starts, 4);
});

test('transfer: polling publishes real counters and reads the artifact only at success', async () => {
  const updates = [job('running', { percent: 24, completedItems: 100, totalItems: 511 }), job('running', { percent: 72, completedItems: 511, totalItems: 511 }), job('succeeded')];
  const seen = []; let artifacts = 0;
  await runner({ get: async () => updates.shift(), result: async () => { artifacts++; return {}; } }, { notify: j => seen.push(j) })
    .observe('course-1', job('queued'));
  assert.deepEqual(seen.map(j => j.percent), [0, 24, 72, 100]);
  assert.equal(seen[1].completedItems, 100); assert.equal(artifacts, 1);
});

test('transfer: download transport retries never repeat the mutation', async () => {
  let starts = 0, artifacts = 0;
  const content = new Blob(['{"ok":true}']);
  const result = await runner({ start: async () => { starts++; return job('succeeded'); }, result: async (_, __, type) => {
    assert.equal(type, 'blob'); if (++artifacts < 3) throw error(502); return content;
  } }).run('course-1', 'export', {}, { resultType: 'blob' });
  assert.equal(result, content); assert.equal(starts, 1); assert.equal(artifacts, 3);
});

for (const state of ['failed', 'cancelled', 'expired']) {
  test(`transfer: ${state} job does not read a success artifact`, async () => {
    let artifacts = 0;
    await assert.rejects(runner({ result: async () => { artifacts++; } }).observe('course-1', job(state, {
      resultStatus: 409, errorJson: JSON.stringify({ code: 'TEST_FAILURE', phase: 'assignments' }),
    })), (e) => e.code === 'TEST_FAILURE' && e.response.data.phase === 'assignments');
    assert.equal(artifacts, 0);
  });
}

test('transfer: interrupted status polling reports uncertainty and never cancels or restarts a job', async () => {
  let reads = 0;
  const api = { get: async () => { reads++; throw error(503); }, start: () => assert.fail('resubmission'), cancel: () => assert.fail('cancellation') };
  await assert.rejects(runner(api).observe('course-1', job('running')), e => e.code === 'TRANSFER_STATUS_UNKNOWN');
  assert.equal(reads, 8);
});

test('transfer: stopping observation is not cancellation of server work', async () => {
  const controller = new AbortController();
  const api = { get: () => assert.fail('read after abort'), cancel: () => assert.fail('server cancel') };
  await assert.rejects(runner(api, { delay: async () => controller.abort() }).observe('course-1', job('running'), { signal: controller.signal }),
    e => e.name === 'AbortError');
});

test('transfer: malformed error JSON produces a safe structured error', () => {
  const result = transferFailure(job('failed', { errorJson: '{', resultStatus: 500 }));
  assert.equal(result.code, 'TRANSFER_FAILED'); assert.equal(result.response.status, 500);
});

test('transfer UI: layout and structure presets never request conditions or tests', () => {
  for (const key of ['layout', 'structure']) {
    assert.equal(TRANSFER_EXPORT_PRESETS[key].includeContent, false);
    assert.equal(TRANSFER_EXPORT_PRESETS[key].includeChecks, false);
    assert.equal(TRANSFER_EXPORT_PRESETS[key].includeGuide, false);
    assert.equal(TRANSFER_EXPORT_PRESETS[key].includeIds, true);
  }
  assert.equal(TRANSFER_EXPORT_PRESETS.layout.includeConnections, false);
  assert.equal(TRANSFER_EXPORT_PRESETS.structure.includeConnections, true);
});

test('transfer UI: preview snapshots load only effective content/check scopes', () => {
  assert.equal(previewExportOptions({ scopes: ['ids', 'layout'] }).includeContent, false);
  assert.equal(previewExportOptions({ scopes: ['ids', 'layout'] }).includeChecks, false);
  assert.equal(previewExportOptions({ scopes: ['ids', 'content', 'checks'] }, { updateContent: false }).includeContent, false);
  assert.equal(previewExportOptions({ scopes: ['ids', 'content', 'checks'] }, { updateChecks: false }).includeChecks, false);
  assert.equal(previewExportOptions({ scopes: ['checks'] }).includeChecks, true);
  assert.equal(previewExportOptions([]).includeContent, true);
});

test('transfer UI: data success is not 100 percent before confirmed map receipt', () => {
  const imported = job('succeeded', { kind: 'import', operationId: 'op-1' });
  assert.equal(transferImportPercent(imported, { operationId: 'op-1', state: 'data-committed' }), 70);
  assert.equal(transferImportPercent(job('succeeded', { kind: 'map', operationId: 'op-1' }), null), 99);
  assert.equal(transferImportPercent(imported, { operationId: 'op-1', state: 'completed' }), 100);
  assert.equal(transferImportPercent(imported, { operationId: 'unrelated', state: 'completed' }), 70);
  assert.equal(transferImportPercent(job('succeeded')), 100);
});

test('transfer UI: status merging excludes another actor/course and ignores stale reads', () => {
  const completed = job('succeeded');
  const rows = mergeTransferStatuses([completed], [job('running', { updatedAt: '2026-10-26T12:00:30Z' }),
    job('running', { transferId: 'other-user', userId: 'user-2' }), job('running', { transferId: 'other-course', courseId: 'course-2' })], 'course-1', 'user-1');
  assert.deepEqual(rows, [completed]);
  assert.equal(mergeTransferStatuses(rows, [job('running')], 'course-1', 'user-1')[0].state, 'succeeded');
});

test('transfer UI: a real retry with a newer attempt is visible', () => {
  const retried = job('queued', { attemptCount: 2, updatedAt: '2026-10-26T12:02:00Z' });
  assert.equal(mergeTransferStatuses([job('failed', { attemptCount: 1 })], [retried], 'course-1', 'user-1')[0].state, 'queued');
});

test('transfer UI: current captions do not overwrite author content or import secret fields', () => {
  const graph = { tasks: [{ id: 'ABC', key: 'a' }, { id: 'DEF', title: 'Author title' }] };
  const result = withPreviewMetadata(graph, { metadata: [{ id: 'abc', title: 'Current', type: 'code-test', description: 'secret', testCases: ['secret'] },
    { id: 'def', title: 'Current other' }] });
  assert.equal(result.tasks[0].title, 'Current'); assert.equal(result.tasks[1].title, 'Author title');
  assert.equal(result.tasks[0].description, undefined); assert.equal(result.tasks[0].testCases, undefined);
  assert.equal(graph.tasks[0].title, undefined);
});

test('transfer UI: many recent small operations cannot hide an older active transfer', () => {
  const active = job('running', { transferId: 'still-running', createdAt: '2026-10-26T10:00:00Z' });
  const completed = Array.from({ length: 40 }, (_, n) => job('succeeded', { transferId: `completed-${n}` }));
  const rows = mergeTransferStatuses([active], completed, 'course-1', 'user-1');
  assert.equal(rows.length, 30); assert.equal(rows[0].transferId, 'still-running');
});
