import api from './http';

export async function getDiscordStatus() {
  const { data } = await api.get('/api/integrations/discord/status');
  return data;
}

export async function generateDiscordCode() {
  const { data } = await api.post('/api/integrations/discord/code');
  return data;
}

export async function unlinkDiscord() {
  await api.delete('/api/integrations/discord/unlink');
}

export async function getMessengerStatus() {
  const { data } = await api.get('/api/integrations/messengers/status');
  return data;
}

export async function setPrimaryMessenger(messenger) {
  const { data } = await api.put('/api/integrations/messengers/primary', { messenger });
  return data;
}
