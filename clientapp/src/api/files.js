import api from './http';

export async function uploadImage(file) {
  const fd = new FormData();
  fd.append('file', file);
  const res = await api.post('/api/files/images', fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res?.data;
}
