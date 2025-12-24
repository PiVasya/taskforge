// clientapp/src/api/support.js
// Набор функций для работы с API техподдержки. Эти функции
// используют экземпляр axios `api` из http.js для выполнения запросов
// к серверу.

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
 * Получить список обращений текущего пользователя (или всех, если пользователь — админ).
 * @returns {Promise<any[]>}
 */
export async function listSupportTickets() {
  const { data } = await api.get('/api/support');
  return data;
}

/**
 * Получить подробности обращения и все его сообщения.
 * @param {string} ticketId
 * @returns {Promise<{ ticket: any, messages: any[] }>}
 */
export async function getSupportTicket(ticketId) {
  const { data } = await api.get(`/api/support/${ticketId}`);
  return data;
}

/**
 * Отправить новое сообщение в обращение (ответ пользователя).
 * @param {string} ticketId
 * @param {{ message: string }} payload
 * @returns {Promise<{ id: string }>}
 */
export async function sendSupportMessage(ticketId, payload) {
  const { data } = await api.post(`/api/support/${ticketId}`, payload);
  return data;
}