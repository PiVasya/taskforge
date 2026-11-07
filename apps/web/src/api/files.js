import api from './http';

export async function uploadImage(file, folder = null) {
  const fd = new FormData();
  fd.append('file', file);
  if (folder) fd.append('folder', folder);
  const res = await api.post('/api/files/images', fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res?.data;
}

export async function listCourseImages(courseId, { offset = 0, limit = 100 } = {}) {
  const prefix = `editor-images/courses/${courseId}/`;
  const response = await api.get('/api/files', { params: { prefix, offset, limit } });
  return response.data;
}
