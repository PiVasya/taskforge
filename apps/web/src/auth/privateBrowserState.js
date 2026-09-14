import { discardAllSolveDraftStores } from '../features/assignment-solve/solveDraftStore';

const PRIVATE_STORAGE_PREFIXES = Object.freeze([
  'solve-draft:v',
  'results:',
  'image-results:',
  'taskforge-sql:',
  'taskforge.compiler.draft.v1.',
]);

function isPrivateLearnerStorageKey(key) {
  return PRIVATE_STORAGE_PREFIXES.some(prefix => key.startsWith(prefix));
}

export function clearPrivateBrowserState() {
  discardAllSolveDraftStores();

  if (typeof window === 'undefined') return;

  try {
    const storage = window.localStorage;
    const keys = [];
    for (let index = 0; index < storage.length; index += 1) {
      const key = storage.key(index);
      if (key && isPrivateLearnerStorageKey(key)) keys.push(key);
    }
    keys.forEach(key => storage.removeItem(key));
  } catch {}
}
