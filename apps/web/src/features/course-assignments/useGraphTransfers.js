import { useCallback, useEffect, useRef, useState } from 'react';
import { listGraphTransfers, subscribeGraphTransfers, cancelGraphTransfer, retryGraphTransfer,
  downloadGraphTransfer, saveGraphTransferBlob } from '../../api/graphTransfers';
import { mergeTransferStatuses, isTransferActive } from './graphTransferModel';

export default function useGraphTransfers({ courseId, userId, enabled, onError, journal }) {
  const [rows, setRows] = useState([]);
  const activeRows = useRef(false);
  activeRows.current = rows.some((row) => String(row.courseId) === String(courseId) && String(row.userId) === String(userId) && isTransferActive(row));
  const [lookupError, setLookupError] = useState(false);
  const [actionId, setActionId] = useState(null);
  const refreshedTransfers = useRef(new Set());
  useEffect(() => { refreshedTransfers.current.clear(); }, [courseId, userId]);
  useEffect(() => {
    if (!journal || !enabled) return;
    for (const job of rows) {
      if (String(job.courseId) !== String(courseId) || String(job.userId) !== String(userId)) continue;
      const key = `${job.transferId}:${job.attemptCount}:${job.state}`;
      if (!job.operationId || isTransferActive(job) || job.state === 'expired' || refreshedTransfers.current.has(key)) continue;
      refreshedTransfers.current.add(key);
      // Refresh only the selected operation, never replace it with old history.
      if (journal.operation?.operationId === job.operationId) void journal.refresh(job.operationId).catch(() => {});
      else if (!journal.operation) void journal.discover();
    }
  }, [rows, journal, courseId, userId, enabled]);
  const version = useRef(0);
  const errorHandler = useRef(onError); errorHandler.current = onError;
  const refresh = useCallback(async (signal) => {
    const captured = version.current;
    if (!enabled) return;
    try {
      const result = await listGraphTransfers(courseId, signal);
      if (captured !== version.current || signal?.aborted) return;
      setRows((old) => mergeTransferStatuses(old, result || [], courseId, userId));
      setLookupError(false);
    } catch (error) { if (captured === version.current && !signal?.aborted) setLookupError(true); }
  }, [courseId, userId, enabled]);
  useEffect(() => {
    version.current += 1; setRows([]); setLookupError(false); setActionId(null);
    if (!enabled) return undefined;
    const controller = new AbortController();
    const unsubscribe = subscribeGraphTransfers((row) => {
      if (!controller.signal.aborted) setRows((old) => mergeTransferStatuses(old, [row], courseId, userId));
    });
    let timer;
    const poll = async () => {
      await refresh(controller.signal);
      if (!controller.signal.aborted) timer = setTimeout(poll, document.hidden ? 15000 : activeRows.current ? 2000 : 15000);
    };
    void poll();
    const focus = () => { void refresh(controller.signal); };
    window.addEventListener('focus', focus);
    return () => { version.current += 1; controller.abort(); clearTimeout(timer); unsubscribe(); window.removeEventListener('focus', focus); };
  }, [courseId, userId, enabled, refresh]);
  const act = async (row, action) => {
    const captured = version.current;
    setActionId(row.transferId);
    try {
      if (action === 'cancel') await cancelGraphTransfer(courseId, row.transferId);
      if (action === 'retry') await retryGraphTransfer(courseId, row.transferId);
      if (action === 'download') {
        const blob = await downloadGraphTransfer(courseId, row.transferId);
        if (captured === version.current) saveGraphTransferBlob(blob, `taskforge-${courseId}-task-graph.json`);
      }
      if (captured === version.current) await refresh();
    } catch (error) { if (captured === version.current) errorHandler.current?.(error); }
    finally { if (captured === version.current) setActionId(null); }
  };
  return { rows: enabled ? rows.filter((row) => String(row.courseId) === String(courseId) && String(row.userId) === String(userId)) : [], lookupError, actionId, refresh, act };
}
