import api from './http';

export async function getProfile() {
  const res = await api.get('/api/profile');
  return res.data;
}

export async function updateProfile(payload) {
  const res = await api.put('/api/profile', payload);
  return res.data;
}
