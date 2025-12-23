// clientapp/src/api/support.js
// Используется для отправки обращений в поддержку.

import { api } from './http';

/**
 * Отправляет обращение в поддержку.
 * @param {{ type: string, message: string }} payload
 * @returns {Promise<void>}
 */
export async function sendSupportMessage(payload) {
  await api.post('/api/support', payload);
}
