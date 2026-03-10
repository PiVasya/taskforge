import api from './http';

export async function getMinecraftChatMessages(take = 60) {
  const { data } = await api.get('/api/integrations/minecraft/chat/messages', { params: { take } });
  return Array.isArray(data) ? data : [];
}

export async function sendMinecraftChatMessage(message) {
  const { data } = await api.post('/api/integrations/minecraft/chat/messages', { message });
  return data;
}
