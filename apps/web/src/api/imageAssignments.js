import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

function imageRequestConfig(timeout) {
  return { timeout, validateStatus: (status) => (status >= 200 && status < 300) || status === 400 };
}

export const getImageAssignment = async (id) => (await api.get(`/api/image-assignments/${id}`)).data;
export const getImageSolveShell = async (id) => (await api.get(`/api/image-assignments/${id}/solve-shell`)).data;
export const getImageStatement = async (id) => (await api.get(`/api/image-assignments/${id}/statement`)).data;
export const getImageTests = async (id) => (await api.get(`/api/image-assignments/${id}/tests`)).data;
export const getImageAssignmentForEdit = async (id) => (await api.get(`/api/image-assignments/${id}/edit`)).data;
export const createImageAssignment = async (courseId, payload) => (await api.post(`/api/courses/${courseId}/image-assignments`, payload)).data;
export const updateImageAssignment = async (id, payload) => (await api.put(`/api/image-assignments/${id}`, payload)).data;

export async function uploadImageExpectedImage(id, file) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('folder', `image-tests/reference/${id}`);
  return (await api.post('/api/files/images', fd, { headers: { 'Content-Type': 'multipart/form-data' } })).data;
}

export async function uploadImageReference(id, file, threshold = 90) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('threshold', String(threshold));
  return (await api.post(`/api/image-assignments/${id}/reference`, fd)).data;
}

export async function compareImage(id, file) {
  const fd = new FormData();
  fd.append('file', file);
  const res = await api.post(`/api/image-assignments/${id}/compare`, fd, imageRequestConfig());
  emitQuotaChanged();
  return res.data;
}

export async function runImageCode(id, language, code, input = '', debug = false) {
  const res = await api.post(`/api/image-assignments/${id}/run-code`, { language, code, input, debug }, imageRequestConfig(60000));
  emitQuotaChanged();
  return res.data;
}

export async function compareImageCode(id, language, code, input = '', debug = false) {
  const res = await api.post(`/api/image-assignments/${id}/compare-code`, { language, code, input, debug }, imageRequestConfig());
  emitQuotaChanged();
  return res.data;
}
