import React, { useMemo } from 'react';
import { useEditor, EditorContent } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
import Link from '@tiptap/extension-link';

function safeParseJson(str) {
  if (!str) return null;
  try {
    const o = JSON.parse(str);
    if (o && typeof o === 'object' && o.type === 'doc') return o;
    return null;
  } catch {
    return null;
  }
}

export default function StatementViewer({ value }) {
  const doc = useMemo(() => safeParseJson(value), [value]);

  // fallback на обычный текст для старых заданий
  if (!doc) {
    return <div className="prose max-w-none whitespace-pre-wrap break-words">{value}</div>;
  }

  const editor = useEditor({
    editable: false,
    extensions: [
      StarterKit,
      Link.configure({ openOnClick: true, autolink: true, linkOnPaste: true }),
      Image.configure({ inline: false, allowBase64: false }),
    ],
    content: doc,
    editorProps: {
      attributes: { class: 'prose max-w-none' },
    },
  });

  if (!editor) return <div className="text-slate-500">…</div>;
  return <EditorContent editor={editor} />;
}
