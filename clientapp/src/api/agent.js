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

export async function polishAgentGeneratedTask(conversationId, payload) {
  const res = await api.post(`/api/agent/conversations/${conversationId}/polish-task`, payload);
  return res.data;
}

export async function polishAgentGeneratedTasks(conversationId, payload) {
  const res = await api.post(`/api/agent/conversations/${conversationId}/polish-tasks`, payload);
  return res.data;
}

export async function uploadAgentAttachment(conversationId, file) {
  const fd = new FormData();
  fd.append('file', file);
  const res = await api.post(`/api/agent/conversations/${conversationId}/attachments`, fd);
  return res.data;
}

export async function applyAgentArtifact(artifactId, payload = {}) {
  const res = await api.post(`/api/agent/artifacts/${artifactId}/apply`, payload);
  return res.data;
}

export async function applyAgentRunArtifact(runId, artifactId, payload = {}) {
  const res = await api.post(`/api/agent/runs/${runId}/artifacts/${artifactId}/apply`, payload);
  return res.data;
}


export async function getAgentConversationDebugDump(conversationId, { format = 'text' } = {}) {
  const res = await api.get(`/api/agent/conversations/${conversationId}/debug-dump`, {
    params: { format },
    responseType: format === 'text' || format === 'txt' || format === 'log' ? 'text' : 'json',
  });
  return res.data;
}
