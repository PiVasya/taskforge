import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Copy, ImagePlus, RefreshCcw } from 'lucide-react';
import { Button } from '../../../components/ui';
import { uploadImage, listCourseImages } from '../../../api/files';
import { exportAssignmentsToJson } from '../../../api/assignments';
import { imageSourceKey, referencedImageSources } from '../images/courseImageGraph';

const MAX_IMAGES_PER_UPLOAD = 50;
const GALLERY_EXPORT_OPTIONS = {
  includeIds: true, includeContent: true, includeChecks: true,
  includeVisibility: false, includeConnections: false, includeConnectionAccess: false,
  includeLayout: false, includeGuide: false,
};

function mergeImages(files, graph) {
  const byKey = new Map();
  for (const file of files) {
    const key = imageSourceKey(file?.url);
    if (!key) continue;
    byKey.set(key, { key, url: file.url, fileName: file.fileName || key.split('/').pop(), size: file.size, count: 0 });
  }
  for (const [src, count] of referencedImageSources(graph || {}).entries()) {
    const key = imageSourceKey(src);
    if (!key) continue;
    const existing = byKey.get(key);
    if (existing) existing.count += count;
    else byKey.set(key, { key, url: src, fileName: key.split('/').pop(), count });
  }
  return Array.from(byKey.values()).sort((a, b) => b.count - a.count || a.fileName.localeCompare(b.fileName));
}

export default function CourseImageGallery({ courseId, open, refreshToken, notify, disabled = false }) {
  const [images, setImages] = useState([]);
  const [loading, setLoading] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [uploadCount, setUploadCount] = useState(0);
  const [error, setError] = useState('');
  const [refreshIndex, setRefreshIndex] = useState(0);
  const [showMore, setShowMore] = useState(false);
  const folder = useMemo(() => `editor-images/courses/${courseId}`, [courseId]);
  const refresh = useCallback(() => setRefreshIndex((value) => value + 1), []);

  useEffect(() => {
    if (!open || !courseId) return undefined;
    let alive = true;
    setLoading(true);
    setError('');
    (async () => {
      try {
        // The folder listing and the graph snapshot are independent: a broken
        // assignment must not hide files already uploaded into the course.
        const [graphResult, firstPageResult] = await Promise.allSettled([
          exportAssignmentsToJson(courseId, GALLERY_EXPORT_OPTIONS),
          listCourseImages(courseId, { offset: 0, limit: 200 }),
        ]);
        if (graphResult.status === 'rejected' && firstPageResult.status === 'rejected')
          throw new Error('Не удалось прочитать изображения курса.');
        const graph = graphResult.status === 'fulfilled' ? graphResult.value : null;
        const firstPage = firstPageResult.status === 'fulfilled' && Array.isArray(firstPageResult.value)
          ? firstPageResult.value : [];
        const pages = [...firstPage];
        if (firstPageResult.status === 'fulfilled') {
          while (pages.length < 2000 && pages.length % 200 === 0) {
            const more = await listCourseImages(courseId, { offset: pages.length, limit: 200 });
            if (!Array.isArray(more)) break;
            pages.push(...more);
            if (more.length < 200) break;
          }
        }
        if (alive) {
          setImages(mergeImages(pages, graph));
          if (graphResult.status === 'rejected' || firstPageResult.status === 'rejected')
            setError('Некоторые изображения могут быть недоступны.');
        }
      } catch (e) {
        if (alive) setError(e?.message || 'Не удалось прочитать изображения курса.');
      } finally { if (alive) setLoading(false); }
    })();
    return () => { alive = false; };
  }, [open, courseId, refreshIndex, refreshToken]);

  const upload = async (files) => {
    const selected = Array.from(files || []);
    if (!selected.length) return;
    if (selected.length > MAX_IMAGES_PER_UPLOAD) { notify.warn(`За одну загрузку можно выбрать не более ${MAX_IMAGES_PER_UPLOAD} изображений.`); return; }
    setUploading(true);
    setUploadCount(0);
    try {
      let done = 0;
      // Sequential uploads keep browser and Files API memory bounded.
      for (const file of selected) {
        if (!/\.(png|jpe?g|webp|gif|bmp|ppm)$/i.test(file.name)) throw new Error(`Неподдерживаемый формат: ${file.name}`);
        await uploadImage(file, folder);
        setUploadCount(++done);
      }
      notify.success(`Изображений загружено: ${selected.length}`);
      refresh();
    } catch (e) {
      notify.error(e?.response?.data?.message || e.message || 'Не удалось загрузить картинку.');
      refresh();
    } finally { setUploading(false); }
  };

  if (!open) return null;
  const visible = showMore ? images : images.slice(0, 80);
  return <section aria-label="Галерея изображений курса" className="mt-4 space-y-3"
    onDragOver={(event) => { if (!disabled && Array.from(event.dataTransfer?.types || []).includes('Files')) event.preventDefault(); }}
    onDrop={(event) => { if (!disabled && event.dataTransfer?.files?.length) { event.preventDefault(); event.stopPropagation(); void upload(event.dataTransfer.files); } }}>
    <div className="flex flex-wrap items-center justify-between gap-2">
      <div className="text-sm font-semibold">Изображения курса · {images.length}</div>
      <div className="flex gap-2">
        <Button type="button" variant="outline" disabled={loading || uploading} onClick={refresh} title="Обновить галерею"><RefreshCcw size={15}/></Button>
        <label className={`btn-outline cursor-pointer${disabled || uploading ? ' pointer-events-none opacity-60' : ''}`}>
          <ImagePlus size={15}/> Загрузить изображения
          <input type="file" multiple accept="image/png,image/jpeg,image/webp,image/gif,image/bmp,image/x-portable-pixmap" className="hidden" disabled={disabled || uploading}
            data-taskforge-automation-id="course-image-upload" onChange={(event) => { void upload(event.target.files); event.target.value = ''; }} />
        </label>
      </div>
    </div>
    {uploading ? <div role="status" className="text-sm">Загружено {uploadCount} изображений…</div> : null}
    {loading ? <div role="status" className="text-sm text-[rgb(var(--text-muted))]">Загружаем галерею…</div> : null}
    {error ? <div role="alert" className="text-sm text-red-600 dark:text-red-300">{error}</div> : null}
    {!loading && !error && images.length === 0 ? <div className="text-sm text-[rgb(var(--text-muted))]">Изображений пока нет.</div> : null}
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
      {visible.map((item) => <article key={item.key} className="min-w-0 rounded-lg border border-[rgb(var(--border))] bg-[rgb(var(--card))] p-2">
        <div className="flex aspect-[4/3] items-center justify-center overflow-hidden rounded-md bg-[rgb(var(--muted))]/30">
          <img src={item.url} alt={item.fileName} loading="lazy" className="max-h-full max-w-full object-contain" />
        </div>
        <div className="mt-2 truncate text-xs" title={item.fileName}>{item.fileName}</div>
        <div className="mt-1 text-xs text-[rgb(var(--text-muted))]">Использований: {item.count}</div>
        <button type="button" data-taskforge-image-key={item.key} className="mt-2 inline-flex items-center gap-1 rounded-md border border-[rgb(var(--border))] px-2 py-1 text-xs"
          onClick={() => navigator.clipboard.writeText(item.url).then(() => notify.success('Ссылка скопирована'), () => notify.error('Не удалось скопировать ссылку'))}>
          <Copy size={12}/> URL
        </button>
      </article>)}
    </div>
    {!showMore && images.length > visible.length ? <Button type="button" variant="outline" onClick={() => setShowMore(true)}>Показать все ({images.length})</Button> : null}
  </section>;
}
