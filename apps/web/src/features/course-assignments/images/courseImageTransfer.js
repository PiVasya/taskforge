import { uploadImage, listCourseImages } from '../../../api/files';
import { archiveAssetName, archiveImageUrl, imageSourceKey, mapGraphImageSources,
  referencedImageSources, replaceImageAssetUrls, toArchiveReference } from './courseImageGraph';
import { readImageZip, writeImageZip } from './zipStore';

const TYPES = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', bmp: 'image/bmp', ppm: 'image/x-portable-pixmap' };
export const MAX_PACKAGE_IMAGES = 500;
export const MAX_UPLOAD_IMAGE_BYTES = 10 * 1024 * 1024;

function imageFile(path, bytes) {
  const ext = path.split('.').pop()?.toLowerCase();
  if (!TYPES[ext]) throw new Error(`Недопустимый формат изображения: ${path}`);
  if (bytes.length === 0 || bytes.length > MAX_UPLOAD_IMAGE_BYTES) throw new Error(`Неверный размер изображения: ${path}`);
  return new File([bytes], path.split('/').pop(), { type: TYPES[ext] });
}

async function concurrent(items, limit, action) {
  let cursor = 0;
  const results = Array(items.length);
  await Promise.all(Array.from({ length: Math.min(items.length, limit) }, async () => {
    while (cursor < items.length) {
      const idx = cursor++;
      results[idx] = await action(items[idx], idx);
    }
  }));
  return results;
}

export async function importGraphImagePackage(file, courseId, onProgress = () => {}) {
  const entries = await readImageZip(file);
  const graphBytes = entries.get('graph.json');
  if (!graphBytes || graphBytes.length > 24 * 1024 * 1024) throw new Error('В ZIP должен быть graph.json размером не более 24 МиБ.');
  let graph;
  try { graph = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(graphBytes)); }
  catch { throw new Error('graph.json не является корректным JSON.'); }
  if (graph?.format !== 'taskforge-task-graph' || !Array.isArray(graph.tasks))
    throw new Error('graph.json должен содержать Task Graph JSON.');
  const files = [];
  for (const [path, bytes] of entries) {
    if (path === 'graph.json') continue;
    if (!/^assets\/[a-z0-9][a-z0-9._-]*\.(png|jpe?g|gif|webp|bmp|ppm)$/i.test(path)) throw new Error(`Недопустимый файл в архиве: ${path}`);
    files.push({ path, file: imageFile(path, bytes) });
  }
  if (files.length > MAX_PACKAGE_IMAGES) throw new Error('В архиве слишком много изображений.');
  const referenced = referencedImageSources(graph);
  for (const src of referenced.keys()) {
    if (src.startsWith('asset://') && (!archiveAssetName(src) || !entries.has(archiveAssetName(src))))
      throw new Error(`Файл изображения отсутствует в архиве: ${src}`);
  }
  // The existing Files API validates file signatures, size and MIME. Uploads are
  // deliberately separate from task mutations; a canceled task import still
  // leaves these uploaded files available in the course gallery.
  const uploaded = new Map();
  let count = 0;
  await concurrent(files, 3, async ({ path, file: image }) => {
    const response = await uploadImage(image, `editor-images/courses/${courseId}`);
    if (!imageSourceKey(response?.url)) throw new Error(`Files API не вернул публичный адрес для ${path}`);
    uploaded.set(path, response.url);
    onProgress(++count, files.length);
  });
  const resolved = replaceImageAssetUrls(graph, uploaded);
  return { text: JSON.stringify(resolved.graph), images: files.length };
}

export async function exportGraphImagePackage(jsonBlob, courseId, { includeGallery = false, onProgress = () => {} } = {}) {
  let graph;
  try { graph = JSON.parse(await jsonBlob.text()); }
  catch { throw new Error('Экспорт сервера не является корректным JSON.'); }
  const sources = referencedImageSources(graph);
  if (includeGallery) {
    let offset = 0;
    for (;;) {
      const rows = await listCourseImages(courseId, { offset, limit: 100 });
      if (!Array.isArray(rows)) throw new Error('Не удалось прочитать галерею курса.');
      for (const row of rows) if (imageSourceKey(row.url)) sources.set(row.url, (sources.get(row.url) || 0) + 1);
      if (rows.length < 100) break;
      offset += rows.length;
      if (offset > MAX_PACKAGE_IMAGES) throw new Error('Галерея слишком большая для одного ZIP.');
    }
  }
  const imagePaths = new Map(), imageUrls = [], pathByKey = new Map();
  let externalImages = 0;
  for (const src of sources.keys()) {
    const key = imageSourceKey(src);
    if (!key) { externalImages++; continue; }
    if (pathByKey.has(key)) { imagePaths.set(src, pathByKey.get(key)); continue; }
    if (imageUrls.length >= MAX_PACKAGE_IMAGES) throw new Error('Для одного ZIP допускается не более 500 изображений.');
    const path = archiveImageUrl(src, imageUrls.length + 1);
    if (!path) continue;
    imagePaths.set(src, path);
    pathByKey.set(key, path);
    imageUrls.push(src);
  }
  let packedGraph = mapGraphImageSources(graph, (src) => imagePaths.has(src) ? toArchiveReference(imagePaths.get(src)) : src).graph;
  const assets = await concurrent(imageUrls, 3, async (src, idx) => {
    const url = new URL(src, window.location.origin);
    const response = await fetch(url.href, { credentials: 'same-origin' });
    if (!response.ok || !response.headers.get('content-type')?.startsWith('image/'))
      throw new Error(`Не удалось получить картинку: ${imagePaths.get(src)}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (!bytes.length || bytes.length > MAX_UPLOAD_IMAGE_BYTES) throw new Error(`Недопустимый размер изображения: ${imagePaths.get(src)}`);
    onProgress(idx + 1, imageUrls.length);
    return { path: imagePaths.get(src), bytes };
  });
  const bytes = new TextEncoder().encode(JSON.stringify(packedGraph));
  return { blob: await writeImageZip([{ path: 'graph.json', bytes }, ...assets]), imageCount: assets.length, externalImages };
}
