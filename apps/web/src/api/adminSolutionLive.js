import { getSolutionDetails, getAdminImageSolutionDetails } from './admin';
import { getAdminTaskTestAttemptReview } from './taskTestAttempts';
import { getAdminMathAttemptReview } from './mathTaskAttempts';
import { getAdminUser } from './adminUsers';

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

export async function loadAdminSolutionLiveDetail(event) {
  const kind = String(event?.kind || '').toLowerCase();
  if (kind === 'test') return getAdminTaskTestAttemptReview(event.itemId);
  if (kind === 'math') return getAdminMathAttemptReview(event.itemId);
  if (kind === 'image') return getAdminImageSolutionDetails(event.itemId);
  return getSolutionDetails(event.itemId);
}

export { getAdminUser };
