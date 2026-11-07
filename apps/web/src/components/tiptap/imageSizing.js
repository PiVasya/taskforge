// The editor and the read-only quiz renderer must interpret saved image sizes identically.
// Size values are deliberately restricted to bounded pixels and percentages; arbitrary
// CSS from imported Tiptap JSON is not trusted.
export function normalizeImageDimension(value, allowPercent = false) {
  if (value == null || value === '') return null;
  const raw = String(value).trim();
  const match = /^(\d{1,5})(px|%)?$/i.exec(raw);
  if (!match) return null;
  const number = Number(match[1]);
  const unit = (match[2] || 'px').toLowerCase();
  if (!Number.isSafeInteger(number) || number < 1) return null;
  if (unit === '%' && allowPercent && number <= 100) return `${number}%`;
  if (unit === 'px' && number <= 8192) return `${number}px`;
  return null;
}

export function imageSizingStyle(attrs) {
  const width = normalizeImageDimension(attrs?.width, true);
  const height = normalizeImageDimension(attrs?.height);
  return width || height ? { ...(width ? { width } : {}), ...(height ? { height } : {}) } : undefined;
}

export function imageDimensionFromElement(element, attribute, allowPercent = false) {
  const styleValue = element?.style?.[attribute];
  return normalizeImageDimension(styleValue, allowPercent)
    || normalizeImageDimension(element?.getAttribute?.(attribute), allowPercent);
}

// Compatibility with older rich JSON that embedded PNG/JPEG/GIF/WEBP instead of
// storing a Files API URL. Deliberately excludes SVG and arbitrary data URLs.
export function isSafeLegacyRasterImage(value) {
  return typeof value === 'string' && value.length <= 8 * 1024 * 1024
    && /^data:image\/(?:png|jpe?g|webp|gif);base64,[a-z0-9+/]+={0,2}$/i.test(value);
}
