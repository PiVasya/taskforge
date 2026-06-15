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
