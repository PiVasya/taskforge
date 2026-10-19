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

export default function useAdminSolutionLiveFeed() {
  const [items, setItems] = useState([]);
  const [state, setState] = useState('connecting');
  const userCache = useRef(new Map());
  const alive = useRef(true);
  const itemsRef = useRef([]);
  const pendingCursor = useRef(0);

  useEffect(() => { itemsRef.current = items; }, [items]);

  useEffect(() => {
    alive.current = true;
    const dispose = subscribeAdminSolutionEvents({
      onOpen: () => alive.current && setState('live'),
      onError: () => alive.current && setState('reconnecting'),
      onEvent: async (event) => {
        const userId = String(event?.userId || '');
        const key = `${String(event?.kind || '')}:${String(event?.itemId || '')}`;
        if (!userId || key === ':') return;

        try {
          let user = userCache.current.get(userId);
          const userPromise = user
            ? Promise.resolve(user)
            : getAdminUser(userId).then((value) => {
                userCache.current.set(userId, value);
                return value;
              });
          const [loadedUser, detail] = await Promise.all([
            userPromise,
            loadAdminSolutionLiveDetail(event),
          ]);
          if (!alive.current) return;
          const next = {
            ...event,
            key,
            user: loadedUser,
            userLabel: userLabel(loadedUser, userId),
            detail,
            assignmentTitle: assignmentTitle(detail, event.assignmentId),
            status: detail?.status || detail?.verdict || event.status || null,
            score: detail?.score ?? detail?.scorePercent ?? detail?.similarityPercent ?? event.score ?? null,
          };
          setItems((current) => {
            const existing = current.find((item) => item.key === key);
            if (existing && new Date(existing.occurredAtUtc || 0) > new Date(next.occurredAtUtc || 0)) return current;
            const merged = [mergeLiveEvent(existing, next), ...current.filter((item) => item.key !== key)];
            merged.sort((a, b) => new Date(b.occurredAtUtc || 0) - new Date(a.occurredAtUtc || 0));
            return merged.slice(0, 120);
          });
        } catch {
          if (!alive.current) return;
          const next = {
            ...event,
            key,
            userLabel: String(event.userId || '').slice(0, 8),
            assignmentTitle: `Задание ${String(event.assignmentId || '').slice(0, 8)}`,
          };
          setItems((current) => {
            const existing = current.find((item) => item.key === key);
            if (existing && new Date(existing.occurredAtUtc || 0) > new Date(next.occurredAtUtc || 0)) return current;
            return [mergeLiveEvent(existing, next), ...current.filter((item) => item.key !== key)].slice(0, 120);
          });
        }
      },
    });
    return () => {
      alive.current = false;
      dispose();
    };
  }, []);

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
