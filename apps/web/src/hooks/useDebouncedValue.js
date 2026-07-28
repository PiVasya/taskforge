import { useEffect, useState } from 'react';

export default function useDebouncedValue(value, delayMs = 250) {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), Math.max(0, Number(delayMs) || 0));
    return () => clearTimeout(timer);
  }, [delayMs, value]);
  return debounced;
}
