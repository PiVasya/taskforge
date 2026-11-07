import api from './http';

export async function getMessengerStatus() {
  const { data } = await api.get('/api/integrations/messengers/status');
  return data;
}

export async function setPrimaryMessenger(messenger) {
  const { data } = await api.put('/api/integrations/messengers/primary', { messenger });
  return data;
}
