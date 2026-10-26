import api from './http';
import { createGraphTransferRunner } from './graphTransferProtocol';
import { newImportOperationId } from '../features/course-assignments/courseTaskGraphOperation';

const listeners = new Set();
export const subscribeGraphTransfers = (listener) => { listeners.add(listener); return () => listeners.delete(listener); };
const publish = (status) => { for (const listener of listeners) listener(status); };
const base = (courseId) => `/api/courses/${courseId}/assignments/transfers`;
const transport = {
  start: async (courseId, request, signal, onUploadProgress) => (await api.post(base(courseId), request, { signal, onUploadProgress, timeout: 45000 })).data,
  get: async (courseId, id, signal) => (await api.get(`${base(courseId)}/${id}`, { signal, timeout: 15000 })).data,
  result: async (courseId, id, type = 'json', signal) => (await api.get(`${base(courseId)}/${id}/result`, { signal, responseType: type })).data,
};
const runner = createGraphTransferRunner({ api: transport, idFactory: newImportOperationId, notify: publish });
export const runGraphTransfer = runner.run;
export const observeGraphTransfer = runner.observe;
export const listGraphTransfers = async (courseId, signal) => (await api.get(base(courseId), { signal, timeout: 15000 })).data;
export const getGraphTransfer = transport.get;
export const downloadGraphTransfer = (courseId, id) => transport.result(courseId, id, 'blob');
export const cancelGraphTransfer = (courseId, id) => api.post(`${base(courseId)}/${id}/cancel`);
export const retryGraphTransfer = (courseId, id) => api.post(`${base(courseId)}/${id}/retry`);
export function saveGraphTransferBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url; link.download = name; document.body.appendChild(link); link.click(); link.remove();
  // Give browsers time to acquire the Blob before releasing its object URL.
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}
