import test from 'node:test';
import assert from 'node:assert/strict';
import { chartColors, resultFill } from './assignmentChartPalette.mjs';

test('every analytics series is bound to live TaskForge tokens rather than fixed red/green', () => {
  for (const [type, color] of Object.entries(chartColors)) {
    assert.match(color, /var\(--(accent(?:-700|2|3)?|text-muted|border)\)/, type);
    assert.doesNotMatch(color, /#[a-f\d]{3,8}\b|\b(?:34d399|fb7185)\b/i, type);
  }
  assert.notEqual(chartColors.passed, chartColors.failed);
  assert.notEqual(chartColors.pending, chartColors.passed);
});

test('donut statuses share the same palette as bars, progress and legend', () => {
  assert.equal(resultFill('Успешные'), chartColors.passed);
  assert.equal(resultFill('Неуспешные'), chartColors.failed);
  assert.equal(resultFill('Проверяются'), chartColors.pending);
});
