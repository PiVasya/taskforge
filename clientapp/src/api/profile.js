import api from './http';

export async function getProfile() {
  const res = await api.get('/api/profile');
  return res.data;
}

export async function updateProfile(payload) {
  const res = await api.put('/api/profile', payload);
  return res.data;
}



export async function changePassword(currentPassword, newPassword) {
  const res = await api.post('/api/profile/change-password', {
    currentPassword,
    newPassword,
  });
  return res.data;
}



export async function changeEmail(newEmail, password) {
  const res = await api.post('/api/profile/change-email', {
    newEmail,
    password,
  });
  return res.data;
}
