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

export async function deleteAccountFindingDecision(findingId) {
  const { data } = await api.delete(`/api/admin/ai/account-manager/findings/${findingId}/decision`);
  return data;
}

export async function deleteAccountDecision(userId) {
  const { data } = await api.delete(`/api/admin/ai/account-manager/accounts/${userId}/decision`);
  return data;
}

export async function updateAccountReview(reviewId, decision, note = null) {
  const { data } = await api.put(`/api/admin/ai/account-manager/reviews/${reviewId}`, { decision, note });
  return data;
}

export async function deleteAccountReview(reviewId) {
  const { data } = await api.delete(`/api/admin/ai/account-manager/reviews/${reviewId}`);
  return data;
}

export async function getBlockedAccounts(params = {}) {
  const { data } = await api.get('/api/admin/ai/account-manager/blocked-accounts', { params });
  return Array.isArray(data) ? data : [];
}

export async function blockAccount(userId, payload = {}) {
  const { data } = await api.put(`/api/admin/ai/account-manager/accounts/${userId}/block`, payload);
  return data;
}

export async function unblockAccount(userId) {
  const { data } = await api.delete(`/api/admin/ai/account-manager/accounts/${userId}/block`);
  return data;
}

export async function createAccountOperation(payload) {
  const { data } = await api.post('/api/admin/ai/account-manager/operations', payload);
  return data;
}

export async function getAccountOperations(params = {}) {
  const { data } = await api.get('/api/admin/ai/account-manager/operations', { params });
  return Array.isArray(data) ? data : [];
}

export async function getAccountOperation(operationId) {
  const { data } = await api.get(`/api/admin/ai/account-manager/operations/${operationId}`);
  return data;
}

export async function updateAccountOperation(operationId, payload) {
  const { data } = await api.put(`/api/admin/ai/account-manager/operations/${operationId}`, payload);
  return data;
}

export async function retryAccountOperation(operationId) {
  const { data } = await api.post(`/api/admin/ai/account-manager/operations/${operationId}/retry`);
  return data;
}

export async function cancelAccountOperation(operationId) {
  const { data } = await api.post(`/api/admin/ai/account-manager/operations/${operationId}/cancel`);
  return data;
}

export async function archiveAccountOperation(operationId) {
  const { data } = await api.delete(`/api/admin/ai/account-manager/operations/${operationId}`);
  return data;
}

export async function restoreAccountOperation(operationId) {
  const { data } = await api.post(`/api/admin/ai/account-manager/operations/${operationId}/restore`);
  return data;
}
