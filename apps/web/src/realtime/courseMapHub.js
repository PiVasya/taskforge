import * as signalR from '@microsoft/signalr';

const connections = new Map();

function keyFor(token, courseId) {
  return `${token || 'cookie'}:${courseId}`;
}

export function createCourseMapPresenceConnection(accessToken, courseId) {
  const key = keyFor(accessToken, courseId);
  const existing = connections.get(key);
  if (existing) return existing;

  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/course-map', {
      withCredentials: true,
      accessTokenFactory: () => accessToken || null,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.None)
    .build();

  connections.set(key, connection);
  return connection;
}

export async function disposeCourseMapPresenceConnection(accessToken, courseId) {
  const key = keyFor(accessToken, courseId);
  const connection = connections.get(key);
  if (!connection) return;
  connections.delete(key);
  try { await connection.stop(); } catch {}
}
