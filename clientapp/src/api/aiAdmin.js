import api from './http';

export async function getAiJobs(params = {}) {
  const { data } = await api.get('/api/admin/ai/jobs', { params });
  return data || { items: [], total: 0 };
}

export async function getAiJob(id) {
  const { data } = await api.get(`/api/admin/ai/jobs/${id}`);
  return data;
}

export async function createAiJob(payload) {
  const { data } = await api.post('/api/admin/ai/jobs', payload);
  return data;
}

export async function generateAiAssignmentFromText(payload) {
  const { data } = await api.post('/api/admin/ai/generate/from-text', payload);
  return data;
}

export async function generateAiAssignmentFromFile(payload) {
  const { data } = await api.post('/api/admin/ai/generate/from-file', payload);
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
