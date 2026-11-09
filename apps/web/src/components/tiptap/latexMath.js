import katex from 'katex';
import 'katex/dist/katex.min.css';

// Bound rendering work for documents supplied through the course JSON importer.
export const MAX_LATEX_LENGTH = 4096;

export function renderLatexMathHtml(value, displayMode = false) {
  if (typeof value !== 'string' || !value.trim() || value.length > MAX_LATEX_LENGTH) return null;
  try {
    return katex.renderToString(value, {
      displayMode,
      output: 'htmlAndMathml',
      throwOnError: true,
      trust: false,
      strict: 'ignore',
      maxExpand: 1000,
      maxSize: 30,
    });
  } catch {
    return null;
  }
}
