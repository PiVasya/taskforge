import api from './http';

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
  return res.data;
}

// Запустить код внутри image-runner, получить PNG и сравнить с эталоном.
export async function compareImageTestCode(assignmentId, language, code, debug = true) {
  const res = await api.post(`/api/assignments/${assignmentId}/image-test/compare-code`, {
    language,
    code,
    debug,
  });
  return res.data;
}
