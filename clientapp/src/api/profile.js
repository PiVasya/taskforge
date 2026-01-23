import api from './http';

export async function getProfile() {
  const res = await api.get('/api/profile');
  return res.data;
}

export async function updateProfile(payload) {
  const res = await api.put('/api/profile', payload);
  return res.data;
}

// Change the current user's password. Requires the current password and the
// desired new password. Returns a message if successful or throws on error.
export async function changePassword(currentPassword, newPassword) {
  const res = await api.post('/api/profile/change-password', {
    currentPassword,
    newPassword,
  });
  return res.data;
}

// Change the current user's email. Requires confirmation of the current
// password and a new unique email address. Returns a message on success.
export async function changeEmail(newEmail, password) {
  const res = await api.post('/api/profile/change-email', {
    newEmail,
    password,
  });
  return res.data;
}
