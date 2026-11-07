import { Node, mergeAttributes } from '@tiptap/core';

// A small inline math node shared by the existing statement editor and viewer.
// MathML is native browser markup; no external formula service or generated images.
export const InlineMath = Node.create({
  name: 'mathInline',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,

  addAttributes() {
    return {
      kind: { default: 'fraction', parseHTML: (element) => element.getAttribute('data-math-kind') || 'fraction' },
      numerator: { default: '1', parseHTML: (element) => element.getAttribute('data-math-numerator') || '1' },
      denominator: { default: '2', parseHTML: (element) => element.getAttribute('data-math-denominator') || '2' },
      expression: { default: 'x', parseHTML: (element) => element.getAttribute('data-math-expression') || 'x' },
    };
  },

  parseHTML() {
    return [{ tag: 'span[data-tf-math-inline]' }];
  },

  renderHTML({ node, HTMLAttributes }) {
    const { kind, numerator, denominator, expression } = node.attrs;
    const ns = 'http://www.w3.org/1998/Math/MathML';
    const body = kind === 'sqrt'
      ? [`${ns} msqrt`, [`${ns} mtext`, String(expression || '')]]
      : [`${ns} mfrac`, [`${ns} mtext`, String(numerator || '')], [`${ns} mtext`, String(denominator || '')]];
    return ['span', mergeAttributes(HTMLAttributes, {
      'data-tf-math-inline': 'true',
      'data-math-kind': kind,
      'data-math-numerator': numerator,
      'data-math-denominator': denominator,
      'data-math-expression': expression,
      class: 'tiptap-inline-math',
    }), [`${ns} math`, {}, body]];
  },
});
