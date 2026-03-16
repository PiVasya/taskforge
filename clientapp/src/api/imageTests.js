import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

// Загрузить/заменить эталонную картинку для image-test.
// threshold — порог совпадения в процентах (0..100)
export async function uploadImageTestReference(assignmentId, file, threshold = 90) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('threshold', String(threshold));

  const res = await api.post(`/api/assignments/${assignmentId}/image-test/reference`, fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res.data;
}

// Сравнить загруженную картинку с эталоном.
export async function compareImageTest(assignmentId, file) {
  const fd = new FormData();
  fd.append('file', file);
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/compare`, fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  emitQuotaChanged();
  return res.data;
}

// Запустить код внутри image-runner, получить PNG и сравнить с эталоном.
export async function compareImageTestCode(assignmentId, language, code, input = "", debug = false) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/compare-code`, {
    language,
    code,
    input,
    debug,
  });
  emitQuotaChanged();
  return res.data;
}

// Пробный прогон: только рендер, без сравнения с эталоном.
export async function runImageTestCode(assignmentId, language, code, input = "", debug = false) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/run-code`, {
    language,
    code,
    input,
    debug,
  }, {
    timeout: 60000, // 60 секунд для генерации картинки
  });
  emitQuotaChanged();
  return res.data;
}

// Финальная отправка: рендер + сравнение (то же самое, что compare-code).
export async function submitImageTestCode(assignmentId, language, code, input = "", debug = false) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/submit-code`, {
    language,
    code,
    input,
    debug,
  }, {
    timeout: 90000, // 90 секунд для генерации + сравнения с нейронкой
  });
  emitQuotaChanged();
  return res.data;
}
