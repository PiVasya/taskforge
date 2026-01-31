import React, { useMemo } from "react";
import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { Link } from "@tiptap/extension-link";
import { Image } from "@tiptap/extension-image";
import { Underline } from "@tiptap/extension-underline";
import { TextAlign } from "@tiptap/extension-text-align";
import { Highlight } from "@tiptap/extension-highlight";
import { TextStyle } from "@tiptap/extension-text-style";
import { Color } from "@tiptap/extension-color";
import { Subscript } from "@tiptap/extension-subscript";
import { Superscript } from "@tiptap/extension-superscript";

import "./tiptap.css";

function safeParseJson(str) {
  if (!str) return null;
  try {
    const o = JSON.parse(str);
    if (o && typeof o === "object" && o.type === "doc") return o;
    return null;
  } catch {
    return null;
  }
}

function PlainTextViewer({ value }) {
  return (
    <div className="prose max-w-none whitespace-pre-wrap break-words text-slate-900 dark:text-slate-100 dark:prose-invert">
      {value || ""}
    </div>
  );
}

function TiptapDocViewer({ doc }) {
  const editor = useEditor({
    editable: false,
    extensions: [
      StarterKit.configure({ link: false }),
      Underline,
      Highlight,
      TextStyle,
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
      Image.configure({ inline: false, allowBase64: false }),
    ],
    content: doc,
    editorProps: {
      attributes: {
        class: "tiptap-content tiptap-content-readonly prose max-w-none text-slate-900 dark:text-slate-100 dark:prose-invert",
      },
    },
  });

  if (!editor) return <div className="text-slate-500">…</div>;
  return <EditorContent editor={editor} />;
}

export default function StatementViewer({ value }) {
  const doc = useMemo(() => safeParseJson(value), [value]);

  // Старые задания (txt) — просто показываем текст, НЕ создаём editor вообще
  if (!doc) return <PlainTextViewer value={value} />;

  // Новые задания — TipTap doc (JSON)
  return <TiptapDocViewer doc={doc} />;
}
