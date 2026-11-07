import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/components/tiptap/imageSizing.js', import.meta.url), 'utf8');
const { normalizeImageDimension, imageSizingStyle, imageDimensionFromElement, isSafeLegacyRasterImage } =
  await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('editor and learner use the exact same bounded Tiptap image dimensions', () => {
  assert.deepEqual(imageSizingStyle({ width: 320, height: '180px' }), { width: '320px', height: '180px' });
  assert.deepEqual(imageSizingStyle({ width: '60%' }), { width: '60%' });
  assert.equal(imageSizingStyle({}), undefined);
  assert.equal(normalizeImageDimension(100, true), '100px');
  assert.equal(normalizeImageDimension('100%', true), '100%');
});

test('unsafe CSS and excessive values from imported image JSON are never rendered', () => {
  for (const input of ['calc(100vh)', 'expression(alert(1))', 'javascript:alert(1)', '-100', '9999999', '0', '1;display:none', '101%']) {
    assert.equal(normalizeImageDimension(input, true), null);
  }
  assert.deepEqual(imageSizingStyle({ width: 'calc(100vh)', height: 240 }), { height: '240px' });
  assert.equal(normalizeImageDimension('80%', false), null);
});

test('pasted HTML images keep their authored size, including a width attribute', () => {
  const element = { style: { width: '', height: '90px' }, getAttribute: (name) => name === 'width' ? '480' : null };
  assert.equal(imageDimensionFromElement(element, 'width', true), '480px');
  assert.equal(imageDimensionFromElement(element, 'height'), '90px');
});

test('legacy inline images are restricted to bounded raster data, never SVG or HTML', () => {
  assert.equal(isSafeLegacyRasterImage('data:image/png;base64,iVBORw0KGgo='), true);
  assert.equal(isSafeLegacyRasterImage('data:image/svg+xml;base64,PHN2Zy8+'), false);
  assert.equal(isSafeLegacyRasterImage('data:text/html;base64,PHNjcmlwdD4='), false);
  assert.equal(isSafeLegacyRasterImage('data:image/png;base64,' + 'A'.repeat(8 * 1024 * 1024)), false);
});
