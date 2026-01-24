import React, { useEffect, useMemo } from 'react';
import { useEditor, EditorContent } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
import Link from '@tiptap/extension-link';
import Placeholder from '@tiptap/extension-placeholder';
import CodeBlockLowlight from '@tiptap/extension-code-block-lowlight';
import { lowlight } from 'lowlight/lib/common';
import { uploadImage } from '../../api/files';

function safeParseJson(str) {
  if (!str) return null;
  try {
    const o = JSON.parse(str);
    if (o && typeof o === 'object') return o;
    return null;
  } catch {
    return null;
  }
}

async function pickImageFile() {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/*';
    input.onchange = () => {
      const f = input.files && input.files[0] ? input.files[0] : null;
      resolve(f);
    };
    input.click();
  });
}

export default function StatementEditor({ value, onChange }) {
  const initialDoc = useMemo(() => {
    const json = safeParseJson(value);
    if (json) return json;
    // если раньше было plain-text, создаём документ с одним параграфом
    if (typeof value === 'string' && value.trim().length > 0) {
      return { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: value }] }] };
    }
    return { type: 'doc', content: [{ type: 'paragraph' }] };
  }, []); // важно: только при первом монтировании

  const editor = useEditor({
    extensions: [
      // отключаем встроенный codeBlock у StarterKit, потому что используем CodeBlockLowlight
      StarterKit.configure({ codeBlock: false }),
      CodeBlockLowlight.configure({ lowlight }),
      Link.configure({ openOnClick: false, autolink: true, linkOnPaste: true }),
      Image.configure({ inline: false, allowBase64: false }),
      Placeholder.configure({ placeholder: 'Опишите условие задания…' }),
    ],
    content: initialDoc,
    editorProps: {
      attributes: {
        class: 'min-h-[220px] tiptap tiptap-editor focus:outline-none',
      },
      handlePaste(view, event) {
        const items = event?.clipboardData?.items;
        if (!items) return false;
        for (const it of items) {
          if (it.kind === 'file') {
            const file = it.getAsFile();
            if (file && file.type && file.type.startsWith('image/')) {
              event.preventDefault();
              (async () => {
                const r = await uploadImage(file);
                if (r?.url) editor?.chain().focus().setImage({ src: r.url }).run();
              })();
              return true;
            }
          }
        }
        return false;
      },
      handleDrop(view, event, slice, moved) {
        const files = event?.dataTransfer?.files;
        if (!files || files.length === 0) return false;
        const file = files[0];
        if (file && file.type && file.type.startsWith('image/')) {
          event.preventDefault();
          (async () => {
            const r = await uploadImage(file);
            if (r?.url) editor?.chain().focus().setImage({ src: r.url }).run();
          })();
          return true;
        }
        return false;
      },
    },
    onUpdate({ editor }) {
      const json = editor.getJSON();
      onChange?.(JSON.stringify(json));
    },
  });

  // если value пришёл извне и это уже json-документ — обновляем редактор
  useEffect(() => {
    if (!editor) return;
    const json = safeParseJson(value);
    if (!json) return;
    const current = editor.getJSON();
    const curStr = JSON.stringify(current);
    const nextStr = JSON.stringify(json);
    if (curStr !== nextStr) {
      editor.commands.setContent(json, false);
    }
  }, [editor, value]);

  if (!editor) return <div className="text-slate-500">Загрузка редактора…</div>;

  const btn = (active, onClick, label) => (
    <button
      type="button"
      onClick={onClick}
      className={`px-2 py-1 rounded-md border text-sm transition ${
        active
          ? 'bg-slate-900 text-white border-slate-900 dark:bg-slate-100 dark:text-slate-900 dark:border-slate-100'
          : 'bg-[rgb(var(--card))] border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800'
      }`}
    >
      {label}
    </button>
  );

  const setLink = () => {
    const prev = editor.getAttributes('link').href || '';
    const url = window.prompt('Ссылка (URL):', prev);
    if (url === null) return;
    if (url.trim() === '') {
      editor.chain().focus().unsetLink().run();
      return;
    }
    editor.chain().focus().setLink({ href: url.trim() }).run();
  };

  const insertImage = async () => {
    const f = await pickImageFile();
    if (!f) return;
    const r = await uploadImage(f);
    if (r?.url) editor.chain().focus().setImage({ src: r.url }).run();
  };

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2 p-2 rounded-xl border border-slate-200 dark:border-slate-800 bg-[rgb(var(--card))] shadow-sm">
        {btn(editor.isActive('bold'), () => editor.chain().focus().toggleBold().run(), 'Жирный')}
        {btn(editor.isActive('italic'), () => editor.chain().focus().toggleItalic().run(), 'Курсив')}
        {btn(editor.isActive('code'), () => editor.chain().focus().toggleCode().run(), 'Код')}
        {btn(editor.isActive('heading', { level: 2 }), () => editor.chain().focus().toggleHeading({ level: 2 }).run(), 'H2')}
        {btn(editor.isActive('bulletList'), () => editor.chain().focus().toggleBulletList().run(), '• Список')}
        {btn(editor.isActive('orderedList'), () => editor.chain().focus().toggleOrderedList().run(), '1. Список')}
        {btn(editor.isActive('blockquote'), () => editor.chain().focus().toggleBlockquote().run(), 'Цитата')}
        {btn(editor.isActive('codeBlock'), () => editor.chain().focus().toggleCodeBlock().run(), 'Блок кода')}
        {btn(editor.isActive('link'), setLink, 'Ссылка')}
        {btn(false, insertImage, 'Картинка')}
      </div>

      <div className="tiptap rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-[rgb(var(--card))] shadow-sm">
        <EditorContent editor={editor} />
      </div>

      <div className="text-xs text-slate-500">
        Можно вставлять картинки через Ctrl+V или перетаскиванием в редактор.
      </div>
    </div>
  );
}
