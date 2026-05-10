import api from './http';

// UI settings for current user (persisted in DB).

export async function getMyUiSettings() {
  const res = await api.get('/api/me/ui-settings');
  return res.data;
}

export async function saveMyUiSettings(payload) {
  const res = await api.put('/api/me/ui-settings', payload);
  return res.data;
}
