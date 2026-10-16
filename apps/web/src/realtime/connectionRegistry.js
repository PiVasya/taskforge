const connections = new Map();

export function registerAuthenticatedConnection(connection, dispose = () => {}) {
  connections.set(connection, dispose);
  return connection;
}

export function unregisterAuthenticatedConnection(connection) {
  connections.delete(connection);
}

export function stopAuthenticatedConnections() {
  const current = Array.from(connections.entries());
  connections.clear();
  for (const [connection, dispose] of current) {
    try { dispose(); } catch {}
    try { Promise.resolve(connection.stop()).catch(() => {}); } catch {}
  }
}
