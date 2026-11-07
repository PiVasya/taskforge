import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const moduleUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
const readSource = (path) => readFileSync(new URL(`../../src/features/course-assignments/images/${path}`, import.meta.url), 'utf8');
const graphUrl = moduleUrl(readSource('courseImageGraph.js'));
const zipUrl = moduleUrl(readSource('zipStore.js'));
const graph = await import(graphUrl);
const zip = await import(zipUrl);

const filesMock = moduleUrl(`export const uploadImage = (...args) => globalThis.__courseImageUpload(...args);
export const listCourseImages = (...args) => globalThis.__courseImageList(...args);`);
const transferSource = readSource('courseImageTransfer.js')
  .replaceAll("from '../../../api/files'", `from '${filesMock}'`)
  .replaceAll("from './courseImageGraph'", `from '${graphUrl}'`)
  .replaceAll("from './zipStore'", `from '${zipUrl}'`);
const transfer = await import(moduleUrl(transferSource));
const course = '11111111-1111-4111-8111-111111111111';
const localSrc = '/api/files/editor-images%2Fabc.png';
const img = (src, attrs = {}) => ({ type: 'image', attrs: { src, alt: 'Геометрия', width: 320, height: 180, monochrome: true, sourceBackground: '#000000', sourceDark: true, ...attrs } });
const tip = (nodes) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: nodes }] });
const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
function sample() {
  return { schemaVersion: 5, format: 'taskforge-task-graph', scopes: ['ids', 'content', 'checks'],
    tasks: [{ key: 'quiz', type: 'test', title: 'Геометрия', description: tip([img(localSrc)]),
      testSettings: { passPercent: 70 }, questions: [{
        id: '22222222-2222-4222-8222-222222222222', prompt: 'Какой ответ?', promptEditorMode: 'rich',
        promptContentJson: tip([img(localSrc), { type: 'mathInline', attrs: { kind: 'sqrt', expression: '7' } }]),
        correctOptionKeys: ['b'], optionsLayout: 'inline',
        options: [{ key: 'a', text: '1', contentJson: tip([{ type: 'text', text: '1' }]) },
          { key: 'b', text: '7', contentJson: tip([img(localSrc)]) }],
      }] }], courses: [], connections: [] };
}

test('all Tiptap image sources are discovered in descriptions, quiz prompts, and answer options', () => {
  const input = sample();
  const sources = graph.referencedImageSources(input);
  assert.equal(sources.get(localSrc), 3);
  assert.equal(input.tasks[0].questions[0].correctOptionKeys[0], 'b');
  const replaced = graph.mapGraphImageSources(input, () => 'asset://assets/00001.png').graph;
  assert.equal(graph.referencedImageSources(replaced).get('asset://assets/00001.png'), 3);
  assert.equal(replaced.tasks[0].questions[0].options[1].key, 'b');
  assert.equal(JSON.parse(replaced.tasks[0].questions[0].options[1].contentJson).content[0].content[0].attrs.sourceDark, true);
  assert.equal(JSON.parse(input.tasks[0].description).content[0].content[0].attrs.src, localSrc);
});

test('only same-origin public Files API image URLs can be packaged', () => {
  const origin = 'https://taskforge.test';
  assert.equal(graph.imageSourceKey(localSrc, origin), 'editor-images/abc.png');
  assert.equal(graph.imageSourceKey('https://evil.test/api/files/editor-images%2Fx.png', origin), null);
  assert.equal(graph.imageSourceKey('/api/private-files/image-tests/reference/x', origin), null);
  assert.equal(graph.imageSourceKey('/api/files/editor-images%2F..%2Fx.png', origin), null);
  assert.equal(graph.archiveAssetName('asset://assets/00001.png'), 'assets/00001.png');
  assert.equal(graph.archiveAssetName('asset://assets/../token'), null);
});

test('ZIP store reader verifies checksums, rejects duplicate names and traversal, retains exact file bytes', async () => {
  const blob = await zip.writeImageZip([{ path: 'graph.json', bytes: new TextEncoder().encode('{"a":1}') }, { path: 'assets/image.png', bytes: png }]);
  const entries = await zip.readImageZip(blob);
  assert.equal(new TextDecoder().decode(entries.get('graph.json')), '{"a":1}');
  assert.deepEqual(entries.get('assets/image.png'), png);
  await assert.rejects(zip.writeImageZip([{ path: '../secret', bytes: png }]), /имя/);
  await assert.rejects(zip.writeImageZip([{ path: 'graph.json', bytes: png }, { path: 'graph.json', bytes: png }]), /имя/);
  const mutated = new Uint8Array(await blob.arrayBuffer());
  mutated[44] ^= 15;
  await assert.rejects(zip.readImageZip(new Blob([mutated])), /контрольная сумма/);
});

test('large graph JSON is compressed inside ZIP when browser compression is supported', async () => {
  const original = new TextEncoder().encode(JSON.stringify({ format: 'taskforge-task-graph', tasks: Array(1000).fill({ title: 'Повтор текста JSON', description: 'Повтор текста JSON' }) }));
  const blob = await zip.writeImageZip([{ path: 'graph.json', bytes: original }]);
  assert.ok(blob.size < original.length / 5);
  const restored = await zip.readImageZip(blob);
  assert.deepEqual(restored.get('graph.json'), original);
});

test('ZIP reader accepts conventional DEFLATE archives, not just TaskForge STORE packages', async () => {
  const fixture = 'UEsDBBQAAAAIACOYZ12g5aLbJwAAACwAAAAKAAAAZ3JhcGguanNvbqtWSssvyk0sUbJSKkkszgZy0lN1QSzd9KLEggwlHbBwsZJVdGwtAFBLAwQUAAAACAAjmGddpAkHegoAAAAIAAAADAAAAGFzc2V0cy9wLnBuZ+sM8HPn5ZLiAgBQSwECFAMUAAAACAAjmGddoOWi2ycAAAAsAAAACgAAAAAAAAAAAAAAgAEAAAAAZ3JhcGguanNvblBLAQIUAxQAAAAIACOYZ12kCQd6CgAAAAgAAAAMAAAAAAAAAAAAAACAAU8AAABhc3NldHMvcC5wbmdQSwUGAAAAAAIAAgByAAAAgwAAAAAA';
  const entries = await zip.readImageZip(new Blob([Buffer.from(fixture, 'base64')]));
  assert.equal(JSON.parse(new TextDecoder().decode(entries.get('graph.json'))).format, 'taskforge-task-graph');
  assert.deepEqual(entries.get('assets/p.png'), png.subarray(0, 8));
});

test('course ZIP exports real image bytes and re-imports Tiptap metadata without changing answer keys', async (t) => {
  const old = { window: globalThis.window, fetch: globalThis.fetch,
    upload: globalThis.__courseImageUpload, list: globalThis.__courseImageList };
  t.after(() => { globalThis.window = old.window; globalThis.fetch = old.fetch;
    globalThis.__courseImageUpload = old.upload; globalThis.__courseImageList = old.list; });
  globalThis.window = { location: { origin: 'https://taskforge.test' } };
  const uploads = [];
  globalThis.__courseImageUpload = async (file, folder) => {
    uploads.push({ file, folder });
    return { url: `/api/files/${encodeURIComponent(folder + '/' + uploads.length + '.png')}` };
  };
  globalThis.__courseImageList = async () => [];
  let fetchCount = 0;
  globalThis.fetch = async (url) => { assert.equal(new URL(url).origin, 'https://taskforge.test'); fetchCount++;
    return { ok: true, headers: { get: () => 'image/png' }, arrayBuffer: async () => png.buffer.slice(0) }; };
  const blob = new Blob([JSON.stringify(sample())], { type: 'application/json' });
  const exported = await transfer.exportGraphImagePackage(blob, course);
  assert.equal(exported.imageCount, 1);
  assert.equal(fetchCount, 1);
  const entries = await zip.readImageZip(exported.blob);
  const packagedGraph = JSON.parse(new TextDecoder().decode(entries.get('graph.json')));
  assert.equal(graph.referencedImageSources(packagedGraph).get('asset://assets/00001.png'), 3);
  assert.equal(packagedGraph.tasks[0].questions[0].correctOptionKeys[0], 'b');
  assert.deepEqual(entries.get('assets/00001.png'), png);
  const imported = await transfer.importGraphImagePackage(exported.blob, course);
  assert.equal(imported.images, 1);
  assert.equal(uploads[0].folder, `editor-images/courses/${course}`);
  const restored = JSON.parse(imported.text);
  const answer = JSON.parse(restored.tasks[0].questions[0].options[1].contentJson).content[0].content[0];
  assert.ok(answer.attrs.src.startsWith('/api/files/editor-images%2Fcourses%2F'));
  assert.equal(answer.attrs.monochrome, true);
  assert.equal(answer.attrs.width, 320);
  assert.equal(answer.attrs.height, 180);
  assert.equal(answer.attrs.sourceBackground, '#000000');
  assert.equal(restored.tasks[0].questions[0].optionsLayout, 'inline');
  assert.deepEqual(restored.tasks[0].questions[0].correctOptionKeys, ['b']);
  assert.equal(JSON.parse(restored.tasks[0].questions[0].promptContentJson).content[0].content[1].type, 'mathInline');
});

test('broken image placeholders fail before any upload, and unreferenced archive assets remain gallery imports', async (t) => {
  const old = { upload: globalThis.__courseImageUpload, list: globalThis.__courseImageList };
  t.after(() => { globalThis.__courseImageUpload = old.upload; globalThis.__courseImageList = old.list; });
  let uploadCalls = 0;
  globalThis.__courseImageUpload = async () => { uploadCalls++; return { url: '/api/files/editor-images%2Fok.png' }; };
  const invalid = sample();
  invalid.tasks[0].description = tip([img('asset://assets/missing.png')]);
  const broken = await zip.writeImageZip([{ path: 'graph.json', bytes: new TextEncoder().encode(JSON.stringify(invalid)) }]);
  await assert.rejects(transfer.importGraphImagePackage(broken, course), /отсутствует/);
  assert.equal(uploadCalls, 0);
  const valid = sample();
  valid.tasks[0].description = tip([]);
  valid.tasks[0].questions[0].promptContentJson = tip([]);
  valid.tasks[0].questions[0].options[1].contentJson = tip([]);
  const noRefs = await zip.writeImageZip([
    { path: 'graph.json', bytes: new TextEncoder().encode(JSON.stringify(valid)) },
    { path: 'assets/unreferenced.png', bytes: png },
  ]);
  const imported = await transfer.importGraphImagePackage(noRefs, course);
  assert.equal(imported.images, 1);
  assert.equal(uploadCalls, 1);
});
