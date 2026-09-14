import { useEffect, useRef, useState } from 'react';
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
            const merged = [next, ...current.filter((item) => item.key !== key)];
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
            return [next, ...current.filter((item) => item.key !== key)].slice(0, 120);
          });
        }
      },
    });
    return () => {
      alive.current = false;
      dispose();
    };
  }, []);

  return { items, state };
}
