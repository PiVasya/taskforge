import { BROWSER_REQUEST_HEADERS } from '../auth/cookieSession';
import { registerAuthenticatedConnection, unregisterAuthenticatedConnection } from './connectionRegistry';
import * as signalR from '@microsoft/signalr';
import { instrumentSignalRConnection, logFrontendEvent } from '../devtools/frontendDiagnostics';

let conn = null;
let startPromise = null;

function build() {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/agent', {
      withCredentials: true,
      headers: BROWSER_REQUEST_HEADERS,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.None)
    .build();
}

export function getAgentHub() {
  if (conn) return conn;
  const current = instrumentSignalRConnection(build(), 'agent');
  conn = current;
  startPromise = null;
  current.onclose(() => { if (conn === current) startPromise = null; });
  registerAuthenticatedConnection(current, () => {
    if (conn === current) { conn = null; startPromise = null; }
  });
  return current;
}

export async function ensureAgentHubStarted() {
  const c = getAgentHub();
  if (c.state === signalR.HubConnectionState.Connected) return c;

  if (!startPromise) {
    logFrontendEvent('realtime', 'start', { hub: 'agent' });
    startPromise = c.start().then(() => { logFrontendEvent('realtime', 'connected', { hub: 'agent', connectionId: c.connectionId || null }); return c; }).catch((err) => {
      logFrontendEvent('realtime', 'connect-error', { hub: 'agent', error: err }, 'error');
      if (conn === c) startPromise = null;
      throw err;
    });
  }

  await startPromise;
  return c;
}

export async function joinAgentConversation(conversationId) {
  const c = await ensureAgentHubStarted();
  if (!conversationId) return c;
  await c.invoke('JoinConversation', String(conversationId));
  return c;
}

export async function leaveAgentConversation(conversationId) {
  if (!conn || !conversationId) return;
  try {
    await conn.invoke('LeaveConversation', String(conversationId));
  } catch {}
}

export async function stopAgentHub() {
  const current = conn;
  if (!current) return;
  conn = null;
  startPromise = null;
  unregisterAuthenticatedConnection(current);
  try {
    await current.stop();
  } catch {}
}
