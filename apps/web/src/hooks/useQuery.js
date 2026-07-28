import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import { useQueryClient } from '../data/QueryClientProvider';
import { hashQueryKey, IDLE_SNAPSHOT } from '../data/queryClient';

const noopSubscribe = () => () => {};
const getIdleSnapshot = () => IDLE_SNAPSHOT;

export default function useQuery({
  queryKey,
  queryFn,
  enabled = true,
  staleTime,
  gcTime,
  keepPreviousData = false,
  refetchOnWindowFocus = false,
  select,
  retry = 1,
  retryDelay = 500,
} = {}) {
  const client = useQueryClient();
  const keyHash = hashQueryKey(queryKey);
  const normalizedKey = useMemo(
    () => (Array.isArray(queryKey) ? queryKey : [queryKey]),
    // Query keys are value-based. Object identity must not restart observers.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [keyHash],
  );
  const queryFnRef = useRef(queryFn);
  const selectRef = useRef(select);
  const previousDataRef = useRef();
  const previousClientRef = useRef(client);
  if (previousClientRef.current !== client) {
    previousClientRef.current = client;
    previousDataRef.current = undefined;
  }
  queryFnRef.current = queryFn;
  selectRef.current = select;

  const execute = useCallback(
    (context) => {
      if (typeof queryFnRef.current !== 'function') return Promise.resolve(undefined);
      return queryFnRef.current(context);
    },
    [],
  );

  const subscribe = useCallback(
    (listener) => (enabled ? client.subscribe(normalizedKey, listener, gcTime) : noopSubscribe(listener)),
    [client, enabled, gcTime, normalizedKey],
  );
  const getSnapshot = useCallback(
    () => (enabled ? client.getQueryState(normalizedKey) : IDLE_SNAPSHOT),
    [client, enabled, normalizedKey],
  );

  const snapshot = useSyncExternalStore(subscribe, getSnapshot, getIdleSnapshot);

  useEffect(() => {
    if (!enabled || typeof queryFnRef.current !== 'function') return undefined;
    client.fetchQuery({ queryKey: normalizedKey, queryFn: execute, staleTime, retry, retryDelay }).catch(() => {});
    return undefined;
  }, [client, enabled, execute, normalizedKey, retry, retryDelay, staleTime]);

  useEffect(() => {
    if (!enabled || !refetchOnWindowFocus || typeof queryFnRef.current !== 'function') return undefined;
    const refresh = () => {
      if (document.visibilityState !== 'hidden') {
        client.fetchQuery({ queryKey: normalizedKey, queryFn: execute, staleTime, retry, retryDelay }).catch(() => {});
      }
    };
    window.addEventListener('focus', refresh);
    window.addEventListener('online', refresh);
    document.addEventListener('visibilitychange', refresh);
    return () => {
      window.removeEventListener('focus', refresh);
      window.removeEventListener('online', refresh);
      document.removeEventListener('visibilitychange', refresh);
    };
  }, [client, enabled, execute, normalizedKey, refetchOnWindowFocus, retry, retryDelay, staleTime]);

  if (snapshot.data !== undefined) previousDataRef.current = snapshot.data;
  const rawData = snapshot.data === undefined && keepPreviousData ? previousDataRef.current : snapshot.data;
  const data = selectRef.current && rawData !== undefined ? selectRef.current(rawData) : rawData;

  const refetch = useCallback(() => {
    if (!enabled || typeof queryFnRef.current !== 'function') return Promise.resolve(undefined);
    return client.fetchQuery({ queryKey: normalizedKey, queryFn: execute, staleTime: 0, retry, retryDelay, force: true });
  }, [client, enabled, execute, normalizedKey, retry, retryDelay]);

  return {
    data,
    error: snapshot.error,
    status: snapshot.status,
    fetchStatus: snapshot.fetchStatus,
    isPending: snapshot.status === 'idle' || snapshot.status === 'pending',
    isLoading: snapshot.status === 'pending' && rawData === undefined,
    isFetching: snapshot.fetchStatus === 'fetching',
    isError: snapshot.status === 'error',
    isSuccess: snapshot.status === 'success',
    isPlaceholderData: snapshot.data === undefined && rawData !== undefined,
    updatedAt: snapshot.updatedAt,
    refetch,
  };
}
