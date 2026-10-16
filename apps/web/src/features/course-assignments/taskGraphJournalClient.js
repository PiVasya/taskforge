import { importPointerKey, isImportPending, newImportOperationId } from './courseTaskGraphOperation';

// One instance belongs to one authenticated user/course. No payload is kept in
// browser storage, and a late response cannot change another instance's state.
export function createTaskGraphJournalClient({ courseId, userId, enabled = true, api,
  storage = undefined, cryptoProvider = typeof crypto === 'undefined' ? null : crypto }) {
  const key = importPointerKey(userId, courseId);
  if (storage === undefined) try { storage = typeof localStorage === 'undefined' ? null : localStorage; } catch { storage = null; }
  let generation = 0;
  let state = { operation: null, history: [], busy: false, ready: !enabled, lookupError: null };
  let inFlight = false;
  let discovery = null;
  const listeners = new Set();
  const emit = (patch) => { state = { ...state, ...patch }; listeners.forEach((fn) => fn()); };
  const remember = (operation) => {
    const history = operation?.operationId
      ? [{ operationId: operation.operationId, state: operation.state, createdAt: operation.createdAt, updatedAt: operation.updatedAt },
        ...state.history.filter((x) => x.operationId !== operation.operationId)].slice(0, 30) : state.history;
    emit({ operation, history });
    if (operation?.operationId) try { storage?.setItem(key, operation.operationId); } catch {}
  };
  const refresh = async (id = state.operation?.operationId) => {
    if (!id || !enabled) return null;
    const started = generation;
    try {
      const operation = await api.get(courseId, id);
      if (started !== generation) return operation;
      remember(operation); emit({ lookupError: null });
      return operation;
    } catch (error) {
      if (started !== generation) throw error;
      emit({ lookupError: error });
      if (error?.response?.status === 404) remember({ operationId: id, courseId, state: 'missing' });
      throw error;
    }
  };
  const discover = () => {
    if (discovery) return discovery;
    if (!enabled || !userId) return Promise.resolve();
    discovery = (async () => {
      let pointer = null;
      try { pointer = storage?.getItem(key); } catch {}
      if (pointer) remember({ operationId: pointer, courseId, state: 'unknown' });
      try {
        const history = (await api.list(courseId)) || [];
        emit({ history });
        const active = history.find(isImportPending);
        const pointerUnknown = pointer && !history.some((x) => x.operationId === pointer);
        const id = active?.operationId || (pointerUnknown ? pointer : pointer || history[0]?.operationId);
        if (id) {
          try { await refresh(id); } catch (error) { if (error?.response?.status !== 404) throw error; }
        }
        emit({ ready: true });
      } catch (error) { emit({ lookupError: error, ready: false }); }
      finally { discovery = null; }
    })();
    return discovery;
  };
  const begin = async (graph, options) => {
    if (!enabled || !userId || !courseId || !state.ready || inFlight) throw new Error('Дождитесь проверки журнала импорта.');
    if ((isImportPending(state.operation) && state.operation.state !== 'missing')
        || state.history.some((x) => isImportPending(x) && x.operationId !== state.operation?.operationId))
      throw new Error('Сначала завершите предыдущий импорт.');
    const operationId = state.operation?.state === 'missing' ? state.operation.operationId : newImportOperationId(cryptoProvider);
    generation++; inFlight = true; emit({ busy: true }); remember({ operationId, courseId, state: 'sending' });
    try {
      const response = await api.begin(courseId, graph, options, operationId);
      const operation = { operationId, courseId, state: response.operationState || 'data-committed', response };
      remember(operation);
      return operation;
    } catch (error) {
      const pendingId = error?.response?.data?.code === 'IMPORT_PREVIOUS_PENDING' ? error.response.data.operationId : null;
      const id = pendingId || operationId;
      remember({ operationId: id, courseId, state: 'unknown', error: { code: error?.response?.data?.code || 'IMPORT_UNCONFIRMED' } });
      try { await refresh(id); } catch {}
      throw error;
    } finally { inFlight = false; emit({ busy: false }); }
  };
  const resume = async () => {
    const id = state.operation?.operationId;
    if (!id || inFlight) return null;
    generation++; inFlight = true; emit({ busy: true });
    try { await api.resume(courseId, id); return await refresh(id); }
    catch (error) { try { await refresh(id); } catch {} throw error; }
    finally { inFlight = false; emit({ busy: false }); }
  };
  const stop = async () => {
    const id = state.operation?.operationId;
    if (!id || inFlight) return null;
    generation++; inFlight = true; emit({ busy: true });
    try { await api.stop(courseId, id); return await refresh(id); }
    finally { inFlight = false; emit({ busy: false }); }
  };
  return { getSnapshot: () => state, subscribe: (listener) => { listeners.add(listener); return () => listeners.delete(listener); },
    discover, refresh, begin, resume, stop, remember };
}
