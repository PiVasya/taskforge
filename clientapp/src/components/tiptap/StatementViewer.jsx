import React, { useMemo } from 'react';
import { useEditor, EditorContent } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
import Link from '@tiptap/extension-link';
import CodeBlockLowlight from '@tiptap/extension-code-block-lowlight';
import { lowlight } from 'lowlight/lib/common';

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

  // вызывем useEditor всегда: если doc null — передаём null
  const editor = useEditor(
    doc
      ? {
          editable: false,
          extensions: [
            StarterKit.configure({ codeBlock: false }),
            CodeBlockLowlight.configure({ lowlight }),
            Link.configure({ openOnClick: true, autolink: true, linkOnPaste: true }),
            Image.configure({ inline: false, allowBase64: false }),
          ],
          content: doc,
          editorProps: { attributes: { class: 'tiptap tiptap-viewer' } },
        }
      : null,
  );

  if (!doc) {
    return <div className="tiptap tiptap-viewer whitespace-pre-wrap break-words">{value}</div>;
  }
  if (!editor) return <div className="text-slate-500">…</div>;
  return <EditorContent editor={editor} />;
}
