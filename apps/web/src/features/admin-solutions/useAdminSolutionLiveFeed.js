import { useEffect, useRef, useState } from 'react';
import { isPendingLiveStatus, mergeLiveEvent, reconcileLiveDetail } from './adminSolutionLiveModel';
import {
  getAdminUser,
  loadAdminSolutionLiveDetail,
  subscribeAdminSolutionEvents,
} from '../../api/adminSolutionLive';

function userLabel(user, userId) {
  const full = [user?.firstName, user?.lastName].filter(Boolean).join(' ').trim();
  return full || user?.displayName || user?.login || user?.email || String(userId || '').slice(0, 8);
}

function assignmentTitle(detail, assignmentId) {
  return detail?.assignmentTitle || detail?.title || detail?.taskTitle || `Задание ${String(assignmentId || '').slice(0, 8)}`;
}

export default function useAdminSolutionLiveFeed(assignmentId = null) {
  const [items, setItems] = useState([]);
  const [state, setState] = useState('connecting');
  const userCache = useRef(new Map());
  const alive = useRef(true);
  const itemsRef = useRef([]);
  const pendingCursor = useRef(0);

  useEffect(() => { itemsRef.current = items; }, [items]);

  useEffect(() => {
    const cachedUsers = userCache.current;
    let subscribed = true;
    alive.current = true;
    itemsRef.current = [];
    setItems([]);
    setState('connecting');
    // Show the persisted submission immediately when SSE arrives. User names
    // and detailed verdicts are enrichment, not a prerequisite for visibility.
    const upsert = (next) => setItems((current) => {
      const existing = current.find((item) => item.key === next.key);
      if (existing && new Date(existing.occurredAtUtc || 0) > new Date(next.occurredAtUtc || 0)) return current;
      const merged = [mergeLiveEvent(existing, next), ...current.filter((item) => item.key !== next.key)];
      merged.sort((a, b) => new Date(b.occurredAtUtc || 0) - new Date(a.occurredAtUtc || 0));
      return merged.slice(0, 120);
    });

    // Replaying 100 events from each DB must not issue 200 concurrent requests.
    const enrichQueue = [];
    let inFlight = 0;
    // The completion callback is outside the while loop: every task shares
    // the bounded scheduler rather than closing over a mutable loop scope.
    function runEnrichment(task) {
      inFlight += 1;
      Promise.resolve().then(task).catch(() => {}).finally(() => {
        inFlight -= 1;
        drain();
      });
    }
    function drain() {
      while (subscribed && inFlight < 6 && enrichQueue.length) {
        runEnrichment(enrichQueue.shift());
      }
    }
    const dispose = subscribeAdminSolutionEvents({
      onOpen: () => subscribed && setState('live'),
      onError: () => subscribed && setState('reconnecting'),
      onEvent: (event) => {
        if (!subscribed) return;
        if (assignmentId && String(event?.assignmentId || '').toLowerCase() !== String(assignmentId).toLowerCase()) return;
        const userId = String(event?.userId || '');
        const key = `${String(event?.kind || '').toLowerCase()}:${String(event?.itemId || '')}`;
        if (!userId || key === ':') return;

        upsert({
          ...event, key,
          userLabel: String(userId).slice(0, 8),
          assignmentTitle: assignmentTitle(null, event.assignmentId),
        });

        // A later notification for the same solution supersedes older status
        // updates. Store the user Promise early to coalesce duplicate requests.
        enrichQueue.unshift(async () => {
          if (!subscribed) return;
          try {
            let userRequest = cachedUsers.get(userId);
            if (!userRequest) {
              userRequest = getAdminUser(userId).then((loaded) => {
                cachedUsers.set(userId, loaded);
                return loaded;
              }).catch((error) => {
                cachedUsers.delete(userId);
                throw error;
              });
              cachedUsers.set(userId, userRequest);
            }
            const [loadedUser, detail] = await Promise.all([
              Promise.resolve(userRequest),
              loadAdminSolutionLiveDetail(event),
            ]);
            if (!subscribed) return;
            upsert({
              ...event, key,
              user: loadedUser,
              userLabel: userLabel(loadedUser, userId),
              detail,
              assignmentTitle: assignmentTitle(detail, event.assignmentId),
              status: detail?.status || detail?.verdict || event.status || null,
              score: detail?.score ?? detail?.scorePercent ?? detail?.similarityPercent ?? event.score ?? null,
            });
          } catch {
            // The durable SSE event remains visible even when enrichment fails.
          }
        });
        drain();
      },
    });
    return () => {
      subscribed = false;
      alive.current = false;
      enrichQueue.length = 0;
      dispose();
      cachedUsers.clear();
    };
  }, [assignmentId]);

  // SSE is the primary source. Reconcile a bounded number of pending cards
  // against stored verdicts when a terminal event was missed during reconnect.
  useEffect(() => {
    let refreshing = false;
    const refreshPending = async () => {
      if (!alive.current || refreshing) return;
      const pending = itemsRef.current.filter((item) =>
        (item.kind === 'code' || item.kind === 'sql') && isPendingLiveStatus(item.status));
      if (!pending.length) return;

      const start = pendingCursor.current % pending.length;
      const batch = Array.from({ length: Math.min(10, pending.length) }, (_, index) =>
        pending[(start + index) % pending.length]);
      pendingCursor.current = (start + batch.length) % pending.length;
      refreshing = true;
      try {
        const results = await Promise.allSettled(batch.map(async (item) => ({
          key: item.key,
          detail: await loadAdminSolutionLiveDetail(item),
        })));
        if (!alive.current) return;
        const refreshed = new Map(results
          .filter((result) => result.status === 'fulfilled' && result.value.detail)
          .map((result) => [result.value.key, result.value.detail]));
        if (refreshed.size) {
          setItems((current) => current.map((item) =>
            refreshed.has(item.key) ? reconcileLiveDetail(item, refreshed.get(item.key)) : item));
        }
      } finally {
        refreshing = false;
      }
    };
    const interval = setInterval(refreshPending, 12000);
    return () => clearInterval(interval);
  }, []);

  return { items, state };
}
