import * as signalR from '@microsoft/signalr';
import api, { getAccessToken } from './http';

export async function getAdminAssignmentInsights(assignmentId) {
  const { data } = await api.get(`/api/admin/assignments/${assignmentId}/insights`);
  return data;
}

export async function getAdminAssignmentTimeline(assignmentId, userId) {
  const { data } = await api.get(`/api/admin/assignments/${assignmentId}/users/${userId}/timeline`);
  return data;
}

export function createAssignmentAnalyticsConnection(assignmentId, onActivity, onStatus) {
  if (!assignmentId || typeof window === 'undefined') return null;
  const tokenFactory = () => getAccessToken?.() || '';
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/assignment-analytics', { accessTokenFactory: tokenFactory })
    .withAutomaticReconnect()
    .build();

  connection.on('assignmentActivity', (items) => {
    const rows = Array.isArray(items) ? items : [items].filter(Boolean);
    if (rows.length) onActivity?.(rows);
  });

  connection.onreconnecting(() => onStatus?.('reconnecting'));
  connection.onreconnected(() => {
    onStatus?.('connected');
    connection.invoke('JoinAssignment', String(assignmentId)).catch(() => {});
  });
  connection.onclose(() => onStatus?.('offline'));

  onStatus?.('connecting');
  connection.start()
    .then(() => connection.invoke('JoinAssignment', String(assignmentId)))
    .then(() => onStatus?.('connected'))
    .catch(() => onStatus?.('offline'));

  return connection;
}
