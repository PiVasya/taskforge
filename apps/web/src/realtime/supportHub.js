


import * as signalR from '@microsoft/signalr';

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
  conn = build(token);
  startPromise = null;
  return conn;
}

export async function ensureSupportHubStarted(accessToken) {
  const c = getSupportHub(accessToken);

  if (c.state === signalR.HubConnectionState.Connected) return c;

  if (!startPromise) {
    startPromise = c.start().catch((err) => {
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
