import { useSyncExternalStore } from 'react';

let currentSecond = Math.floor(Date.now() / 1000);
let timerId = null;
const listeners = new Set();
const noopSubscribe = () => () => {};

function emitCurrentSecond() {
  const nextSecond = Math.floor(Date.now() / 1000);
  if (nextSecond === currentSecond) return;
  currentSecond = nextSecond;
  listeners.forEach((listener) => listener());
}

function startClock() {
  if (timerId != null || typeof window === 'undefined') return;
  emitCurrentSecond();
  const delay = 1000 - (Date.now() % 1000) + 8;
  timerId = window.setTimeout(function tick() {
    emitCurrentSecond();
    timerId = window.setTimeout(tick, 1000);
  }, delay);
}

function stopClock() {
  if (timerId == null || typeof window === 'undefined') return;
  window.clearTimeout(timerId);
  timerId = null;
}

function subscribe(listener) {
  listeners.add(listener);
  startClock();
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) stopClock();
  };
}

function getSnapshot() {
  return currentSecond;
}

function getServerSnapshot() {
  return 0;
}

export default function useSecondClock(enabled = true) {
  const second = useSyncExternalStore(
    enabled ? subscribe : noopSubscribe,
    enabled ? getSnapshot : getServerSnapshot,
    getServerSnapshot,
  );
  return second * 1000;
}
