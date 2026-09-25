import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const neoCss = await fs.readFile(new URL('../../src/styles/neobrutal.css', import.meta.url), 'utf8');
const mapCss = await fs.readFile(new URL('../../src/features/course-assignments/course-map.css', import.meta.url), 'utf8');
const dbCss = await fs.readFile(new URL('../../src/features/sql-task/sql-database.css', import.meta.url), 'utf8');
const solveCss = await fs.readFile(new URL('../../src/features/assignment-solve/assignment-solve.css', import.meta.url), 'utf8');
const solveSource = await fs.readFile(new URL('../../src/features/assignment-solve/AssignmentSolveFeature.jsx', import.meta.url), 'utf8');
const constraintsSource = await fs.readFile(new URL('../../src/features/assignment-solve/components/AssignmentTaskConstraints.jsx', import.meta.url), 'utf8');

test('neo-brutal table headings use current theme surfaces instead of light brand swatches', () => {
  const start = neoCss.indexOf('html.neo-brutal th {');
  const end = neoCss.indexOf('}', start) + 1;
  const headingRule = neoCss.slice(start, end);
  assert.match(headingRule, /background:\s*rgb\(var\(--muted\)\) !important;/);
  assert.match(headingRule, /color:\s*rgb\(var\(--text\)\) !important;/);
  assert.doesNotMatch(headingRule, /brand-100/);
  assert.match(dbCss, /html\.neo-brutal \.sql-db-grid th[\s\S]*?background:\s*rgb\(var\(--muted\)\) !important;/);
});

test('learner connection handles stay fully opaque', () => {
  assert.match(mapCss, /\.course-map-shell\.is-viewer \.course-map-handle\.react-flow__handle \{[\s\S]*?opacity:\s*1;/);
  assert.match(mapCss, /\.course-map-node--code\.is-solved \.course-map-handle\.react-flow__handle \{[\s\S]*?opacity:\s*1 !important;/);
});

test('assignment solve semantic UI uses TaskForge theme tokens instead of independent traffic-light colors', () => {
  const forbiddenPalette = /(amber|yellow|emerald|green|rose|red|sky|blue|cyan|teal|lime|orange)-(?:50|100|200|300|400|500|600|700|800|900|950)/;
  assert.doesNotMatch(solveSource, forbiddenPalette);
  assert.doesNotMatch(constraintsSource, forbiddenPalette);
  assert.match(solveCss, /\.solve-tone-panel/);
  assert.match(solveCss, /rgb\(var\(--accent\)\)/);
  assert.match(solveCss, /rgb\(var\(--text\)\)/);
  assert.match(solveCss, /rgb\(var\(--muted\)\)/);
});
