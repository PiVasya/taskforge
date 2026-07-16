export async function getUpdatesIndex() {
  const res = await fetch('/updates/index.json', { cache: 'no-store' });
  if (!res.ok) throw new Error(`updates index: ${res.status}`);
  const data = await res.json();
  if (!Array.isArray(data)) return [];

  return [...data].sort((a, b) => Number(b?.order || 0) - Number(a?.order || 0));
}

export async function getUpdatePost(postFile) {
  const res = await fetch(`/updates/${postFile}`, { cache: 'no-store' });
  if (!res.ok) throw new Error(`updates post: ${res.status}`);
  return res.json();
}
