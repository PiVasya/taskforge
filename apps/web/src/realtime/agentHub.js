import * as signalR from '@microsoft/signalr';
import { instrumentSignalRConnection, logFrontendEvent } from '../devtools/frontendDiagnostics';

let conn = null;
let token = null;
let startPromise = null;

function build(tokenValue) {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/agent', {
      withCredentials: true,
      accessTokenFactory: () => tokenValue || null,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.None)
    .build();
}

export function getAgentHub(accessToken) {
  if (conn && token === accessToken) return conn;

  try {
    if (conn) conn.stop();
  } catch {}

  token = accessToken || null;
  conn = instrumentSignalRConnection(build(token), 'agent');
  startPromise = null;
  return conn;
}

export async function ensureAgentHubStarted(accessToken) {
  const c = getAgentHub(accessToken);
  if (c.state === signalR.HubConnectionState.Connected) return c;

  if (!startPromise) {
    logFrontendEvent('realtime', 'start', { hub: 'agent' });
    startPromise = c.start().then(() => { logFrontendEvent('realtime', 'connected', { hub: 'agent', connectionId: c.connectionId || null }); return c; }).catch((err) => {
      logFrontendEvent('realtime', 'connect-error', { hub: 'agent', error: err }, 'error');
      startPromise = null;
      throw err;
    });
  }

  await startPromise;
  return c;
}

export async function joinAgentConversation(accessToken, conversationId) {
  const c = await ensureAgentHubStarted(accessToken);
  if (!conversationId) return c;
  await c.invoke('JoinConversation', String(conversationId));
  return c;
}

export async function leaveAgentConversation(accessToken, conversationId) {
  if (!conn || !conversationId) return;
  try {
    await conn.invoke('LeaveConversation', String(conversationId));
  } catch {}
}

export async function stopAgentHub() {
  if (!conn) return;
  try {
    await conn.stop();
  } catch {}
  conn = null;
  token = null;
  startPromise = null;
}
