import api, { getAccessToken } from './http';

export async function getCourseMap(courseId) {
  const res = await api.get(`/api/courses/${courseId}/map`);
  return res.data;
}

export async function getLearningCourseMap(courseId) {
  const res = await api.get(`/api/courses/${courseId}/learning-map`);
  return res.data;
}

async function openLearningMapStream(courseId, signal, retry = true) {
  const headers = { Accept: 'application/x-ndjson' };
  const token = getAccessToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(`/api/courses/${courseId}/learning-map/stream`, {
    method: 'GET',
    credentials: 'include',
    headers,
    signal,
  });

  if (response.status === 401 && retry) {
    try {
      await api.get('/api/profile');
    } catch {
      return response;
    }
    return openLearningMapStream(courseId, signal, false);
  }
  return response;
}

export async function streamLearningCourseMap(courseId, {
  signal,
  onMeta,
  onSegment,
  onDone,
} = {}) {
  const response = await openLearningMapStream(courseId, signal, true);
  if (!response.ok) {
    let message = 'Не удалось загрузить карту курса';
    try {
      const payload = await response.json();
      message = payload?.message || message;
    } catch {}
    const error = new Error(message);
    error.response = { status: response.status, data: { message } };
    throw error;
  }
  if (!response.body) throw new Error('Браузер не поддерживает потоковую загрузку карты');

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let meta = null;

  const handleLine = async (line) => {
    const trimmed = line.trim();
    if (!trimmed) return;
    const event = JSON.parse(trimmed);
    if (event?.type === 'meta') {
      meta = event.data || null;
      await onMeta?.(meta);
      return;
    }
    if (event?.type === 'segment') {
      await onSegment?.(event.data || null, meta);
      return;
    }
    if (event?.type === 'done') await onDone?.(event.data || null, meta);
  };

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let newline = buffer.indexOf('\n');
    while (newline >= 0) {
      const line = buffer.slice(0, newline);
      buffer = buffer.slice(newline + 1);
      await handleLine(line);
      newline = buffer.indexOf('\n');
    }
  }
  buffer += decoder.decode();
  if (buffer.trim()) await handleLine(buffer);
  return meta;
}

export async function getLearningCourseMapDelta(courseId, projectionToken, changedAssignmentId = null) {
  const res = await api.post(`/api/courses/${courseId}/learning-map/delta`, {
    projectionToken: projectionToken || null,
    changedAssignmentId: changedAssignmentId || null,
  });
  return res.data;
}

export async function saveCourseMap(courseId, expectedVersion, document) {
  const res = await api.put(`/api/courses/${courseId}/map`, {
    expectedVersion,
    document,
  });
  return res.data;
}
