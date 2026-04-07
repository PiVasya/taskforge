import api from './http';

export async function getAiJobs(params = {}) {
  const { data } = await api.get('/api/admin/ai/jobs', { params });
  return data || { items: [], total: 0 };
}

export async function getAiJob(id) {
  const { data } = await api.get(`/api/admin/ai/jobs/${id}`);
  return data;
}

export async function getAiBatches() {
  const { data } = await api.get('/api/admin/ai/batches');
  return Array.isArray(data) ? data : [];
}

export async function getAiBatch(id) {
  const { data } = await api.get(`/api/admin/ai/batches/${id}`);
  return data;
}

export async function generateAiBatch(payload) {
  const { data } = await api.post('/api/admin/ai/batches/generate', payload);
  return data;
}

export async function analyzeAiAssignment(payload) {
  const { data } = await api.post('/api/admin/ai/analyze-assignment', payload);
  return data;
}

export async function reviewAiSubmission(payload) {
  const { data } = await api.post('/api/admin/ai/review-submission', payload);
  return data;
}

export async function reviewAiUser(payload) {
  const { data } = await api.post('/api/admin/ai/review-user', payload);
  return data;
}

export async function getAiSubmissionReviews(params = {}) {
  const { data } = await api.get('/api/admin/ai/submission-reviews', { params });
  return Array.isArray(data) ? data : [];
}

export async function getAiRiskReports(params = {}) {
  const { data } = await api.get('/api/admin/ai/risk-reports', { params });
  return Array.isArray(data) ? data : [];
}

export async function getAiAssignmentInsights(params = {}) {
  const { data } = await api.get('/api/admin/ai/assignment-insights', { params });
  return Array.isArray(data) ? data : [];
}

export async function getAiDrafts() {
  const { data } = await api.get('/api/admin/ai/drafts');
  return Array.isArray(data) ? data : [];
}

export async function reviewAiDraft(id, action) {
  await api.post(`/api/admin/ai/drafts/${id}/review`, { action });
}

export async function publishAiDraft(id, payload = {}) {
  const { data } = await api.post(`/api/admin/ai/drafts/${id}/publish`, payload);
  return data;
}

export async function validateAiDraft(id, payload = {}) {
  const { data } = await api.post(`/api/admin/ai/drafts/${id}/validate`, payload);
  return data;
}

export async function deleteAiDraft(id) {
  await api.delete(`/api/admin/ai/drafts/${id}`);
}

export async function deleteAiBatch(id) {
  await api.delete(`/api/admin/ai/batches/${id}`);
}

export async function deleteAiJob(id) {
  await api.delete(`/api/admin/ai/jobs/${id}`);
}

export async function clearAiJobs(status) {
  const { data } = await api.post('/api/admin/ai/jobs/clear', null, { params: status ? { status } : {} });
  return data;
}

export async function retryAiJob(id) {
  await api.post(`/api/admin/ai/jobs/${id}/retry`);
}

export async function cancelAiJob(id) {
  await api.post(`/api/admin/ai/jobs/${id}/cancel`);
}


export async function resolveAiFoundryChat(payload) {
  const { data } = await api.post('/api/admin/ai/chat/resolve', payload);
  return data;
}
