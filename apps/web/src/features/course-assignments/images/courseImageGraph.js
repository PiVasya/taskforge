// Task Graph v5 image references: edit only Tiptap image attrs, including
// serialized documents in description, promptContentJson and contentJson.
const TYPED_DOCUMENT_FIELDS = new Set(['description', 'promptContentJson', 'contentJson']);
const ASSET_PREFIX = 'asset://assets/';

function visit(value, changeSrc, sources) {
  if (Array.isArray(value)) return value.map((entry) => visit(entry, changeSrc, sources));
  if (!value || typeof value !== 'object') return value;
  const out = {};
  for (const [key, item] of Object.entries(value)) {
    if (value.type === 'image' && key === 'attrs' && item && typeof item === 'object') {
      const next = { ...item };
      if (typeof next.src === 'string' && next.src.length) {
        sources.set(next.src, (sources.get(next.src) || 0) + 1);
        next.src = changeSrc(next.src);
      }
      out[key] = next;
    } else if (TYPED_DOCUMENT_FIELDS.has(key) && typeof item === 'string' && item.trimStart().startsWith('{')) {
      let document;
      try { document = JSON.parse(item); } catch { document = null; }
      if (document?.type === 'doc') {
        const newDoc = visit(document, changeSrc, sources);
        out[key] = JSON.stringify(newDoc);
      } else out[key] = item;
    } else {
      out[key] = visit(item, changeSrc, sources);
    }
  }
  return out;
}

export function mapGraphImageSources(graph, mapper = (url) => url) {
  const sources = new Map();
  const result = visit(graph, mapper, sources);
  return { graph: result, sources };
}

export function imageSourceKey(src, origin) {
  try {
    const base = origin || (typeof window !== 'undefined' ? window.location.origin : 'https://taskforge.invalid');
    const url = new URL(src, base);
    if (url.origin !== base || url.username || url.password) return null;
    if (!url.pathname.startsWith('/api/files/')) return null;
    const key = decodeURIComponent(url.pathname.slice('/api/files/'.length));
    if (!/^(editor-images|public)\/[a-z0-9._/-]+$/i.test(key) || key.includes('..') || key.includes('//')) return null;
    return key;
  } catch { return null; }
}

export function archiveAssetName(src) {
  if (typeof src !== 'string' || !src.startsWith(ASSET_PREFIX)) return null;
  const name = src.slice(ASSET_PREFIX.length);
  if (!name || name.length > 200 || !/^[a-z0-9][a-z0-9._-]*$/i.test(name) || name.includes('..')) return null;
  return `assets/${name}`;
}

export function referencedImageSources(graph) {
  return mapGraphImageSources(graph).sources;
}

export function replaceImageAssetUrls(graph, urls) {
  const seen = new Set();
  const mapped = mapGraphImageSources(graph, (src) => {
    const path = archiveAssetName(src);
    if (!path) return src;
    const url = urls.get(path);
    if (!url) throw new Error(`Отсутствует изображение: ${path}`);
    seen.add(path);
    return url;
  });
  return { graph: mapped.graph, seen };
}

export function archiveImageUrl(src, id) {
  const key = imageSourceKey(src);
  if (!key) return null;
  const ext = key.match(/\.(png|jpe?g|webp|gif|bmp|ppm)$/i)?.[1]?.toLowerCase() || 'png';
  return `assets/${String(id).padStart(5, '0')}.${ext}`;
}

export function toArchiveReference(path) { return `asset://${path}`; }
