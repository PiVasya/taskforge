import { api } from './http';

/**
 * Создать новое обращение в поддержку.
 * @param {{ type: string, message: string }} payload
 * @returns {Promise<{ ticketId: string }>}
 */
export async function createSupportTicket(payload) {
  const { data } = await api.post('/api/support', payload);
  return data;
}

/**
 * Получить список всех обращений текущего пользователя.
 * @returns {Promise<Array>}
 */
export async function listSupportTickets() {
  const { data } = await api.get('/api/support');
  return data;
}

/**
 * Получить обращение и все сообщения.
 * @param {string} ticketId
 * @returns {Promise<{ ticket: object, messages: Array }>}
 */
export async function getSupportTicket(ticketId) {
  const { data } = await api.get(`/api/support/${ticketId}`);
  return data;
}

/**
 * Отправить ответ в существующее обращение.
 * @param {string} ticketId
 * @param {{ message: string }} payload
 * @returns {Promise<{ id: string }>}
 */
export async function sendSupportMessage(ticketId, payload) {
  const { data } = await api.post(`/api/support/${ticketId}`, payload);
  return data;
}
