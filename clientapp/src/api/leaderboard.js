import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export async function getLeaderboard(params = {}) {
  const res = await api.get('/api/leaderboard', { params });
  // Загрузка топа расходует квоту — обновим индикатор.
  emitQuotaChanged();
  return res.data;
}
