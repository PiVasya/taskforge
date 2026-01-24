import React, { useMemo } from 'react';
import { useEditor, EditorContent } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
// NOTE: We intentionally omit the Link extension here.  Including Link
// alongside StarterKit in the viewer can cause a duplicate extension
// error ("Duplicate extension names found: ['link']") in newer
// versions of TipTap.  StarterKit already knows how to render
// existing anchor tags in the document.  If link-specific behaviour
// (such as autolinking or openOnClick) is needed in the future,
// consider configuring Link on the editor side only.
// We need the lowlight-based code block extension from TipTap.  The
// `@tiptap/extension-code-block-lowlight` package exposes a custom
// CodeBlockLowlight extension that integrates with the lowlight
// highlighter.  It must be imported explicitly alongside the
// highlighter instance.  Importing from `lowlight/lib/common` is no
// longer supported in v3 of the lowlight package – see the exports
// field in `node_modules/lowlight/package.json`.  Instead, import
// `lowlight` directly.  We'll register specific languages if
// necessary elsewhere.
// We no longer rely on the lowlight-based code block extension.  Using
// `@tiptap/extension-code-block-lowlight` with `lowlight` requires
// importing from `lowlight/lib/common`, which is no longer exported in
// recent versions of the package.  This caused build errors (see
// `Module not found: Package path ./lib/common is not exported`).  To
// avoid these issues, we fall back to the default code block provided
// by StarterKit.  Code blocks will still appear correctly and can be
// styled via CSS, but they will not have syntax highlighting.

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
            // Use the default codeBlock from StarterKit.  We previously
            // disabled this and registered a Lowlight-powered version,
            // but that required importing `lowlight/lib/common`, which
            // is not exported in the installed version of lowlight.  By
            // leaving codeBlock enabled here and removing the
            // lowlight-dependent extension, we avoid the build error.
            StarterKit,
            // We deliberately avoid including the Link extension here to prevent
            // duplicate extension errors.  See the note above.
            Image.configure({ inline: false, allowBase64: false }),
          ],
          content: doc,
          editorProps: { attributes: { class: 'prose max-w-none' } },
        }
      : null,
  );

  if (!doc) {
    return <div className="prose max-w-none whitespace-pre-wrap break-words">{value}</div>;
  }
  if (!editor) return <div className="text-slate-500">…</div>;
  return <EditorContent editor={editor} />;
}
