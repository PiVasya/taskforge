import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/utils/solutionDto.js', import.meta.url), 'utf8');
const dto = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('assignment link normalizes assignment id from code/sql/image solution DTOs', () => {
  assert.equal(dto.getAssignmentId({ assignmentId: 'a-code' }), 'a-code');
  assert.equal(dto.getAssignmentId({ AssignmentId: 'a-image' }), 'a-image');
});

test('assignment link normalizes taskAssignmentId from test/math attempts', () => {
  assert.equal(dto.getAssignmentId({ taskAssignmentId: 'a-test' }), 'a-test');
  assert.equal(dto.getAssignmentId({ TaskAssignmentId: 'a-math' }), 'a-math');
});

test('assignment link supports nested assignment DTO and stays empty without an assignment', () => {
  assert.equal(dto.getAssignmentId({ assignment: { id: 'a-nested' } }), 'a-nested');
  assert.equal(dto.getAssignmentId({ id: 'solution-only' }), '');
});
