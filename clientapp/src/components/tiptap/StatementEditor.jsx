import React, { useMemo, useRef } from "react";
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

import {
  Bold,
  Italic,
  Underline as UnderlineIcon,
  Strikethrough,
  Code,
  Quote,
  List,
  ListOrdered,
  Heading2,
  Undo,
  Redo,
  AlignLeft,
  AlignCenter,
  AlignRight,
  AlignJustify,
  Link as LinkIcon,
  Image as ImageIcon,
  Highlighter,
  Subscript as SubIcon,
  Superscript as SupIcon,
  RemoveFormatting,
  Minus,
} from "lucide-react";

import { Button } from "../ui";
import { uploadImage } from "../../api/files";

import "./tiptap.css";

function safeParseJson(str) {
  if (!str) return null;
  try {
    const o = JSON.parse(str);
    if (o && typeof o === "object" && o.type === "doc") return o;
  } catch {
    // ignore
  }
  return null;
}

function ToolbarButton({ title, isActive, disabled, onClick, children }) {
  return (
    <Button
      type="button"
      title={title}
      onClick={onClick}
      disabled={disabled}
      className={
        "h-9 w-9 p-0 rounded-lg border transition-colors " +
        (isActive
          ? "bg-gray-900 text-white border-gray-900"
          : "bg-white hover:bg-gray-50 border-gray-200") +
        (disabled ? " opacity-50 cursor-not-allowed" : "")
      }
    >
      {children}
    </Button>
  );
}

function ToolbarDivider() {
  return <div className="w-px h-6 bg-gray-200 mx-2" />;
}

async function uploadAndInsertImageWithEditor(editor, file) {
  if (!editor || !file || !file.type?.startsWith("image/")) return false;

  try {
    const res = await uploadImage(file);
    // uploadImage() возвращает { key, url }
    const url = res?.url;
    if (!url) return false;

    editor.chain().focus().setImage({ src: url }).run();
    return true;
  } catch {
    return false;
  }
}

function pickFirstImageFileFromDataTransfer(dt) {
  if (!dt) return null;
  // 1) иногда браузер кладёт файлы сюда
  if (dt.files && dt.files.length > 0) {
    const f = dt.files[0];
    if (f && f.type?.startsWith("image/")) return f;
  }
  // 2) часто для Ctrl+V файлы лежат в items
  if (dt.items && dt.items.length > 0) {
    for (const it of dt.items) {
      if (it.kind === "file") {
        const f = it.getAsFile();
        if (f && f.type?.startsWith("image/")) return f;
      }
    }
  }
  return null;
}

function StatementEditor({ value, onChange }) {
  const editorRef = useRef(null);
  const initialContent = useMemo(() => {
    const doc = safeParseJson(value);
    return doc ?? (value ?? "");
  }, [value]);

  const editor = useEditor({
    extensions: [
      StarterKit.configure({
        // Link добавляем отдельно (нужно custom конфиг), иначе будет duplicate extension names
        link: false,
      }),

      Underline,
      Highlight,
      TextStyle,
      Color,
      Subscript,
      Superscript,

      TextAlign.configure({
        types: ["heading", "paragraph"],
      }),

      Link.configure({
        openOnClick: false,
        autolink: true,
        linkOnPaste: true,
        HTMLAttributes: {
          rel: "noopener noreferrer nofollow",
          target: "_blank",
          class: "tiptap-link",
        },
      }),

      Image.configure({
        inline: false,
        allowBase64: false,
      }),
    ],

    content: initialContent,

    editorProps: {
      attributes: {
        class:
          "tiptap-content min-h-[260px] rounded-xl border border-gray-200 bg-white px-4 py-3 outline-none",
      },

      handlePaste: (_view, event) => {
        const file = pickFirstImageFileFromDataTransfer(event.clipboardData);
        if (!file) return false;
        event.preventDefault();
        void uploadAndInsertImageWithEditor(editorRef.current, file);
        return true;
      },

      handleDrop: (_view, event) => {
        const file = pickFirstImageFileFromDataTransfer(event.dataTransfer);
        if (!file) return false;
        event.preventDefault();
        void uploadAndInsertImageWithEditor(editorRef.current, file);
        return true;
      },
    },

    onCreate: ({ editor }) => {
      editorRef.current = editor;
    },

    onUpdate: ({ editor }) => {
      onChange(JSON.stringify(editor.getJSON()));
    },
  });

  if (!editor) return null;

  const setLink = () => {
    const previousUrl = editor.getAttributes("link").href;
    const url = window.prompt("Введите ссылку", previousUrl || "https://");
    if (url === null) return;

    if (url.trim() === "") {
      editor.chain().focus().extendMarkRange("link").unsetLink().run();
      return;
    }

    editor.chain().focus().extendMarkRange("link").setLink({ href: url }).run();
  };

  const insertImageByUrl = () => {
    const url = window.prompt("URL картинки", "https://");
    if (!url) return;
    editor.chain().focus().setImage({ src: url }).run();
  };

  const uploadImageByPicker = async () => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = "image/*";
    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return;
      await uploadAndInsertImageWithEditor(editor, file);
    };
    input.click();
  };

  return (
    <div className="w-full">
      <div className="flex flex-wrap items-center gap-2 rounded-xl border border-gray-200 bg-white/70 px-3 py-2 backdrop-blur">
        <ToolbarButton
          title="Отменить"
          disabled={!editor.can().chain().focus().undo().run()}
          onClick={() => editor.chain().focus().undo().run()}
        >
          <Undo size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Повторить"
          disabled={!editor.can().chain().focus().redo().run()}
          onClick={() => editor.chain().focus().redo().run()}
        >
          <Redo size={18} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Жирный"
          isActive={editor.isActive("bold")}
          onClick={() => editor.chain().focus().toggleBold().run()}
        >
          <Bold size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Курсив"
          isActive={editor.isActive("italic")}
          onClick={() => editor.chain().focus().toggleItalic().run()}
        >
          <Italic size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Подчёркивание"
          isActive={editor.isActive("underline")}
          onClick={() => editor.chain().focus().toggleUnderline().run()}
        >
          <UnderlineIcon size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Зачёркивание"
          isActive={editor.isActive("strike")}
          onClick={() => editor.chain().focus().toggleStrike().run()}
        >
          <Strikethrough size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Код"
          isActive={editor.isActive("code")}
          onClick={() => editor.chain().focus().toggleCode().run()}
        >
          <Code size={18} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Заголовок (H2)"
          isActive={editor.isActive("heading", { level: 2 })}
          onClick={() => editor.chain().focus().toggleHeading({ level: 2 }).run()}
        >
          <Heading2 size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Цитата"
          isActive={editor.isActive("blockquote")}
          onClick={() => editor.chain().focus().toggleBlockquote().run()}
        >
          <Quote size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Маркированный список"
          isActive={editor.isActive("bulletList")}
          onClick={() => editor.chain().focus().toggleBulletList().run()}
        >
          <List size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Нумерованный список"
          isActive={editor.isActive("orderedList")}
          onClick={() => editor.chain().focus().toggleOrderedList().run()}
        >
          <ListOrdered size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Блок кода"
          isActive={editor.isActive("codeBlock")}
          onClick={() => editor.chain().focus().toggleCodeBlock().run()}
        >
          <Code size={18} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Выравнивание слева"
          isActive={editor.isActive({ textAlign: "left" })}
          onClick={() => editor.chain().focus().setTextAlign("left").run()}
        >
          <AlignLeft size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="По центру"
          isActive={editor.isActive({ textAlign: "center" })}
          onClick={() => editor.chain().focus().setTextAlign("center").run()}
        >
          <AlignCenter size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Справа"
          isActive={editor.isActive({ textAlign: "right" })}
          onClick={() => editor.chain().focus().setTextAlign("right").run()}
        >
          <AlignRight size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="По ширине"
          isActive={editor.isActive({ textAlign: "justify" })}
          onClick={() => editor.chain().focus().setTextAlign("justify").run()}
        >
          <AlignJustify size={18} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Подсветка"
          isActive={editor.isActive("highlight")}
          onClick={() => editor.chain().focus().toggleHighlight().run()}
        >
          <Highlighter size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Нижний индекс"
          isActive={editor.isActive("subscript")}
          onClick={() => editor.chain().focus().toggleSubscript().run()}
        >
          <SubIcon size={18} />
        </ToolbarButton>
        <ToolbarButton
          title="Верхний индекс"
          isActive={editor.isActive("superscript")}
          onClick={() => editor.chain().focus().toggleSuperscript().run()}
        >
          <SupIcon size={18} />
        </ToolbarButton>

        <div className="flex items-center gap-2 ml-2">
          <input
            type="color"
            title="Цвет текста"
            className="h-9 w-9 rounded-lg border border-gray-200 bg-white p-1"
            onChange={(e) => editor.chain().focus().setColor(e.target.value).run()}
          />
          <ToolbarButton
            title="Очистить форматирование"
            onClick={() => editor.chain().focus().unsetAllMarks().clearNodes().run()}
          >
            <RemoveFormatting size={18} />
          </ToolbarButton>
        </div>

        <ToolbarDivider />

        <ToolbarButton
          title="Ссылка"
          isActive={editor.isActive("link")}
          onClick={setLink}
        >
          <LinkIcon size={18} />
        </ToolbarButton>
        <ToolbarButton title="Картинка (загрузить)" onClick={uploadImageByPicker}>
          <ImageIcon size={18} />
        </ToolbarButton>
        <ToolbarButton title="Картинка (по URL)" onClick={insertImageByUrl}>
          <Minus size={18} />
        </ToolbarButton>

        <div className="ml-auto text-xs text-gray-500">
          Можно вставлять картинки: Ctrl+V / Drag&amp;Drop
        </div>
      </div>

      <div className="mt-3">
        <EditorContent editor={editor} />
      </div>
    </div>
  );
}

export default StatementEditor;
