import api from './http';

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
