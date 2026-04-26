import api from './http';

export async function listAgentConversations(params = {}) {
  const res = await api.get('/api/agent/conversations', { params });
  return res.data || [];
}

export async function createAgentConversation(payload = {}) {
  const res = await api.post('/api/agent/conversations', payload);
  return res.data;
}

export async function getAgentConversation(conversationId) {
  const res = await api.get(`/api/agent/conversations/${conversationId}`);
  return res.data;
}

export async function sendAgentMessage(conversationId, payload) {
  const res = await api.post(`/api/agent/conversations/${conversationId}/messages`, payload);
  return res.data;
}

export async function cancelAgentRun(runId, reason = 'user_requested') {
  const res = await api.post(`/api/agent/runs/${runId}/cancel`, { reason });
  return res.data;
}
