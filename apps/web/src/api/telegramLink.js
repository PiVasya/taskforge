import api from './http';

export async function getTelegramStatus() {
  const res = await api.get('/api/integrations/telegram/status');
  return res.data;
}

export async function generateTelegramCode() {
  const res = await api.post('/api/integrations/telegram/code');
  return res.data;
}

export async function unlinkTelegram() {
  await api.delete('/api/integrations/telegram/unlink');
}
