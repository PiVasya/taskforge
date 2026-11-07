import React, { useMemo } from "react";
import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { Link } from "@tiptap/extension-link";
import { TextAlign } from "@tiptap/extension-text-align";
import { Highlight } from "@tiptap/extension-highlight";
import { TextStyle } from "@tiptap/extension-text-style";
import { Color } from "@tiptap/extension-color";
import { Subscript } from "@tiptap/extension-subscript";
import { Superscript } from "@tiptap/extension-superscript";
import { normalizeLegacyTiptapDoc, plainTextToTiptapDoc } from "./markdownToTiptap";
import { FontSize } from "./extensions/FontSize";
import { InlineMath } from "./extensions/InlineMath";
import { AdaptiveImage } from "./extensions/AdaptiveImage";

import "./tiptap.css";

function safeParseJson(str, preserveLiteral = false) {
  if (!str) return null;
  try {
    const o = JSON.parse(str);
    if (o && typeof o === "object" && o.type === "doc") return preserveLiteral ? o : normalizeLegacyTiptapDoc(o);
    return null;
  } catch {
    return null;
  }
}

function TiptapDocViewer({ doc }) {
  const editor = useEditor({
    editable: false,
    extensions: [
      StarterKit.configure({ link: false }),
      Highlight.configure({ multicolor: true }),
      TextStyle,
      FontSize,
      InlineMath,
      Color,
      Subscript,
      Superscript,
      TextAlign.configure({ types: ["heading", "paragraph"] }),
      Link.configure({
        openOnClick: true,
        HTMLAttributes: {
          rel: "noopener noreferrer nofollow",
          target: "_blank",
          class: "tiptap-link",
        },
      }),
      AdaptiveImage.configure({ inline: false, allowBase64: false }),
    ],
    content: doc,
    editorProps: {
      attributes: {
        class: "tiptap-content tiptap-content-readonly prose max-w-none text-neutral-900 dark:text-neutral-100 dark:prose-invert",
      },
    },
  });

  if (!editor) return <div className="text-neutral-500">…</div>;
  return <EditorContent editor={editor} />;
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

function renderStaticNode(node, key, context) {
  if (!node || typeof node !== 'object') return null;
  if (node.type === 'text') {
    const value = String(node.text || '');
    if (context.fill && !context.used) {
      const match = /_{3,}/.exec(value);
      if (match) {
        context.used = true;
        const width = Math.min(30, Math.max(6, match[0].length * 2));
        return <React.Fragment key={key}>
          {markedText(value.slice(0, match.index), node.marks)}
          <input
            className="fill-input"
            value={context.fill.value || ''}
            readOnly={context.fill.readOnly}
            aria-label={context.fill.ariaLabel || 'Ответ на вопрос'}
            onChange={context.fill.readOnly ? undefined : (event) => context.fill.onChange?.(event.target.value)}
            style={{ width: `${width}ch` }}
            {...context.fill.attributes}
          />
          {markedText(value.slice(match.index + match[0].length), node.marks)}
        </React.Fragment>;
      }
    }
    return <React.Fragment key={key}>{markedText(value, node.marks)}</React.Fragment>;
  }
  if (node.type === 'hardBreak') return <br key={key} />;
  if (node.type === 'horizontalRule') return <hr key={key} />;
  if (node.type === 'image') {
    const src = safeUrl(node.attrs?.src, true);
    return src ? <img
      key={key}
      src={src}
      alt={node.attrs?.alt || ''}
      title={node.attrs?.title || ''}
      loading="lazy"
      decoding="async"
      data-tf-mono={node.attrs?.monochrome ? 'true' : undefined}
      data-tf-mono-bg={node.attrs?.monochrome ? node.attrs?.sourceBackground || '#ffffff' : undefined}
      data-tf-mono-dark={node.attrs?.monochrome ? String(Boolean(node.attrs?.sourceDark)) : undefined}
    /> : null;
  }
  if (node.type === 'mathInline') {
    const { kind, numerator, denominator, expression } = node.attrs || {};
    return <span key={key} className="tiptap-inline-math">
      <math xmlns="http://www.w3.org/1998/Math/MathML">
        {kind === 'sqrt' ? <msqrt><mtext>{String(expression || '')}</mtext></msqrt>
          : <mfrac><mtext>{String(numerator || '')}</mtext><mtext>{String(denominator || '')}</mtext></mfrac>}
      </math>
    </span>;
  }
  const children = (node.content || []).map((child, index) => renderStaticNode(child, `${key}.${index}`, context));
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

function StatementViewer({ value, staticOnly = false, fill = null, preserveLiteral = false }) {
  const doc = useMemo(() => safeParseJson(value, preserveLiteral) || plainTextToTiptapDoc(value), [value, preserveLiteral]);
  if (staticOnly) return <StaticStatement doc={doc} fill={fill} />;
  return <TiptapDocViewer doc={doc} />;
}

export default React.memo(StatementViewer);
