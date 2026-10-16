import { BROWSER_REQUEST_HEADERS } from '../auth/cookieSession';
import { registerAuthenticatedConnection, unregisterAuthenticatedConnection } from './connectionRegistry';
import * as signalR from '@microsoft/signalr';
import { instrumentSignalRConnection } from '../devtools/frontendDiagnostics';

const connections = new Map();

function keyFor(userId, courseId) {
  return `${userId || 'anonymous'}:${courseId}`;
}

export function createCourseMapPresenceConnection(userId, courseId) {
  const key = keyFor(userId, courseId);
  const existing = connections.get(key);
  if (existing) return existing;

  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/course-map', {
      withCredentials: true,
      headers: BROWSER_REQUEST_HEADERS,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.None)
    .build();

  instrumentSignalRConnection(connection, 'course-map', { courseId: String(courseId || '') });
  connections.set(key, connection);
  registerAuthenticatedConnection(connection, () => {
    if (connections.get(key) === connection) connections.delete(key);
  });
  return connection;
}

export async function disposeCourseMapPresenceConnection(userId, courseId) {
  const key = keyFor(userId, courseId);
  const connection = connections.get(key);
  if (!connection) return;
  connections.delete(key);
  unregisterAuthenticatedConnection(connection);
  try { await connection.stop(); } catch {}
}
