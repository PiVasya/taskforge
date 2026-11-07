import { Image } from '@tiptap/extension-image';
import { imageDimensionFromElement, normalizeImageDimension } from '../imageSizing';

// An opt-in display property of the existing Tiptap image node.
// Image bytes are never modified; neobrutal themes compose monochrome diagrams.
export const AdaptiveImage = Image.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      width: {
        default: null,
        parseHTML: (element) => imageDimensionFromElement(element, 'width', true),
        renderHTML: (attrs) => {
          const width = normalizeImageDimension(attrs.width, true);
          return width ? { style: `width: ${width}` } : {};
        },
      },
      height: {
        default: null,
        parseHTML: (element) => imageDimensionFromElement(element, 'height'),
        renderHTML: (attrs) => {
          const height = normalizeImageDimension(attrs.height);
          return height ? { style: `height: ${height}` } : {};
        },
      },
      monochrome: {
        default: false,
        parseHTML: (element) => element.getAttribute('data-tf-mono') === 'true',
        renderHTML: (attrs) => attrs.monochrome ? { 'data-tf-mono': 'true' } : {},
      },
      sourceBackground: {
        default: '#ffffff',
        parseHTML: (element) => element.getAttribute('data-tf-mono-bg') || '#ffffff',
        renderHTML: (attrs) => attrs.monochrome ? { 'data-tf-mono-bg': attrs.sourceBackground || '#ffffff' } : {},
      },
      sourceDark: {
        default: false,
        parseHTML: (element) => element.getAttribute('data-tf-mono-dark') === 'true',
        renderHTML: (attrs) => attrs.monochrome ? { 'data-tf-mono-dark': String(Boolean(attrs.sourceDark)) } : {},
      },
    };
  },
});

export function isDarkMonochromeBackground(value) {
  const raw = String(value || '').replace(/^#/, '');
  const hex = raw.length === 3 ? raw.split('').map((char) => `${char}${char}`).join('') : raw;
  if (!/^[0-9a-f]{6}$/i.test(hex)) return false;
  const channels = [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const linear = channels.map((x) => x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4);
  return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722 < 0.18;
}
