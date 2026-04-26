import React, { useEffect, useMemo, useRef, useState } from "react";
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

import { FontSize } from "./extensions/FontSize";

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

function plainTextToDoc(value) {
  const text = String(value ?? "");
  if (!text.trim()) return "";
  if (/<[a-z][\s\S]*>/i.test(text)) return text;
  return {
    type: "doc",
    content: text.replace(/\r/g, "").split("\n").map((line) => ({
      type: "paragraph",
      content: line.length ? [{ type: "text", text: line }] : [],
    })),
  };
}

function ToolbarButton({ title, isActive, disabled, onClick, children }) {
  return (
    <button
      type="button"
      title={title}
      onClick={onClick}
      disabled={disabled}
      className={
        "h-9 w-9 inline-flex items-center justify-center rounded-lg transition-colors " +
        (isActive
          ? "bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900"
          : "text-neutral-700 dark:text-neutral-200 hover:bg-neutral-100 dark:hover:bg-neutral-800") +
        (disabled ? " opacity-40 cursor-not-allowed" : "")
      }
    >
      {children}
    </button>
  );
}

function ToolbarDivider() {
  return <div className="w-px h-6 bg-neutral-200 dark:bg-neutral-700 mx-2" />;
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
  const [ctxMenu, setCtxMenu] = useState({ open: false, x: 0, y: 0 });
  const initialContent = useMemo(() => {
    const doc = safeParseJson(value);
    return doc ?? plainTextToDoc(value ?? "");
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
      FontSize,
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
          "tiptap-content min-h-[260px] rounded-xl border border-gray-200 bg-white px-4 py-3 text-neutral-900 outline-none dark:border-neutral-800 dark:bg-neutral-950 dark:text-neutral-100",
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

  // Контекст-меню (ПКМ) — выносим туда «редкие» вещи (размер/цвет текста)
  // ВАЖНО: hooks должны вызываться всегда в одном порядке, поэтому ранний return ниже.
  useEffect(() => {
    if (!ctxMenu.open) return;

    const close = () => setCtxMenu((s) => ({ ...s, open: false }));
    const onKeydown = (e) => {
      if (e.key === "Escape") close();
    };

    window.addEventListener("scroll", close, true);
    window.addEventListener("resize", close);
    document.addEventListener("click", close);
    document.addEventListener("keydown", onKeydown);

    return () => {
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("resize", close);
      document.removeEventListener("click", close);
      document.removeEventListener("keydown", onKeydown);
    };
  }, [ctxMenu.open]);

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
      <div className="flex flex-wrap items-center gap-2 rounded-xl border border-neutral-200 dark:border-neutral-800 bg-white/70 dark:bg-neutral-950/60 px-3 py-2 backdrop-blur">
        <ToolbarButton
          title="Отменить"
          disabled={!editor.can().chain().focus().undo().run()}
          onClick={() => editor.chain().focus().undo().run()}
        >
          <Undo size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Повторить"
          disabled={!editor.can().chain().focus().redo().run()}
          onClick={() => editor.chain().focus().redo().run()}
        >
          <Redo size={20} />
        </ToolbarButton>

        <ToolbarDivider />

        <div className="flex items-center gap-2">
          <select
            title="Стиль текста"
            className="h-9 rounded-lg border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-950 px-2 text-sm"
            value={
              editor.isActive("heading", { level: 1 })
                ? "h1"
                : editor.isActive("heading", { level: 2 })
                  ? "h2"
                  : editor.isActive("heading", { level: 3 })
                    ? "h3"
                    : "p"
            }
            onChange={(e) => {
              const v = e.target.value;
              if (v === "p") editor.chain().focus().setParagraph().run();
              if (v === "h1") editor.chain().focus().toggleHeading({ level: 1 }).run();
              if (v === "h2") editor.chain().focus().toggleHeading({ level: 2 }).run();
              if (v === "h3") editor.chain().focus().toggleHeading({ level: 3 }).run();
            }}
          >
            <option value="p">Текст</option>
            <option value="h1">H1</option>
            <option value="h2">H2</option>
            <option value="h3">H3</option>
          </select>

          {/* Размер/цвет текста вынесены в контекст-меню по ПКМ */}
        </div>

        <ToolbarButton
          title="Жирный"
          isActive={editor.isActive("bold")}
          onClick={() => editor.chain().focus().toggleBold().run()}
        >
          <Bold size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Курсив"
          isActive={editor.isActive("italic")}
          onClick={() => editor.chain().focus().toggleItalic().run()}
        >
          <Italic size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Подчёркивание"
          isActive={editor.isActive("underline")}
          onClick={() => editor.chain().focus().toggleUnderline().run()}
        >
          <UnderlineIcon size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Зачёркивание"
          isActive={editor.isActive("strike")}
          onClick={() => editor.chain().focus().toggleStrike().run()}
        >
          <Strikethrough size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Код"
          isActive={editor.isActive("code")}
          onClick={() => editor.chain().focus().toggleCode().run()}
        >
          <Code size={20} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Заголовок (H2)"
          isActive={editor.isActive("heading", { level: 2 })}
          onClick={() => editor.chain().focus().toggleHeading({ level: 2 }).run()}
        >
          <Heading2 size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Цитата"
          isActive={editor.isActive("blockquote")}
          onClick={() => editor.chain().focus().toggleBlockquote().run()}
        >
          <Quote size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Маркированный список"
          isActive={editor.isActive("bulletList")}
          onClick={() => editor.chain().focus().toggleBulletList().run()}
        >
          <List size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Нумерованный список"
          isActive={editor.isActive("orderedList")}
          onClick={() => editor.chain().focus().toggleOrderedList().run()}
        >
          <ListOrdered size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Блок кода"
          isActive={editor.isActive("codeBlock")}
          onClick={() => editor.chain().focus().toggleCodeBlock().run()}
        >
          <Code size={20} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Выравнивание слева"
          isActive={editor.isActive({ textAlign: "left" })}
          onClick={() => editor.chain().focus().setTextAlign("left").run()}
        >
          <AlignLeft size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="По центру"
          isActive={editor.isActive({ textAlign: "center" })}
          onClick={() => editor.chain().focus().setTextAlign("center").run()}
        >
          <AlignCenter size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Справа"
          isActive={editor.isActive({ textAlign: "right" })}
          onClick={() => editor.chain().focus().setTextAlign("right").run()}
        >
          <AlignRight size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="По ширине"
          isActive={editor.isActive({ textAlign: "justify" })}
          onClick={() => editor.chain().focus().setTextAlign("justify").run()}
        >
          <AlignJustify size={20} />
        </ToolbarButton>

        <ToolbarDivider />

        <ToolbarButton
          title="Подсветка"
          isActive={editor.isActive("highlight")}
          onClick={() => editor.chain().focus().toggleHighlight().run()}
        >
          <Highlighter size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Нижний индекс"
          isActive={editor.isActive("subscript")}
          onClick={() => editor.chain().focus().toggleSubscript().run()}
        >
          <SubIcon size={20} />
        </ToolbarButton>
        <ToolbarButton
          title="Верхний индекс"
          isActive={editor.isActive("superscript")}
          onClick={() => editor.chain().focus().toggleSuperscript().run()}
        >
          <SupIcon size={20} />
        </ToolbarButton>

        <div className="flex items-center gap-2 ml-2">
          <ToolbarButton
            title="Очистить форматирование"
            onClick={() => editor.chain().focus().unsetAllMarks().clearNodes().run()}
          >
            <RemoveFormatting size={20} />
          </ToolbarButton>
        </div>

        <ToolbarDivider />

        <ToolbarButton
          title="Ссылка"
          isActive={editor.isActive("link")}
          onClick={setLink}
        >
          <LinkIcon size={20} />
        </ToolbarButton>
        <ToolbarButton title="Картинка (загрузить)" onClick={uploadImageByPicker}>
          <ImageIcon size={20} />
        </ToolbarButton>
        <ToolbarButton title="Картинка (по URL)" onClick={insertImageByUrl}>
          <Minus size={20} />
        </ToolbarButton>

        <div className="ml-auto text-xs text-gray-500">
          Можно вставлять картинки: Ctrl+V / Drag&amp;Drop
        </div>
      </div>

      <div
        className="mt-3"
        onContextMenu={(e) => {
          const inside = e.target.closest(".tiptap");
          if (!inside) return;
          e.preventDefault();

          const menuW = 260;
          const menuH = 160;
          let x = e.clientX;
          let y = e.clientY;
          if (x + menuW > window.innerWidth - 8) x = window.innerWidth - menuW - 8;
          if (y + menuH > window.innerHeight - 8) y = window.innerHeight - menuH - 8;

          setCtxMenu({ open: true, x, y });
        }}
      >
        <EditorContent editor={editor} />

        {ctxMenu.open && (
          <div
            role="menu"
            style={{ position: "fixed", left: ctxMenu.x, top: ctxMenu.y, zIndex: 9999 }}
            className="min-w-[260px] rounded-xl border border-neutral-200 dark:border-neutral-700 bg-white dark:bg-neutral-950 shadow-2xl p-2"
            onClick={(ev) => ev.stopPropagation()}
          >
            <div className="px-2 py-1 text-xs text-neutral-500">Формат</div>

            <div className="px-2 py-2">
              <div className="text-xs text-neutral-500 mb-1">Размер текста</div>
              <select
                className="w-full h-9 rounded-lg border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-950 px-2 text-sm"
                value={editor.getAttributes("textStyle")?.fontSize || ""}
                onChange={(e) => {
                  const v = e.target.value;
                  if (!v) editor.chain().focus().unsetFontSize().run();
                  else editor.chain().focus().setFontSize(v).run();
                }}
              >
                <option value="">По умолчанию</option>
                <option value="12">12</option>
                <option value="14">14</option>
                <option value="16">16</option>
                <option value="18">18</option>
                <option value="20">20</option>
                <option value="24">24</option>
                <option value="28">28</option>
                <option value="32">32</option>
              </select>
            </div>

            <div className="px-2 py-2">
              <div className="text-xs text-neutral-500 mb-1">Цвет текста</div>
              <div className="flex items-center gap-2">
                <input
                  type="color"
                  className="h-9 w-12 rounded-lg border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-950 p-1"
                  value={editor.getAttributes("textStyle")?.color || "#000000"}
                  onChange={(e) => editor.chain().focus().setColor(e.target.value).run()}
                />
                <button
                  type="button"
                  className="h-9 px-3 rounded-lg text-sm hover:bg-neutral-100 dark:hover:bg-neutral-800"
                  onClick={() => editor.chain().focus().unsetColor().run()}
                >
                  Сбросить
                </button>
              </div>
            </div>

            <div className="h-px bg-neutral-200 dark:bg-neutral-800 my-1" />
            <button
              type="button"
              className="w-full text-left px-2 py-2 rounded-lg text-sm hover:bg-neutral-100 dark:hover:bg-neutral-800"
              onClick={() => {
                editor.chain().focus().unsetAllMarks().clearNodes().run();
                setCtxMenu((s) => ({ ...s, open: false }));
              }}
            >
              Очистить форматирование
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

export default StatementEditor;
