import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

function imageRequestConfig(timeout) {
  return {
    timeout,
    validateStatus: (status) => (status >= 200 && status < 300) || status === 400,
  };
}



export async function uploadImageTestReference(assignmentId, file, threshold = 90) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('threshold', String(threshold));

  const res = await api.post(`/api/assignments/${assignmentId}/image-test/reference`, fd);
  return res.data;
}


export async function uploadImageTestExpectedImage(assignmentId, file) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('folder', `image-tests/reference/${assignmentId}`);
  const res = await api.post('/api/files/images', fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res.data;
}


export async function compareImageTest(assignmentId, file) {
  const fd = new FormData();
  fd.append('file', file);
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/compare`, fd, imageRequestConfig());
  emitQuotaChanged();
  return res.data;
}


export async function compareImageTestCode(assignmentId, language, code, input = "", debug = false) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/compare-code`, {
    language,
    code,
    input,
    debug,
  }, imageRequestConfig());
  emitQuotaChanged();
  return res.data;
}


export async function runImageTestCode(assignmentId, language, code, input = "", debug = false) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/run-code`, {
    language,
    code,
    input,
    debug,
  }, imageRequestConfig(60000));
  emitQuotaChanged();
  return res.data;
}


export async function submitImageTestCode(assignmentId, language, code, input = "", debug = false) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/submit-code`, {
    language,
    code,
    input,
    debug,
  }, imageRequestConfig(90000));
  emitQuotaChanged();
  return res.data;
}
