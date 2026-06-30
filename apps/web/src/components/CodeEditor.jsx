import React, { useCallback, useEffect, useMemo, useState } from 'react';
import Editor from 'react-simple-code-editor';
import Prism from 'prismjs';
import 'prismjs/components/prism-clike';
import 'prismjs/components/prism-c';
import 'prismjs/components/prism-cpp';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-python';
import 'prismjs/components/prism-java';
import 'prismjs/components/prism-javascript';
import 'prismjs/components/prism-pascal';

const CODE_EDITOR_STYLE_KEY = 'codeEditorStyle';

function normalizeEditorStyle(value) {
  return value === 'mono' ? 'mono' : 'color';
}

function readCodeEditorStyle() {
  try {
    return normalizeEditorStyle(localStorage.getItem(CODE_EDITOR_STYLE_KEY));
  } catch {
    return 'color';
  }
}

function normalizeLanguage(language) {
  switch (String(language || '').toLowerCase()) {
    case 'c':
      return 'c';
    case 'c++':
    case 'cpp':
      return 'cpp';
    case 'c#':
    case 'cs':
    case 'csharp':
      return 'csharp';
    case 'python':
    case 'py':
      return 'python';
    case 'js':
    case 'node':
    case 'nodejs':
    case 'javascript':
      return 'javascript';
    case 'pas':
    case 'pascal':
      return 'pascal';
    case 'java':
      return 'java';
    default:
      return 'text';
  }
}

function escapeHtml(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function highlightCode(code, language, editorStyle) {
  const text = String(code ?? '');
  if (editorStyle === 'mono') return escapeHtml(text);

  const grammar = Prism.languages[language] || Prism.languages.clike;
  if (!grammar) return escapeHtml(text);

  try {
    return Prism.highlight(text, grammar, language);
  } catch {
    return escapeHtml(text);
  }
}

function getLineCount(value) {
  return Math.max(1, String(value ?? '').split('\n').length);
}

export default function CodeEditor({
  language = 'cpp',
  value,
  onChange,
  height = 320,
  lineNumbers = 'on',
  readOnly = false,
}) {
  const [editorStyle, setEditorStyle] = useState(readCodeEditorStyle);
  const code = value ?? '';
  const normalizedLanguage = useMemo(() => normalizeLanguage(language), [language]);
  const showLineNumbers = lineNumbers !== 'off' && lineNumbers !== false;
  const lineCount = useMemo(() => getLineCount(code), [code]);
  const lines = useMemo(
    () => Array.from({ length: lineCount }, (_, index) => index + 1),
    [lineCount]
  );

  useEffect(() => {
    const refreshSettings = () => setEditorStyle(readCodeEditorStyle());
    window.addEventListener('tf-ui-settings-changed', refreshSettings);
    window.addEventListener('storage', refreshSettings);
    return () => {
      window.removeEventListener('tf-ui-settings-changed', refreshSettings);
      window.removeEventListener('storage', refreshSettings);
    };
  }, []);

  const handleValueChange = useCallback((nextValue) => {
    if (readOnly) return;
    onChange?.(nextValue ?? '');
  }, [onChange, readOnly]);

  const highlighted = useCallback(
    (nextCode) => highlightCode(nextCode, normalizedLanguage, editorStyle),
    [normalizedLanguage, editorStyle]
  );

  return (
    <div
      className={`code-editor-shell taskforge-code-editor taskforge-code-editor--${editorStyle} rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0`}
      style={{ width: '100%', height }}
    >
      <div className="taskforge-code-editor__body" style={{ minHeight: height, height }}>
        {showLineNumbers && (
          <div className="taskforge-code-editor__lines" aria-hidden="true">
            {lines.map((line) => (
              <div key={line}>{line}</div>
            ))}
          </div>
        )}
        <Editor
          value={code}
          onValueChange={handleValueChange}
          highlight={highlighted}
          padding={8}
          tabSize={2}
          insertSpaces
          ignoreTabKey={false}
          readOnly={readOnly}
          textareaClassName="taskforge-code-editor__textarea"
          preClassName="taskforge-code-editor__pre"
          className="taskforge-code-editor__editor"
          style={{
            minHeight: height,
            fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace',
            fontSize: 14,
            lineHeight: '20px',
          }}
        />
      </div>
    </div>
  );
}
