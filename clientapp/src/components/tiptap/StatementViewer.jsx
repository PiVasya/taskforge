import React, { useMemo } from "react";
import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { Image } from "@tiptap/extension-image";

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
    <div className="prose max-w-none whitespace-pre-wrap break-words">
      {value || ""}
    </div>
  );
}

function TiptapDocViewer({ doc }) {
  const editor = useEditor({
    editable: false,
    extensions: [StarterKit, Image.configure({ inline: false, allowBase64: false })],
    content: doc,
    editorProps: { attributes: { class: "prose max-w-none" } },
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
