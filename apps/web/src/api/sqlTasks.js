import api from './http';
const body = promise => promise.then(response => response.data);
export const sqlEngines = () => body(api.get('/api/sql/engines'));
export const sqlRuntime = () => body(api.get('/api/execution/sql-status'));
export const sqlDatasets = () => body(api.get('/api/sql/datasets'));
export const sqlDataset = id => body(api.get(`/api/sql/datasets/${id}`));
export const sqlDatasetVersion = (id, version) => body(api.get(`/api/sql/datasets/${id}/versions/${version}`));
export const createSqlDataset = input => body(api.post('/api/sql/datasets', input));
export const updateSqlDataset = (id, input) => body(api.put(`/api/sql/datasets/${id}`, input));
export const createSqlDatasetVersion = (id, input) => body(api.post(`/api/sql/datasets/${id}/versions`, input));
export const sqlEdit = id => body(api.get(`/api/assignments/${id}/sql/edit`));
export const saveSqlEdit = (id, input) => body(api.put(`/api/assignments/${id}/sql/edit`, input));
export const validateSqlEdit = id => body(api.post(`/api/assignments/${id}/sql/validate`));
export const publishSqlEdit = (id, input) => body(api.post(`/api/assignments/${id}/sql/publish`, input));
export const sqlAssignment = id => body(api.get(`/api/assignments/${id}/sql`));
export const runSql = input => body(api.post('/api/solutions/sql/run', input));
export const sqlPreview = id => body(api.get(`/api/solutions/sql/previews/${id}`));
export async function checkSql(input) {
  try { return await body(api.post('/api/solutions/sql/check', input)); }
  finally { window.dispatchEvent(new Event('quota:changed')); }
}
