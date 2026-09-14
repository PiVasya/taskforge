export function subscribeAdminSolutionEvents({ onEvent, onOpen, onError }) {
  const source = new EventSource('/api/admin/solution-events', { withCredentials: true });
  source.addEventListener('solution', (event) => {
    try {
      const payload = JSON.parse(event.data);
      if (payload && typeof onEvent === 'function') onEvent(payload);
    } catch {
    }
  });
  source.onopen = () => onOpen?.();
  source.onerror = (error) => onError?.(error, source.readyState);
  return () => source.close();
}
