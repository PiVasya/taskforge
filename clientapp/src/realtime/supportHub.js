// clientapp/src/realtime/supportHub.js
// Единый singleton SignalR соединения для техподдержки.

import * as signalR from '@microsoft/signalr';

let conn = null;
let token = null;
let startPromise = null;

function build(tokenValue) {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/support', {
      accessTokenFactory: () => tokenValue,
    })
    // быстрые первые ретраи + дальше реже
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

export function getSupportHub(accessToken) {
  if (!accessToken) return null;
  if (conn && token === accessToken) return conn;

  // token changed -> recreate
  try {
    if (conn) conn.stop();
  } catch {}

  token = accessToken;
  conn = build(accessToken);
  startPromise = null;
  return conn;
}

export async function ensureSupportHubStarted(accessToken) {
  const c = getSupportHub(accessToken);
  if (!c) return null;

  if (c.state === signalR.HubConnectionState.Connected) return c;

  if (!startPromise) {
    startPromise = c
      .start()
      .catch((err) => {
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
