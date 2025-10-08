import React, { useEffect, useMemo, useRef, useState } from 'react';
import Editor from '@monaco-editor/react';

const langMap = {
  cpp: 'cpp',
  csharp: 'csharp',
  python: 'python',
};

export default function CodeEditor({ language = 'cpp', value, onChange, height = 320 }) {
  const [theme, setTheme] = useState(
    document.documentElement.classList.contains('dark') ? 'vs-dark' : 'vs'
  );

  useEffect(() => {
    const mo = new MutationObserver(() => {
      setTheme(document.documentElement.classList.contains('dark') ? 'vs-dark' : 'vs');
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => mo.disconnect();
  }, []);

  const monacoLang = langMap[language] || 'plaintext';

  return (
    <div className="rounded-xl overflow-hidden border border-slate-200 dark:border-slate-800">
      <Editor
        height={height}
        language={monacoLang}
        theme={theme}
        value={value}
        onChange={(v) => onChange?.(v ?? '')}
        options={{
          fontSize: 14,
          minimap: { enabled: false },
          automaticLayout: true,
          scrollBeyondLastLine: false,
          wordWrap: 'on',
          tabSize: 2,
          insertSpaces: true,
          renderWhitespace: 'selection',
          rulers: [100],
        }}
      />
    </div>
  );
}
