import api from './http';

export async function getSystemStatus({ signal } = {}) {
  const { data } = await api.get('/api/admin/cluster', { signal });
  return data;
}

export async function switchClusterPrimary(target) {
  const { data } = await api.post('/api/admin/cluster/primary', { target });
  return data;
}

export async function startClusterDiagnostics(options) {
  const { data } = await api.post('/api/admin/cluster/diagnostics', options);
  return data;
}

export async function getClusterDiagnosticsJob(node, jobId, { signal } = {}) {
  const { data } = await api.get(`/api/admin/cluster/diagnostics/${encodeURIComponent(node)}/${encodeURIComponent(jobId)}`, { signal });
  return data;
}

export async function downloadClusterDiagnostics(node, jobId) {
  const response = await api.get(`/api/admin/cluster/diagnostics/${encodeURIComponent(node)}/${encodeURIComponent(jobId)}/download`, {
    responseType: 'blob',
    timeout: 30 * 60 * 1000,
  });
  const disposition = response.headers?.['content-disposition'] || '';
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  const quoted = /filename="([^"]+)"/i.exec(disposition)?.[1];
  let fileName = quoted || `taskforge_diagnostics_${node}_${jobId}.tar.gz`;
  if (encoded) {
    try { fileName = decodeURIComponent(encoded); } catch { fileName = encoded; }
  }
  return { blob: response.data, fileName };
}
