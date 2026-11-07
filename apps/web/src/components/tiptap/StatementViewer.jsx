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
import StaticStatementViewer from "./StaticStatementViewer";

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

function StatementViewer({ value, staticOnly = false, fill = null, preserveLiteral = false }) {
  const doc = useMemo(() => safeParseJson(value, preserveLiteral) || plainTextToTiptapDoc(value), [value, preserveLiteral]);
  if (staticOnly) return <StaticStatementViewer value={value} fill={fill} preserveLiteral={preserveLiteral} />;
  return <TiptapDocViewer doc={doc} />;
}

export default React.memo(StatementViewer);
