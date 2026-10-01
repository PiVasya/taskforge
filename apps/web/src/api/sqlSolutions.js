import api from './http';

const body = (promise) => promise.then((response) => response.data);
function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export const runSqlSolution = (assignmentId, input) =>
  body(api.post('/api/sql-solutions/run', { assignmentId, ...input }));

export const getSqlPreview = (_assignmentId, jobId) =>
  body(api.get(`/api/sql-solutions/previews/${jobId}`));

export const getSqlSubmission = async (_assignmentId, submissionId) =>
  body(api.get(`/api/sql-solutions/${submissionId}`));

export async function submitSqlSolution(assignmentId, input) {
  try { return await body(api.post('/api/sql-solutions/check', { assignmentId, ...input })); }
  finally { emitQuotaChanged(); }
}
