import { BROWSER_REQUEST_HEADERS } from '../auth/cookieSession';
import { registerAuthenticatedConnection } from './connectionRegistry';
import * as signalR from '@microsoft/signalr';
import { instrumentSignalRConnection, logFrontendEvent } from '../devtools/frontendDiagnostics';

let conn = null;
let startPromise = null;

function build() {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/minecraft-chat', {
      withCredentials: true,
      headers: BROWSER_REQUEST_HEADERS,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

export function getMinecraftChatHub() {
  if (conn) return conn;
  const current = instrumentSignalRConnection(build(), 'minecraft-chat');
  conn = current;
  startPromise = null;
  current.onclose(() => { if (conn === current) startPromise = null; });
  registerAuthenticatedConnection(current, () => {
    if (conn === current) { conn = null; startPromise = null; }
  });
  return current;
}

export async function ensureMinecraftChatHubStarted() {
  const c = getMinecraftChatHub();
  if (c.state === signalR.HubConnectionState.Connected) return c;
  if (!startPromise) {
    logFrontendEvent('realtime', 'start', { hub: 'minecraft-chat' });
    startPromise = c.start()
      .then(() => { logFrontendEvent('realtime', 'connected', { hub: 'minecraft-chat', connectionId: c.connectionId || null }); return c; })
      .catch((err) => { logFrontendEvent('realtime', 'connect-error', { hub: 'minecraft-chat', error: err }, 'error'); if (conn === c) startPromise = null; throw err; });
  }
  await startPromise;
  return c;
}
