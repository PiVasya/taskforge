import * as signalR from '@microsoft/signalr';
import api from './http';
import { BROWSER_REQUEST_HEADERS } from '../auth/cookieSession';
import { registerAuthenticatedConnection, unregisterAuthenticatedConnection } from '../realtime/connectionRegistry';

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
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/assignment-analytics', { withCredentials: true, headers: BROWSER_REQUEST_HEADERS })
    .withAutomaticReconnect()
    .build();

  registerAuthenticatedConnection(connection);
  connection.on('assignmentActivity', (items) => {
    const rows = Array.isArray(items) ? items : [items].filter(Boolean);
    if (rows.length) onActivity?.(rows);
  });

  connection.onreconnecting(() => onStatus?.('reconnecting'));
  connection.onreconnected(() => {
    onStatus?.('connected');
    connection.invoke('JoinAssignment', String(assignmentId)).catch(() => {});
  });
  connection.onclose(() => { unregisterAuthenticatedConnection(connection); onStatus?.('offline'); });

  onStatus?.('connecting');
  connection.start()
    .then(() => connection.invoke('JoinAssignment', String(assignmentId)))
    .then(() => onStatus?.('connected'))
    .catch(() => onStatus?.('offline'));

  return connection;
}
