import React, { useMemo } from 'react';
import { renderLatexMathHtml } from './latexMath';

function LatexFormula({ latex, displayMode = false }) {
  const source = typeof latex === 'string' ? latex : '';
  const html = useMemo(() => renderLatexMathHtml(source, displayMode), [source, displayMode]);
  const Tag = displayMode ? 'div' : 'span';
  return <Tag
    className={`tiptap-latex-math ${displayMode ? 'tiptap-latex-block' : 'tiptap-latex-inline'}`}
    role="math"
    aria-label={source}
    data-type={displayMode ? 'blockMath' : 'inlineMath'}
    data-tf-latex-inline={displayMode ? undefined : 'true'}
    data-tf-latex-block={displayMode ? 'true' : undefined}
    data-latex={source}
  >
    {html ? <span dangerouslySetInnerHTML={{ __html: html }} /> : <code className="tiptap-latex-invalid">{source || '∑'}</code>}
  </Tag>;
}

export default React.memo(LatexFormula);
