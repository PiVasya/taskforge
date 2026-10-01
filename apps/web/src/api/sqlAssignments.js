import api from './http';

const body = (promise) => promise.then((response) => response.data);
export const getSqlAssignment = (id) => body(api.get(`/api/sql-assignments/${id}`));
export const getSqlSolveShell = (id) => body(api.get(`/api/sql-assignments/${id}/solve-shell`));
export const getSqlStatement = (id) => body(api.get(`/api/sql-assignments/${id}/statement`));
export const getSqlTests = (id) => body(api.get(`/api/sql-assignments/${id}/tests`));
export const getSqlAssignmentForEdit = (id) => body(api.get(`/api/sql-assignments/${id}/edit`));
export const createSqlAssignment = (courseId, payload) => body(api.post(`/api/courses/${courseId}/sql-assignments`, payload));
export const updateSqlAssignment = (id, payload) => body(api.put(`/api/sql-assignments/${id}`, payload));
export const getSqlSpecEdit = (id) => body(api.get(`/api/sql-assignments/${id}/spec/edit`));
export const saveSqlSpecEdit = (id, input) => body(api.put(`/api/sql-assignments/${id}/spec/edit`, input));
export const validateSqlSpecEdit = (id) => body(api.post(`/api/sql-assignments/${id}/spec/validate`));
export const publishSqlSpecEdit = (id, input) => body(api.post(`/api/sql-assignments/${id}/spec/publish`, input));
