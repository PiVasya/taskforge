// Pure protocol: lost HTTP responses never allocate another operation ID.
export const activeTransfer = (job) => ['queued', 'running'].includes(job?.state);
export const retryableTransport = (error) => !error?.response || [408, 429, 502, 503, 504].includes(error.response.status);
export function transferFailure(job) {
  let data;
  try { data = JSON.parse(job.errorJson || '{}'); } catch { data = {}; }
  const error = new Error(data.message || data.code || 'TRANSFER_FAILED');
  error.code = data.code || 'TRANSFER_FAILED';
  error.transferId = job.transferId;
  error.response = { status: job.resultStatus || 409, data: { ...data, transferId: job.transferId } };
  return error;
}
export function createGraphTransferRunner({ api, idFactory, delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms)), notify = () => {} }) {
  const checkAbort = (signal) => { if (signal?.aborted) { const error = new Error('TRANSFER_OBSERVATION_STOPPED'); error.name = 'AbortError'; throw error; } };
  const observe = async (courseId, first, { signal, resultType = 'json' } = {}) => {
    let job = first, failures = 0;
    while (true) {
      checkAbort(signal);
      notify(job);
      if (!activeTransfer(job)) {
        if (job.state !== 'succeeded') throw transferFailure(job);
        for (let attempt = 0; ; attempt += 1) {
          try { return await api.result(courseId, job.transferId, resultType, signal); }
          catch (error) {
            checkAbort(signal);
            if (attempt >= 2 || !retryableTransport(error)) { error.transferId = job.transferId; throw error; }
            await delay(1000 * (attempt + 1));
          }
        }
      }
      await delay(Math.min(1000 * (2 ** failures), 15000));
      checkAbort(signal);
      try { job = await api.get(courseId, job.transferId, signal); failures = 0; }
      catch (error) {
        checkAbort(signal);
        if (!retryableTransport(error) || ++failures >= 8) {
          error.transferId = job.transferId;
          error.code = 'TRANSFER_STATUS_UNKNOWN';
          throw error; // Not a rollback: the server job continues.
        }
      }
    }
  };
  const run = async (courseId, kind, payload, { operationId, signal, resultType = 'json', onUploadProgress } = {}) => {
    const transferId = idFactory();
    const request = { transferId, kind, payload, ...(operationId ? { operationId } : {}) };
    let job;
    for (let attempt = 0; !job; attempt += 1) {
      checkAbort(signal);
      try { job = await api.start(courseId, request, signal, onUploadProgress); }
      catch (error) {
        checkAbort(signal);
        if (!retryableTransport(error)) { error.transferId = transferId; throw error; }
        // The POST may have committed even when the gateway lost its response.
        try { job = await api.get(courseId, transferId, signal); }
        catch (lookup) {
          checkAbort(signal);
          if (lookup?.response?.status !== 404 && !retryableTransport(lookup)) throw lookup;
          if (attempt >= 3) { error.transferId = transferId; error.code = 'TRANSFER_STATUS_UNKNOWN'; throw error; }
          await delay(1000 * (attempt + 1));
        }
      }
    }
    return observe(courseId, job, { signal, resultType });
  };
  return { run, observe };
}
