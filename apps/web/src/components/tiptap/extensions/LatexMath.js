import { Node, mergeAttributes } from '@tiptap/core';
import { renderLatexMathHtml } from '../latexMath';

function createLatexNode(name, displayMode) {
  const tag = displayMode ? 'div' : 'span';
  const attribute = displayMode ? 'data-tf-latex-block' : 'data-tf-latex-inline';
  const className = `tiptap-latex-math ${displayMode ? 'tiptap-latex-block' : 'tiptap-latex-inline'}`;

  return Node.create({
    name,
    group: displayMode ? 'block' : 'inline',
    inline: !displayMode,
    atom: true,
    selectable: true,

    addAttributes() {
      return {
        latex: {
          default: '',
          parseHTML: (element) => element.getAttribute('data-latex') || '',
        },
      };
    },

    parseHTML() {
      return [{ tag: `${tag}[${attribute}]` }, { tag: `${tag}[data-type="${name}"]` }];
    },

    renderHTML({ node, HTMLAttributes }) {
      const latex = String(node.attrs.latex || '');
      return [tag, mergeAttributes(HTMLAttributes, {
        'data-type': name,
        [attribute]: 'true',
        'data-latex': latex,
        class: className,
      }), latex];
    },

    addNodeView() {
      return ({ node }) => {
        const dom = document.createElement(tag);
        dom.className = className;
        dom.contentEditable = 'false';
        dom.setAttribute('role', 'math');

        const updateFormula = (current) => {
          const latex = String(current.attrs.latex || '');
          dom.setAttribute(attribute, 'true');
          dom.setAttribute('data-type', name);
          dom.setAttribute('data-latex', latex);
          dom.setAttribute('aria-label', latex);
          const html = renderLatexMathHtml(latex, displayMode);
          if (html) dom.innerHTML = html;
          else dom.textContent = latex || '∑';
        };

        updateFormula(node);
        return {
          dom,
          update(next) {
            if (next.type.name !== name) return false;
            updateFormula(next);
            return true;
          },
          selectNode() { dom.classList.add('ProseMirror-selectednode'); },
          deselectNode() { dom.classList.remove('ProseMirror-selectednode'); },
          ignoreMutation() { return true; },
        };
      };
    },
  });
}

// Node names match the Tiptap mathematics JSON shape for portable authored content.
export const InlineLatexMath = createLatexNode('inlineMath', false);
export const BlockLatexMath = createLatexNode('blockMath', true);
