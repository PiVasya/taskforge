import React, { useMemo } from "react";
import Editor from "react-simple-code-editor";
import Prism from "prismjs";

// ВАЖНО: отдадим Prism в глобальную область до импортов языков
if (typeof window !== "undefined") window.Prism = Prism;

// Порядок имеет значение:
import "prismjs/components/prism-clike";
import "prismjs/components/prism-c";       // <-- ДО cpp
import "prismjs/components/prism-cpp";
import "prismjs/components/prism-csharp";
import "prismjs/components/prism-python";

import "prismjs/themes/prism-tomorrow.css"; // или prism.css

const LANG_MAP = { csharp: "csharp", cpp: "cpp", python: "python" };

export default function CodeEditor({ language="cpp", value, onChange, placeholder, className="" }) {
  const prismLang = useMemo(
    () => Prism.languages[LANG_MAP[language]] || Prism.languages.clike,
    [language]
  );

  return (
    <div className={"rounded-xl overflow-hidden border border-slate-200 dark:border-slate-800 bg-slate-900 " + className}>
      <Editor
        value={value || ""}
        onValueChange={onChange}
        padding={16}
        placeholder={placeholder}
        highlight={(code) => {
          try { return Prism.highlight(code, prismLang, language); }
          catch { return code; } // safety net, чтобы не уронить весь React
        }}
        style={{
          fontFamily:
            "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono','Courier New', monospace",
          fontSize: 14,
          lineHeight: "1.5",
          minHeight: 280,
          outline: "none",
          background: "transparent",
          color: "#e5e7eb",
          caretColor: "#60a5fa",
          whiteSpace: "pre",
        }}
      />
    </div>
  );
}
