import api from './http';

export async function getMinecraftStatus() {
  const res = await api.get('/api/integrations/minecraft/status');
  return res.data;
}

export async function requestMinecraftLink(nick) {
  const res = await api.post('/api/integrations/minecraft/request', { nick });
  return res.data;
}

export async function confirmMinecraftLink(code) {
  const res = await api.post('/api/integrations/minecraft/confirm', { code });
  return res.data;
}

export async function unlinkMinecraft(linkId) {
  if (!linkId) throw new Error('Minecraft link id is required');
  const res = await api.delete(`/api/integrations/minecraft/links/${linkId}`);
  return res.data;
}
