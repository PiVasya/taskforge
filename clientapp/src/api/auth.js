import api from './http';

// The app uses AuthApi from AuthContext, but some pages import named helpers.
// Keep both styles to avoid broken imports after refactors.

export async function registerUser(payload) {
  // Register does NOT set cookies (login does). We still skip refresh logic.
  const res = await api.post('/api/auth/register', payload, { __skipAuthRefresh: true });
  return res.data;
}

export const AuthApi = {
  async login(payload) {
    // Sets HttpOnly cookies on success. Also returns accessToken for in-memory role parsing.
    const res = await api.post('/api/auth/login', payload, { __skipAuthRefresh: true });
    return res.data;
  },

  async refresh() {
    const res = await api.post('/api/auth/refresh', null, { __skipAuthRefresh: true });
    return res.data;
  },

  async logout() {
    const res = await api.post('/api/auth/logout', null, { __skipAuthRefresh: true });
    return res.data;
  },
};
