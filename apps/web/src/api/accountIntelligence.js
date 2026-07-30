import api from './http';

export async function startAccountAnalysis() {
  const { data } = await api.post('/api/admin/ai/account-manager/runs');
  return data;
}

export async function getLatestAccountAnalysisRun() {
  const { data } = await api.get('/api/admin/ai/account-manager/runs/latest');
  return data?.run || null;
}

export async function getAccountAnalysisRun(runId) {
  const { data } = await api.get(`/api/admin/ai/account-manager/runs/${runId}`);
  return data;
}

export async function getAccountAnalysisFindings(params = {}) {
  const { data } = await api.get('/api/admin/ai/account-manager/findings', { params });
  return data;
}

export async function decideAccountFinding(findingId, decision, note = null) {
  const { data } = await api.post(`/api/admin/ai/account-manager/findings/${findingId}/decision`, { decision, note });
  return data;
}

export async function decideAccount(userId, decision, note = null) {
  const { data } = await api.post(`/api/admin/ai/account-manager/accounts/${userId}/decision`, { decision, note });
  return data;
}

export async function getAccountReviews(params = {}) {
  const { data } = await api.get('/api/admin/ai/account-manager/reviews', { params });
  return data;
}
