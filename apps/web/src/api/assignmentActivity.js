import api from './http';

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
      return navigator.sendBeacon(url, blob);
    }
    if (typeof fetch === 'function') {
      fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body,
        credentials: 'include',
        keepalive: true,
      }).catch(() => {});
      return true;
    }
  } catch {
  }
  return false;
}
