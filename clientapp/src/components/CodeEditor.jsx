import React, { useEffect, useMemo, useState } from 'react';
import Editor, { useMonaco } from '@monaco-editor/react';

/**
 * Пропсы:
 *  - language: 'cpp' | 'csharp' | 'python'
 *  - value: string
 *  - onChange: (code: string) => void
 *  - height?: number | string
 *  - lineNumbers?: 'on' | 'off'   // по умолчанию 'on'
 */
export default function CodeEditor({
  language = 'cpp',
  value,
  onChange,
  height = 320,
  lineNumbers = 'on',
}) {
  const monaco = useMonaco();
  const [isDark, setIsDark] = useState(
    document.documentElement.classList.contains('dark')
  );

  // Регистрируем «синие» темы
  useEffect(() => {
    if (!monaco) return;

    // тёмная синяя (в тон сайту)
    monaco.editor.defineTheme('taskforge-blue-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: '', foreground: 'D8DEE9' },
        { token: 'comment', foreground: '7B8794' },
        { token: 'string', foreground: 'A3E635' },   // салатовый
        { token: 'number', foreground: 'F59E0B' },   // янтарный
        { token: 'keyword', foreground: '60A5FA', fontStyle: 'bold' }, // синеватые ключевые
        { token: 'type', foreground: '8BBAFF' },
        { token: 'function', foreground: 'F8FAFC' },
        { token: 'identifier', foreground: 'D8DEE9' },
      ],
      colors: {
        // фон и гаттер — глубокий тёмно-синий
        'editor.background': '#0b1422',
        'editorGutter.background': '#0b1422',
        'editor.foreground': '#D8DEE9',

        // номера строк — включены и менее навязчивые
        'editorLineNumber.foreground': '#5d6b7e',
        'editorLineNumber.activeForeground': '#a7b4c6',

        // выделения/скролл/границы — холодные тона
        'editor.selectionBackground': '#1d2a41',
        'editor.inactiveSelectionBackground': '#172338',
        'editor.lineHighlightBackground': '#111c2d',
        'editorCursor.foreground': '#E5E7EB',

        'scrollbarSlider.background': '#2a3a5266',
        'scrollbarSlider.hoverBackground': '#2a3a5299',
        'scrollbarSlider.activeBackground': '#2a3a52cc',

        'editorIndentGuide.background': '#233047',
        'editorIndentGuide.activeBackground': '#2f3e5b',

        'editorWidget.background': '#0e1a2b',
        'editorWidget.border': '#20314a',
        'editorSuggestWidget.background': '#0e1a2b',
        'editorSuggestWidget.border': '#20314a',
        'editorSuggestWidget.selectedBackground': '#16243a',
        'list.hoverBackground': '#132035',
      },
    });

    // светлая (белая) тема
    monaco.editor.defineTheme('taskforge-blue-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '94A3B8' },
        { token: 'string', foreground: '10B981' },
        { token: 'number', foreground: 'DB2777' },
        { token: 'keyword', foreground: '2563EB', fontStyle: 'bold' },
        { token: 'type', foreground: '0EA5E9' },
      ],
      colors: {
        'editor.background': '#FFFFFF',
        'editorGutter.background': '#FFFFFF',
        'editor.foreground': '#0F172A',

        'editorLineNumber.foreground': '#94A3B8',
        'editorLineNumber.activeForeground': '#475569',

        'editor.selectionBackground': '#CDE3FF',
        'editor.inactiveSelectionBackground': '#E6F0FF',
        'editor.lineHighlightBackground': '#F6F8FA',

        'editorIndentGuide.background': '#E5E7EB',
        'editorIndentGuide.activeBackground': '#CBD5E1',
      },
    });
  }, [monaco]);

  // отслеживаем переключение темы сайта
  useEffect(() => {
    const mo = new MutationObserver(() => {
      setIsDark(document.documentElement.classList.contains('dark'));
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => mo.disconnect();
  }, []);

  // соответствие языков Monaco
  const monacoLang = useMemo(() => {
    switch (language) {
      case 'cpp': return 'cpp';
      case 'csharp': return 'csharp';
      case 'python': return 'python';
      default: return 'plaintext';
    }
  }, [language]);

  return (
    <div className="rounded-xl overflow-hidden border border-slate-200 dark:border-slate-800">
      <Editor
        height={height}
        language={monacoLang}
        theme={isDark ? 'taskforge-blue-dark' : 'taskforge-blue-light'}
        value={value}
        onChange={(v) => onChange?.(v ?? '')}
        options={{
          // Номера строк включены и компактные
          lineNumbers,
          lineNumbersMinChars: 2,          // уже гаттер для цифр
          lineDecorationsWidth: 0,         // без дополнительного поля слева
          glyphMargin: false,
          folding: false,

          // Стиль
          fontSize: 14,
          lineHeight: 20,
          letterSpacing: 0.2,
          padding: { top: 8, bottom: 8 },

          // UX
          minimap: { enabled: false },
          automaticLayout: true,
          wordWrap: 'on',
          tabSize: 2,
          insertSpaces: true,
          renderWhitespace: 'selection',
          renderLineHighlight: 'line',
          scrollBeyondLastLine: false,
          smoothScrolling: true,
          mouseWheelZoom: true,
        }}
      />
    </div>
  );
}
