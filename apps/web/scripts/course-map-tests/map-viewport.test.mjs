import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/course-assignments/courseMapViewport.js', import.meta.url), 'utf8');
const { measureCourseMapViewportHeight } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const atTop = (top) => ({ getBoundingClientRect: () => ({ top }) });

test('map loading and ready views occupy the same remaining viewport height', () => {
  const loadingFrame = atTop(103.5);
  const readyFrame = atTop(103.5);
  const expected = 746;
  assert.equal(measureCourseMapViewportHeight(loadingFrame, 864), expected);
  assert.equal(measureCourseMapViewportHeight(readyFrame, 864), expected);
  assert.equal(measureCourseMapViewportHeight(readyFrame, 864), measureCourseMapViewportHeight(loadingFrame, 864));
});

test('map height responds to viewport changes without changing the component geometry rules', () => {
  const frame = atTop(108);
  assert.equal(measureCourseMapViewportHeight(frame, 720), 598);
  assert.equal(measureCourseMapViewportHeight(frame, 950), 828);
  assert.equal(measureCourseMapViewportHeight(frame, 1500), 1000);
  assert.equal(measureCourseMapViewportHeight(frame, 400), 360);
});

test('map height reads the real element position, not a fixed guessed header size', () => {
  assert.equal(measureCourseMapViewportHeight(atTop(108), 900), 778);
  assert.equal(measureCourseMapViewportHeight(atTop(225), 900), 661);
  assert.equal(measureCourseMapViewportHeight(null, 900), null);
  assert.equal(measureCourseMapViewportHeight(atTop(Number.NaN), 900), null);
  assert.equal(measureCourseMapViewportHeight(atTop(108), Number.NaN), null);
});
