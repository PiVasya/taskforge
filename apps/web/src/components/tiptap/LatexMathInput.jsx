import React, { useMemo } from 'react';
import LatexFormula from './LatexFormula';
import { renderLatexMathHtml, MAX_LATEX_LENGTH } from './latexMath';

export default function LatexMathInput({ draft, onChange, onSave, onCancel }) {
  const valid = useMemo(() => Boolean(renderLatexMathHtml(draft.latex, draft.kind === 'blockMath')), [draft]);
  return <div className="tiptap-latex-input" role="group" aria-label="Редактирование LaTeX">
    <div className="tiptap-latex-input-header">
      <span>{draft.kind === 'blockMath' ? 'Формула отдельным блоком' : 'Формула в тексте'}</span>
      <button type="button" onClick={onCancel} aria-label="Отменить редактирование формулы">×</button>
    </div>
    <textarea
      aria-label="Исходник LaTeX"
      value={draft.latex}
      rows={draft.kind === 'blockMath' ? 3 : 2}
      maxLength={MAX_LATEX_LENGTH}
      onChange={(event) => onChange(event.target.value)}
      onKeyDown={(event) => {
        if (event.key === 'Escape') { event.preventDefault(); onCancel(); }
        if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && valid) { event.preventDefault(); onSave(); }
      }}
      autoFocus
      spellCheck={false}
    />
    <div className="tiptap-latex-preview" aria-live="polite">
      {draft.latex.trim() ? (
        valid ? <LatexFormula latex={draft.latex} displayMode={draft.kind === 'blockMath'} />
          : <span className="tiptap-latex-error">Некорректная формула LaTeX</span>
      ) : null}
    </div>
    <div className="tiptap-latex-actions">
      <button type="button" onClick={onCancel}>Отмена</button>
      <button type="button" onClick={onSave} disabled={!valid}>{draft.editing ? 'Сохранить' : 'Вставить'}</button>
    </div>
  </div>;
}
