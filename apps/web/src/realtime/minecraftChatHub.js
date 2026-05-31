import * as signalR from '@microsoft/signalr';

let conn = null;
let token = null;
let startPromise = null;

function build(tokenValue) {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/minecraft-chat', {
      withCredentials: true,
      accessTokenFactory: () => tokenValue || null,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

export function getMinecraftChatHub(accessToken) {
  if (conn && token === accessToken) return conn;
  try { if (conn) conn.stop(); } catch {}
  token = accessToken || null;
  conn = build(token);
  startPromise = null;
  return conn;
}

export async function ensureMinecraftChatHubStarted(accessToken) {
  const c = getMinecraftChatHub(accessToken);
  if (c.state === signalR.HubConnectionState.Connected) return c;
  if (!startPromise) {
    startPromise = c.start().catch((err) => { startPromise = null; throw err; });
  }
  await startPromise;
  return c;
}
