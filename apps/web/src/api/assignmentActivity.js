import api from './http';
import { logFrontendEvent } from '../devtools/frontendDiagnostics';

export async function recordAssignmentActivityBatch(assignmentId, payload) {
  if (!assignmentId || !payload) return null;
  const { data } = await api.post(`/api/assignments/${assignmentId}/activity/batch`, payload);
  return data;
}

export function sendAssignmentActivityBeacon(assignmentId, payload) {
  if (!assignmentId || !payload || typeof navigator === 'undefined') return false;
  try {
    const body = JSON.stringify(payload);
    const url = `/api/assignments/${encodeURIComponent(assignmentId)}/activity/batch`;
    if (typeof navigator.sendBeacon === 'function') {
      const blob = new Blob([body], { type: 'application/json' });
      const accepted = navigator.sendBeacon(url, blob);
      logFrontendEvent('activity', 'beacon', { assignmentId: String(assignmentId), transport: 'sendBeacon', accepted, bytes: body.length });
      return accepted;
    }
    if (typeof fetch === 'function') {
      logFrontendEvent('activity', 'beacon', { assignmentId: String(assignmentId), transport: 'fetch', accepted: true, bytes: body.length });
      fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body,
        credentials: 'include',
        keepalive: true,
      }).then((response) => {
        logFrontendEvent('activity', 'beacon-response', { assignmentId: String(assignmentId), status: response.status });
      }).catch((error) => {
        logFrontendEvent('activity', 'beacon-error', { assignmentId: String(assignmentId), error }, 'warn');
      });
      return true;
    }
  } catch {
  }
  return false;
}
