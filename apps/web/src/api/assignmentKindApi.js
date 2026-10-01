import * as code from './codeAssignments';
import * as image from './imageAssignments';
import * as test from './testAssignments';
import * as math from './mathAssignments';
import * as sql from './sqlAssignments';

export const assignmentKindApi = {
  'code-test': {
    get: code.getCodeAssignment, shell: code.getCodeSolveShell, statement: code.getCodeStatement, tests: code.getCodeTests,
    edit: code.getCodeAssignmentForEdit, create: code.createCodeAssignment, update: code.updateCodeAssignment,
  },
  'image-test': {
    get: image.getImageAssignment, shell: image.getImageSolveShell, statement: image.getImageStatement, tests: image.getImageTests,
    edit: image.getImageAssignmentForEdit, create: image.createImageAssignment, update: image.updateImageAssignment,
  },
  test: {
    get: test.getTestAssignment, shell: test.getTestSolveShell, statement: test.getTestStatement, tests: test.getTestTests,
    edit: test.getTestAssignmentForEdit, create: test.createTestAssignment, update: test.updateTestAssignment,
  },
  math: {
    get: math.getMathAssignment, shell: math.getMathSolveShell, statement: math.getMathStatement, tests: math.getMathTests,
    edit: math.getMathAssignmentForEdit, create: math.createMathAssignment, update: math.updateMathAssignment,
  },
  'sql-test': {
    get: sql.getSqlAssignment, shell: sql.getSqlSolveShell, statement: sql.getSqlStatement, tests: sql.getSqlTests,
    edit: sql.getSqlAssignmentForEdit, create: sql.createSqlAssignment, update: sql.updateSqlAssignment,
  },
};

export function apiForAssignmentType(type) {
  const api = assignmentKindApi[String(type || '').trim()];
  if (!api) throw new Error(`Unsupported assignment type: ${type || ''}`);
  return api;
}
