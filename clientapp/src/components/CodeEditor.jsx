import React, { useMemo } from "react";
import Editor from "react-simple-code-editor";
import Prism from "prismjs";

// базовые языки
import "prismjs/components/prism-clike";
import "prismjs/components/prism-csharp";
import "prismjs/components/prism-cpp";
import "prismjs/components/prism-python";

// своя минимальная тема
const styles = {
  root: {
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace',
    fontSize: 14,
    lineHeight: 1.5,
    borderRadius: "0.75rem",
    border: "1px solid var(--tw-prose-pre-border, rgba(148,163,184,0.3))",
    background: "var(--tw-prose-pre-bg, #0f172a)",
    color: "white",
    padding: "0.75rem 0.9rem",
    minHeight: 280,
  },
};

const langMap = {
  csharp: "csharp",
  cpp: "cpp",
  python: "python",
};

export default function CodeEditor({ language = "csharp", value, onChange, placeholder }) {
  const grammar = useMemo(() => {
    const key = langMap[language] || "clike";
    return Prism.languages[key] || Prism.languages.clike;
  }, [language]);

  const highlight = (code) => Prism.highlight(code, grammar, language);

  return (
    <Editor
      value={value}
      onValueChange={onChange}
      highlight={highlight}
      padding={12}
      placeholder={placeholder}
      style={styles.root}
      textareaClassName="outline-none"
      preClassName="language-none"
    />
  );
}
