import { api } from './http';

// Helper to include bearer token in requests
function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

/**
 * Fetch the current user's profile from the backend.
 * Returns an object with fields like email, firstName, lastName, phoneNumber, etc.
 */
export async function getProfile() {
  const { data } = await api.get('/api/profile', {
    headers: authHeaders(),
  });
  return data;
}

/**
 * Update the current user's profile.  Only provided fields will be updated.
 * @param {*} payload An object containing updated profile fields.
 */
export async function updateProfile(payload) {
  await api.put('/api/profile', payload, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  });
  return true;
}
