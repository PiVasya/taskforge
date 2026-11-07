import React, { useMemo } from 'react';
import { normalizeLegacyTiptapDoc, plainTextToTiptapDoc } from './markdownToTiptap';
import './tiptap.css';

function parseStatementDoc(value, preserveLiteral) {
  if (typeof value === 'string' && value) {
    try {
      const doc = JSON.parse(value);
      if (doc && doc.type === 'doc' && Array.isArray(doc.content)) {
        return preserveLiteral ? doc : normalizeLegacyTiptapDoc(doc);
      }
    } catch { /* Plain text is still valid input. */ }
  }
  return plainTextToTiptapDoc(value);
}

function safeUrl(value, image = false) {
  const url = String(value || '').trim();
  if (url.startsWith('/') && !url.startsWith('//')) return url;
  if (/^https?:\/\//i.test(url)) return url;
  if (!image && /^mailto:/i.test(url)) return url;
  return null;
}

function markedText(content, marks = []) {
  return marks.reduce((element, mark, index) => {
    const key = `${mark.type}-${index}`;
    switch (mark.type) {
      case 'bold': return <strong key={key}>{element}</strong>;
      case 'italic': return <em key={key}>{element}</em>;
      case 'underline': return <u key={key}>{element}</u>;
      case 'strike': return <s key={key}>{element}</s>;
      case 'code': return <code key={key}>{element}</code>;
      case 'superscript': return <sup key={key}>{element}</sup>;
      case 'subscript': return <sub key={key}>{element}</sub>;
      case 'highlight': {
        const color = String(mark.attrs?.color || '');
        const style = /^#[0-9a-f]{3,8}$/i.test(color) ? { backgroundColor: color, color: '#171717' } : undefined;
        return <mark key={key} style={style}>{element}</mark>;
      }
      case 'link': {
        const href = safeUrl(mark.attrs?.href);
        return href ? <a key={key} href={href} className="tiptap-link" rel="noopener noreferrer nofollow" target="_blank">{element}</a> : element;
      }
      case 'textStyle': {
        const color = String(mark.attrs?.color || '');
        const size = String(mark.attrs?.fontSize || '');
        const style = {};
        if (/^#[0-9a-f]{3,8}$/i.test(color) || /^rgba?\([\d, %.]+\)$/i.test(color)) style.color = color;
        if (/^\d{1,2}(px)?$/.test(size)) style.fontSize = `${Math.min(72, Number.parseInt(size, 10))}px`;
        return Object.keys(style).length ? <span key={key} style={style}>{element}</span> : element;
      }
      default: return element;
    }
  }, content);
}

// A fill blank may cross adjacent text nodes because bold/italic marks split text.
// The edit document stays untouched: only the read-only view receives a transient input.
function replaceInlineFillBlank(content, context) {
  if (!context.fill || context.used || !Array.isArray(content)) return content;
  for (let start = 0; start < content.length;) {
    if (content[start]?.type !== 'text') { start += 1; continue; }
    let end = start;
    let run = '';
    while (end < content.length && content[end]?.type === 'text') {
      run += String(content[end].text || '');
      end += 1;
    }
    const match = /_{3,}/.exec(run);
    if (!match) { start = end; continue; }
    const prefix = [];
    const suffix = [];
    let offset = 0;
    const after = match.index + match[0].length;
    for (const node of content.slice(start, end)) {
      const text = String(node.text || '');
      const next = offset + text.length;
      const beforeText = text.slice(0, Math.max(0, Math.min(text.length, match.index - offset)));
      const afterText = text.slice(Math.max(0, Math.min(text.length, after - offset)));
      if (beforeText) prefix.push({ ...node, text: beforeText });
      if (afterText) suffix.push({ ...node, text: afterText });
      offset = next;
    }
    context.used = true;
    return [
      ...content.slice(0, start), ...prefix,
      { type: 'quizAnswerBlank', attrs: { blankLength: match[0].length } },
      ...suffix, ...content.slice(end),
    ];
  }
  return content;
}

function renderStaticNode(node, key, context) {
  if (!node || typeof node !== 'object') return null;
  if (node.type === 'text') {
    const value = String(node.text || '');
    return <React.Fragment key={key}>{markedText(value, node.marks)}</React.Fragment>;
  }
  if (node.type === 'quizAnswerBlank') {
    const width = Math.min(30, Math.max(6, Number(node.attrs?.blankLength || 3) * 2));
    return <input
      key={key}
      className="fill-input"
      value={context.fill?.value || ''}
      readOnly={Boolean(context.fill?.readOnly)}
      aria-label={context.fill?.ariaLabel || 'Ответ на вопрос'}
      onChange={context.fill?.readOnly ? undefined : (event) => context.fill?.onChange?.(event.target.value)}
      style={{ width: `${width}ch` }}
      {...context.fill?.attributes}
    />;
  }
  if (node.type === 'hardBreak') return <br key={key} />;
  if (node.type === 'horizontalRule') return <hr key={key} />;
  if (node.type === 'image') {
    const src = safeUrl(node.attrs?.src, true);
    if (!src) return null;
    const image = <img
      src={src}
      alt={node.attrs?.alt || ''}
      title={node.attrs?.title || ''}
      loading="lazy"
      decoding="async"
      data-tf-mono={node.attrs?.monochrome ? 'true' : undefined}
      data-tf-mono-bg={node.attrs?.monochrome ? node.attrs?.sourceBackground || '#ffffff' : undefined}
      data-tf-mono-dark={node.attrs?.monochrome ? String(Boolean(node.attrs?.sourceDark)) : undefined}
    />;
    return <React.Fragment key={key}>{markedText(image, node.marks)}</React.Fragment>;
  }
  if (node.type === 'mathInline') {
    const { kind, numerator, denominator, expression } = node.attrs || {};
    const formula = <span className="tiptap-inline-math">
      <math xmlns="http://www.w3.org/1998/Math/MathML">
        {kind === 'sqrt' ? <msqrt><mtext>{String(expression || '')}</mtext></msqrt>
          : <mfrac><mtext>{String(numerator || '')}</mtext><mtext>{String(denominator || '')}</mtext></mfrac>}
      </math>
    </span>;
    return <React.Fragment key={key}>{markedText(formula, node.marks)}</React.Fragment>;
  }
  const content = replaceInlineFillBlank(node.content, context);
  const children = (content || []).map((child, index) => renderStaticNode(child, `${key}.${index}`, context));
  const align = ['left', 'right', 'center', 'justify'].includes(node.attrs?.textAlign) ? node.attrs.textAlign : undefined;
  switch (node.type) {
    case 'doc': return <React.Fragment key={key}>{children}</React.Fragment>;
    case 'paragraph': return <p key={key} style={align ? { textAlign: align } : undefined}>{children}</p>;
    case 'heading': {
      const level = [1, 2, 3].includes(node.attrs?.level) ? node.attrs.level : 2;
      return React.createElement(`h${level}`, { key, style: align ? { textAlign: align } : undefined }, children);
    }
    case 'bulletList': return <ul key={key}>{children}</ul>;
    case 'orderedList': return <ol key={key} start={Number(node.attrs?.start) > 0 ? Number(node.attrs.start) : 1}>{children}</ol>;
    case 'listItem': return <li key={key}>{children}</li>;
    case 'blockquote': return <blockquote key={key}>{children}</blockquote>;
    case 'codeBlock': return <pre key={key}><code>{children}</code></pre>;
    default: return <React.Fragment key={key}>{children}</React.Fragment>;
  }
}

function StaticStatement({ doc, fill }) {
  const context = { fill, used: false };
  return <div className="tiptap-content tiptap-content-readonly tiptap-content-static prose max-w-none text-neutral-900 dark:text-neutral-100 dark:prose-invert">
    {renderStaticNode(doc, 'root', context)}
  </div>;
}


function StaticStatementViewer({ value, fill = null, preserveLiteral = false }) {
  const doc = useMemo(() => parseStatementDoc(value, preserveLiteral), [value, preserveLiteral]);
  return <StaticStatement doc={doc} fill={fill} />;
}

export default React.memo(StaticStatementViewer);
