


import * as signalR from '@microsoft/signalr';
import { instrumentSignalRConnection, logFrontendEvent } from '../devtools/frontendDiagnostics';

let conn = null;
let token = null;
let startPromise = null;

function build(tokenValue) {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/support', {
      
      
      withCredentials: true,
      accessTokenFactory: () => tokenValue || null,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

export function getSupportHub(accessToken) {
  
  if (conn && token === accessToken) return conn;

  
  try {
    if (conn) conn.stop();
  } catch {}

  token = accessToken || null;
  conn = instrumentSignalRConnection(build(token), 'support');
  startPromise = null;
  return conn;
}

export async function ensureSupportHubStarted(accessToken) {
  const c = getSupportHub(accessToken);

  if (c.state === signalR.HubConnectionState.Connected) return c;

  if (!startPromise) {
    logFrontendEvent('realtime', 'start', { hub: 'support' });
    startPromise = c.start().then(() => { logFrontendEvent('realtime', 'connected', { hub: 'support', connectionId: c.connectionId || null }); return c; }).catch((err) => {
      logFrontendEvent('realtime', 'connect-error', { hub: 'support', error: err }, 'error');
      startPromise = null;
      throw err;
    });
  }

  await startPromise;
  return c;
}

export async function stopSupportHub() {
  if (!conn) return;
  try {
    await conn.stop();
  } catch {}
  conn = null;
  token = null;
  startPromise = null;
}
