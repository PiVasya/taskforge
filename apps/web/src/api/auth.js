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

  async getPasswordRecoveryStatus() {
    const res = await api.get('/api/auth/password-recovery/status', { __skipAuthRefresh: true });
    return res.data;
  },

  async cancelPasswordRecovery() {
    const res = await api.post('/api/auth/password-recovery/cancel', null, { __skipAuthRefresh: true });
    return res.data;
  },

  async requestPasswordRecovery(identity) {
    const res = await api.post('/api/auth/password-recovery/request', { identity }, { __skipAuthRefresh: true });
    return res.data;
  },

  async verifyPasswordRecovery(verificationCode) {
    const res = await api.post('/api/auth/password-recovery/verify', { verificationCode }, { __skipAuthRefresh: true });
    return res.data;
  },

  async resetPasswordRecovery(newPassword) {
    const res = await api.post('/api/auth/password-recovery/reset', { newPassword }, { __skipAuthRefresh: true });
    return res.data;
  },
};
