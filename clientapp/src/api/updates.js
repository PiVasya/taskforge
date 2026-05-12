



export async function getUpdatesIndex() {
  const res = await fetch('/updates/index.json', { cache: 'no-store' });
  if (!res.ok) throw new Error(`updates index: ${res.status}`);
  const data = await res.json();
  if (!Array.isArray(data)) return [];

  
  const toTime = (d) => {
    const t = Date.parse(d);
    return Number.isFinite(t) ? t : 0;
  };

  return [...data].sort((a, b) => {
    const ap = a?.pinned ? 1 : 0;
    const bp = b?.pinned ? 1 : 0;
    if (ap !== bp) return bp - ap;
    return toTime(b?.date) - toTime(a?.date);
  });
}

export async function getUpdatePost(postFile) {
  const res = await fetch(`/updates/${postFile}`, { cache: 'no-store' });
  if (!res.ok) throw new Error(`updates post: ${res.status}`);
  
  const data = await res.json();
  return data;
}
