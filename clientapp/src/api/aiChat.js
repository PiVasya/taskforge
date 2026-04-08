import api from './http';

export async function getAiChatSessions() {
  const { data } = await api.get('/api/admin/ai/chat/sessions');
  return Array.isArray(data) ? data : [];
}

export async function createAiChatSession(payload = {}) {
  const { data } = await api.post('/api/admin/ai/chat/sessions', payload);
  return data;
}

export async function getAiChatSession(id) {
  const { data } = await api.get(`/api/admin/ai/chat/sessions/${id}`);
  return data;
}

export async function updateAiChatSession(id, payload = {}) {
  const { data } = await api.put(`/api/admin/ai/chat/sessions/${id}`, payload);
  return data;
}

export async function deleteAiChatSession(id) {
  await api.delete(`/api/admin/ai/chat/sessions/${id}`);
}

export async function sendAiChatMessage(id, payload) {
  const { data } = await api.post(`/api/admin/ai/chat/sessions/${id}/messages`, payload);
  return data;
}

export async function uploadAiChatFile(file) {
  const fd = new FormData();
  fd.append('file', file);
  const { data } = await api.post('/api/admin/ai/chat/files', fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return data;
}

export async function confirmAiChatTool(id, payload) {
  const { data } = await api.post(`/api/admin/ai/chat/sessions/${id}/confirm-tool`, payload);
  return data;
}


export async function downloadAiChatExport(id, format = 'md') {
  const response = await api.get(`/api/admin/ai/chat/sessions/${id}/export`, {
    params: { format },
    responseType: 'blob',
  });
  const header = response.headers?.['content-disposition'] || response.headers?.['Content-Disposition'] || '';
  const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(header);
  const fileName = decodeURIComponent(match?.[1] || match?.[2] || `ai-chat-history.${format}`);
  return { blob: response.data, fileName };
}
