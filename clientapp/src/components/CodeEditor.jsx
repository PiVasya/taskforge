import React, { useMemo, useCallback } from "react";
import Editor from "react-simple-code-editor";
import Prism from "prismjs";

// Сделаем Prism доступным глобально (важно для подключаемых языков)
if (typeof window !== "undefined") {
  // не трогаем уже установленный, чтобы избежать переопределений в HMR
  // eslint-disable-next-line no-undef
  window.Prism = window.Prism || Prism;
}

/** ПОРЯДОК ИМПОРТОВ ВАЖЕН */
import "prismjs/components/prism-clike";
import "prismjs/components/prism-c";       // должен быть ДО cpp
import "prismjs/components/prism-cpp";
import "prismjs/components/prism-csharp";
import "prismjs/components/prism-python";

// Тема подсветки (можешь заменить на prism.css или любую другую из prism-themes)
import "prismjs/themes/prism-tomorrow.css";

const LANG_MAP = { csharp: "csharp", cpp: "cpp", python: "python" };

export default function CodeEditor({
  language = "cpp",
  value,
  onChange,
  placeholder,
  className = "",
  height = 320,
  readOnly = false,
}) {
  const prismLang = useMemo(() => {
    const key = LANG_MAP[language] || "clike";
    return Prism.languages[key] || Prism.languages.clike;
  }, [language]);

  // Безопасный highlight — если что-то не так с грамматикой, просто вернём исходный текст
  const highlight = useCallback(
    (code) => {
      try {
        const gram = prismLang || Prism.languages.clike;
        return Prism.highlight(code, gram, LANG_MAP[language] || "clike");
      } catch {
        return code;
      }
    },
    [prismLang, language]
  );

  // Поддержка Tab -> вставляем 2 пробела (или поменяй на "\t")
  const handleKeyDown = useCallback((e) => {
    if (readOnly) return;
    if (e.key === "Tab") {
      e.preventDefault();
      const el = e.target;
      const start = el.selectionStart;
      const end = el.selectionEnd;
      const before = (value ?? "").slice(0, start);
      const after = (value ?? "").slice(end);
      const insert = "  "; // или "\t"
      const next = before + insert + after;
      onChange?.(next);
      // Восстановим каретку после вставки
      requestAnimationFrame(() => {
        try {
          el.selectionStart = el.selectionEnd = start + insert.length;
        } catch {}
      });
    }
  }, [value, onChange, readOnly]);

  return (
    <div
      className={
        "rounded-xl overflow-hidden border border-slate-200 dark:border-slate-800 bg-slate-900 " +
        className
      }
      style={{ lineHeight: 0 }}
    >
      <Editor
        value={value || ""}
        onValueChange={(v) => onChange?.(v ?? "")}
        highlight={highlight}
        padding={14}
        readOnly={readOnly}
        placeholder={placeholder}
        textareaId="code-editor"
        aria-label="Редактор кода"
        onKeyDown={handleKeyDown}
        style={{
          fontFamily:
            "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono','Courier New', monospace",
          fontSize: 14,
          lineHeight: "1.5",
          minHeight: height,
          background: "transparent",
          color: "#e5e7eb",
          caretColor: "#60a5fa",
          outline: "none",
          // Правильные переносы и горизонтальный скролл без ломания подсветки
          whiteSpace: "pre",
          overflow: "auto",
        }}
      />
    </div>
  );
}
