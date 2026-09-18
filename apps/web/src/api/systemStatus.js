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

function downloadFileName(response, fallback) {
  const disposition = response.headers?.['content-disposition'] || '';
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  const quoted = /filename="([^"]+)"/i.exec(disposition)?.[1];
  let fileName = quoted || fallback;
  if (encoded) {
    try { fileName = decodeURIComponent(encoded); } catch { fileName = encoded; }
  }
  return fileName;
}

async function downloadArchive(url, fallbackFileName, { signal, onProgress } = {}) {
  const response = await api.get(url, {
    responseType: 'blob',
    timeout: 30 * 60 * 1000,
    signal,
    onDownloadProgress: event => {
      if (typeof onProgress !== 'function') return;
      const loaded = Math.max(0, Number(event?.loaded) || 0);
      const rawTotal = Number(event?.total);
      const total = Number.isFinite(rawTotal) && rawTotal > 0 ? rawTotal : null;
      const rawProgress = Number(event?.progress);
      const percent = total
        ? Math.max(0, Math.min(100, Math.round(loaded / total * 100)))
        : Number.isFinite(rawProgress)
          ? Math.max(0, Math.min(100, Math.round(rawProgress * 100)))
          : null;
      const rawRate = Number(event?.rate);
      onProgress({ loaded, total, percent, rate: Number.isFinite(rawRate) && rawRate > 0 ? rawRate : null });
    },
  });
  return { blob: response.data, fileName: downloadFileName(response, fallbackFileName) };
}

export async function downloadClusterDiagnostics(node, jobId, options = {}) {
  return downloadArchive(
    `/api/admin/cluster/diagnostics/${encodeURIComponent(node)}/${encodeURIComponent(jobId)}/download`,
    `taskforge_diagnostics_${node}_${jobId}.tar.gz`,
    options,
  );
}

export async function getClusterDiagnosticsArchives({ signal } = {}) {
  const { data } = await api.get('/api/admin/cluster/diagnostics/archives', { signal });
  return data;
}

export async function downloadStoredClusterDiagnostics(archiveId, options = {}) {
  return downloadArchive(
    `/api/admin/cluster/diagnostics/archives/${encodeURIComponent(archiveId)}/download`,
    'taskforge_diagnostics.tar.gz',
    options,
  );
}
