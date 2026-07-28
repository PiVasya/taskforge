import { useCallback, useSyncExternalStore } from 'react';

const stores = new Map();
const EMPTY_ANSWER = Object.freeze({});
const EMPTY_ANSWERS = Object.freeze({});

function shallowEqualAnswer(left, right) {
  if (left === right) return true;
  const leftKeys = Object.keys(left || {});
  const rightKeys = Object.keys(right || {});
  if (leftKeys.length !== rightKeys.length) return false;
  return leftKeys.every((key) => Object.is(left?.[key], right?.[key]));
}

function createStore(key) {
  let answers = EMPTY_ANSWERS;
  const itemListeners = new Map();
  const allListeners = new Set();

  const emitItem = (itemId) => {
    itemListeners.get(String(itemId))?.forEach((listener) => listener());
    allListeners.forEach((listener) => listener());
  };

  return {
    key,
    getAnswers: () => answers,
    getAnswer: (itemId) => answers[String(itemId)] || EMPTY_ANSWER,
    subscribeItem(itemId, listener) {
      const normalizedId = String(itemId);
      let listeners = itemListeners.get(normalizedId);
      if (!listeners) {
        listeners = new Set();
        itemListeners.set(normalizedId, listeners);
      }
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) itemListeners.delete(normalizedId);
      };
    },
    subscribeAll(listener) {
      allListeners.add(listener);
      return () => allListeners.delete(listener);
    },
    setAnswer(itemId, updater) {
      const normalizedId = String(itemId);
      const previous = answers[normalizedId] || EMPTY_ANSWER;
      const next = typeof updater === 'function' ? updater(previous) : updater;
      const normalized = next && typeof next === 'object' ? Object.freeze({ ...next }) : EMPTY_ANSWER;
      if (shallowEqualAnswer(previous, normalized)) return previous;
      answers = Object.freeze({ ...answers, [normalizedId]: normalized });
      emitItem(normalizedId);
      return normalized;
    },
    reset(nextAnswers = {}) {
      const previousIds = new Set(Object.keys(answers));
      const normalized = {};
      Object.entries(nextAnswers || {}).forEach(([itemId, value]) => {
        normalized[String(itemId)] = Object.freeze({ ...(value || {}) });
        previousIds.add(String(itemId));
      });
      answers = Object.freeze(normalized);
      previousIds.forEach(emitItem);
    },
    destroy() {
      itemListeners.clear();
      allListeners.clear();
      answers = EMPTY_ANSWERS;
    },
  };
}

export function getAttemptAnswerStore(storeKey) {
  const key = String(storeKey || '');
  if (!key) return null;
  let store = stores.get(key);
  if (!store) {
    store = createStore(key);
    stores.set(key, store);
  }
  return store;
}

export function resetAttemptAnswers(storeKey, answers = {}) {
  getAttemptAnswerStore(storeKey)?.reset(answers);
}

export function getAttemptAnswers(storeKey) {
  return getAttemptAnswerStore(storeKey)?.getAnswers() || EMPTY_ANSWERS;
}

export function subscribeAttemptAnswers(storeKey, listener) {
  return getAttemptAnswerStore(storeKey)?.subscribeAll(listener) || (() => {});
}

export function useAttemptAnswer(storeKey, itemId) {
  const store = getAttemptAnswerStore(storeKey);
  const subscribe = useCallback(
    (listener) => store?.subscribeItem(itemId, listener) || (() => {}),
    [itemId, store],
  );
  const getSnapshot = useCallback(
    () => store?.getAnswer(itemId) || EMPTY_ANSWER,
    [itemId, store],
  );
  return useSyncExternalStore(subscribe, getSnapshot, () => EMPTY_ANSWER);
}

export function setAttemptAnswer(storeKey, itemId, updater) {
  return getAttemptAnswerStore(storeKey)?.setAnswer(itemId, updater);
}

export function destroyAttemptAnswers(storeKey) {
  const key = String(storeKey || '');
  if (!key) return;
  const store = stores.get(key);
  store?.destroy();
  stores.delete(key);
}
