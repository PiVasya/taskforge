// Quiz question/choice documents use the same Tiptap JSON as assignment statements.
// Plain legacy values are interpreted as literal text, not Markdown source.
export function literalTextDocument(value) {
  const lines = String(value ?? '').replace(/\r/g, '').split('\n');
  return {
    type: 'doc',
    content: lines.map((line) => ({
      type: 'paragraph',
      content: line ? [{ type: 'text', text: line }] : [],
    })),
  };
}

export function parseQuizDocument(value) {
  if (!value || typeof value !== 'string') return null;
  try {
    const doc = JSON.parse(value);
    return doc && doc.type === 'doc' && Array.isArray(doc.content) ? doc : null;
  } catch {
    return null;
  }
}

function documentHasVisibleContent(node) {
  return node?.type === 'image'
    || node?.type === 'mathInline'
    || ((node?.type === 'inlineMath' || node?.type === 'blockMath') && Boolean(String(node.attrs?.latex || '').trim()))
    || (node?.type === 'text' && Boolean(String(node.text || '').trim()))
    || (Array.isArray(node?.content) && node.content.some(documentHasVisibleContent));
}

export function quizRichValue(rich, fallback = '') {
  const doc = parseQuizDocument(rich);
  // A partially migrated record can have an empty rich document but still have its old prompt.
  // Never replace a nonempty document: switching editor modes must preserve its formatting.
  return doc && (documentHasVisibleContent(doc) || !String(fallback || '').trim())
    ? rich
    : JSON.stringify(literalTextDocument(fallback));
}

export function quizDocumentPlainText(value) {
  const doc = typeof value === 'string' ? parseQuizDocument(value) : value;
  if (!doc) return String(value ?? '');
  const visit = (node) => {
    if (!node || typeof node !== 'object') return '';
    if (node.type === 'text') return String(node.text || '');
    if (node.type === 'hardBreak') return '\n';
    if (node.type === 'image') return '[Изображение]';
    if (node.type === 'inlineMath' || node.type === 'blockMath') return String(node.attrs?.latex || '');
    if (node.type === 'mathInline') {
      const attrs = node.attrs || {};
      return attrs.kind === 'sqrt'
        ? `√(${attrs.expression || ''})`
        : `(${attrs.numerator || ''})/(${attrs.denominator || ''})`;
    }
    const parts = Array.isArray(node.content) ? node.content.map(visit) : [];
    return ['doc', 'paragraph', 'heading', 'listItem', 'bulletList', 'orderedList', 'blockquote', 'codeBlock'].includes(node.type)
      ? parts.join(['doc', 'bulletList', 'orderedList', 'blockquote'].includes(node.type) ? '\n' : '')
      : parts.join('');
  };
  return visit(doc).trim();
}

export function hasQuizContent(rich, fallback = '') {
  // Match the effective document used by the editor and learner viewer: an
  // empty migrated JSON document must not hide an existing legacy prompt.
  const doc = parseQuizDocument(quizRichValue(rich, fallback));
  return documentHasVisibleContent(doc);
}

export function setQuizQuestionEditorMode(question, mode) {
  const selectedMode = mode === 'rich' ? 'rich' : 'plain';
  return {
    ...question,
    promptEditorMode: selectedMode,
    options: Array.isArray(question?.options)
      ? question.options.map((option) => ({ ...option, editorMode: selectedMode }))
      : question?.options,
  };
}

export function normalizeQuizQuestions(questions) {
  if (!Array.isArray(questions)) return [];
  return questions.map((question) => setQuizQuestionEditorMode({
    ...question,
    promptContentJson: quizRichValue(question?.promptContentJson, question?.prompt),
    optionsLayout: question?.optionsLayout === 'inline' ? 'inline' : 'list',
    options: Array.isArray(question?.options) ? question.options.map((option) => ({
      ...option,
      contentJson: quizRichValue(option?.contentJson, option?.text),
    })) : [],
  }, question?.promptEditorMode));
}

// Detect a continuous blank even when styling splits underscores into adjacent text nodes.
// Image/math/hard-break nodes terminate the run, just as in the static viewer.
export function quizHasInlineBlank(question) {
  const doc = parseQuizDocument(quizRichValue(question?.promptContentJson, question?.prompt));
  const visit = (node) => {
    if (!node || !Array.isArray(node.content)) return false;
    let consecutiveText = '';
    for (const child of node.content) {
      if (child?.type === 'text') {
        consecutiveText += String(child.text || '');
        if (/_{3,}/.test(consecutiveText)) return true;
      } else {
        consecutiveText = '';
        if (visit(child)) return true;
      }
    }
    return false;
  };
  return visit(doc);
}
