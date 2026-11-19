// The administrative feed has TWO owners. A single Redis subscription cannot
// observe both, and a disconnected region must be able to replay DB events.
const FEED_PATHS = [
  '/api/admin/solution-events',       // code, SQL, images (Solutions API)
  '/api/admin/task-solution-events',  // tests, math (Tasks API)
];

export function subscribeAdminSolutionEvents({ onEvent, onOpen, onError }) {
  const states = new Array(FEED_PATHS.length).fill(false);
  let disposed = false;
  const sources = FEED_PATHS.map((path, index) => {
    const source = new EventSource(path, { withCredentials: true });
    source.addEventListener('solution', (event) => {
      if (disposed) return;
      try {
        const payload = JSON.parse(event.data);
        if (payload && typeof onEvent === 'function') onEvent(payload);
      } catch {
        // Ignore a malformed event; EventSource keeps the durable replay cursor.
      }
    });
    source.onopen = () => {
      if (disposed) return;
      states[index] = true;
      if (states.every(Boolean)) onOpen?.();
    };
    source.onerror = (error) => {
      if (disposed) return;
      states[index] = false;
      onError?.(error, source.readyState);
    };
    return source;
  });
  return () => {
    disposed = true;
    for (const source of sources) source.close();
  };
}
