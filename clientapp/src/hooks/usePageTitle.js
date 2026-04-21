import { useEffect } from 'react';

export function usePageTitle(title) {
  useEffect(() => {
    if (!title) return;
    document.title = title;
  }, [title]);
}
