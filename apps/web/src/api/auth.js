import api, { cookieSession } from './http';
import { readSessionInfo } from '../auth/cookieSession';

export async function registerUser(payload) {
  const res = await api.post('/api/auth/register', payload, { __skipAuthRefresh: true });
  return res.data;
}

export const AuthApi = {
  login(payload) {
    return cookieSession.login(payload);
  },

  async session() {
    const res = await api.get('/api/auth/session', { __skipAuthRefresh: true });
    return readSessionInfo(res.data);
  },

  remember(rememberMe) {
    return cookieSession.remember(rememberMe);
  },

  refresh() {
    return cookieSession.refresh();
  },

  logout() {
    return cookieSession.logout();
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
