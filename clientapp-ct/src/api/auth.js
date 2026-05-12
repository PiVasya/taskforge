import api from './http';




export async function registerUser(payload) {
  
  const res = await api.post('/api/auth/register', payload, { __skipAuthRefresh: true });
  return res.data;
}

export const AuthApi = {
  async login(payload) {
    
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
