



import api from './http';


export async function createSupportTicket(payload) {
  const { data } = await api.post('/api/support', payload);
  const ticketId = data?.ticketId || data?.id || data?.Id;
  return { ...data, ticketId };
}


export async function listSupportTickets() {
  const { data } = await api.get('/api/support');
  return data;
}


export async function getSupportTicket(ticketId) {
  const { data } = await api.get(`/api/support/${ticketId}`);
  return data;
}


export async function sendSupportMessage(ticketId, payload) {
  const { data } = await api.post(`/api/support/${ticketId}`, payload);
  return data;
}
