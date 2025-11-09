import React, { useEffect, useMemo, useRef, useState, useCallback } from 'react';
import Editor from '@monaco-editor/react';

/**
 * Пропсы:
 *  - language: 'cpp' | 'csharp' | 'python'
 *  - value: string
 *  - onChange: (code: string) => void
 *  - height?: number | string
 *  - lineNumbers?: 'on' | 'off'
 */
export default function CodeEditor({
  language = 'cpp',
  value,
  onChange,
  height = 320,
  lineNumbers = 'on',
}) {
  const [isDark, setIsDark] = useState(
    document.documentElement.classList.contains('dark')
  );

  const wrapperRef = useRef(null);
  const editorRef = useRef(null);   // monaco.editor.IStandaloneCodeEditor
  const monacoRef = useRef(null);   // monaco namespace
  const roRef = useRef(null);       // ResizeObserver

  // соответствие языков Monaco
  const monacoLang = useMemo(() => {
    switch (language) {
      case 'cpp': return 'cpp';
      case 'csharp': return 'csharp';
      case 'python': return 'python';
      default: return 'plaintext';
    }
  }, [language]);

  // РЕГИСТРАЦИЯ ТЕМ — ДО создания редактора
  const handleBeforeMount = useCallback((monaco) => {
    monaco.editor.defineTheme('taskforge-blue-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: '', foreground: 'D8DEE9' },
        { token: 'comment', foreground: '7B8794' },
        { token: 'string', foreground: 'A3E635' },
        { token: 'number', foreground: 'F59E0B' },
        /* было 60A5FA — убираем синеву */
        { token: 'keyword', foreground: 'DB2777', fontStyle: 'bold' }, /* rose-600 */
        /* было 8BBAFF — мягкая фуксия */
        { token: 'type', foreground: 'F0ABFC' },                       /* fuchsia-300 */
        { token: 'function', foreground: 'F8FAFC' },
        { token: 'identifier', foreground: 'D8DEE9' },
      ],
      colors: {
        'editor.background': '#0b1422',
        'editorGutter.background': '#0b1422',
        'editor.foreground': '#D8DEE9',
        /* номера строк чуть «теплее», активный — светлее */
        'editorLineNumber.foreground': '#7f5d6b',
        'editorLineNumber.activeForeground': '#d4a7b4',
        /* селекшн с лёгким розовым оттенком */
        'editor.selectionBackground': '#3b143033',
        'editor.inactiveSelectionBackground': '#3b143022',
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
        'focusBorder': '#DB2777',   
      },
    });

    monaco.editor.defineTheme('taskforge-blue-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '94A3B8' },
        { token: 'string', foreground: '10B981' },
        { token: 'number', foreground: 'DB2777' },
        /* было 2563EB — заменили на rose-700 */
        { token: 'keyword', foreground: 'BE185D', fontStyle: 'bold' },
        /* было 0EA5E9 — заменили на розовый */
        { token: 'type', foreground: 'DB2777' },
      ],
      colors: {
        'editor.background': '#FFFFFF',
        'editorGutter.background': '#FFFFFF',
        'editor.foreground': '#0F172A',
        'editorLineNumber.foreground': '#94A3B8',
        'editorLineNumber.activeForeground': '#475569',
        'editor.selectionBackground': '#FBCFE833',
        'editor.inactiveSelectionBackground': '#FBCFE822',
        'editor.lineHighlightBackground': '#F6F8FA',
        'editorIndentGuide.background': '#E5E7EB',
        'editorIndentGuide.activeBackground': '#CBD5E1',
        'focusBorder': '#F472B6',
      },
    });
  }, []);

  // хелпер — безопасно перелэйаутить редактор
  const relayout = useCallback(() => {
    const ed = editorRef.current;
    const el = wrapperRef.current;
    if (!ed || !el) return;
    const w = Math.max(0, el.clientWidth);
    const h = typeof height === 'number' ? height : el.clientHeight || 0;
    // запросим кадр, чтобы не дёргать layout чаще, чем перерисовка
    requestAnimationFrame(() => ed.layout({ width: w, height: h }));
  }, [height]);

  // ПРИ МАУНТЕ — запомним ссылки, установим тему, поднимем ResizeObserver
  const handleMount = useCallback((editor, monaco) => {
    editorRef.current = editor;
    monacoRef.current = monaco;
    monaco.editor.setTheme(
      document.documentElement.classList.contains('dark')
        ? 'taskforge-blue-dark'
        : 'taskforge-blue-light'
    );

    // наблюдаем изменения размеров контейнера (брейкпоинты/масштаб/скрытие)
    if (wrapperRef.current && !roRef.current) {
      roRef.current = new ResizeObserver(() => relayout());
      roRef.current.observe(wrapperRef.current);
    }

    // перестраиваемся на ресайз окна и смену ориентации
    const onWinResize = () => relayout();
    window.addEventListener('resize', onWinResize);
    window.addEventListener('orientationchange', onWinResize);

    // первая раскладка
    relayout();

    // очистка
    return () => {
      window.removeEventListener('resize', onWinResize);
      window.removeEventListener('orientationchange', onWinResize);
      roRef.current?.disconnect();
      roRef.current = null;
    };
  }, [relayout]);

  // Реакция на переключение темы сайта (класс .dark)
  useEffect(() => {
    const mo = new MutationObserver(() => {
      const dark = document.documentElement.classList.contains('dark');
      setIsDark(dark);
      try {
        monacoRef.current?.editor?.setTheme(
          dark ? 'taskforge-blue-dark' : 'taskforge-blue-light'
        );
      } catch {}
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => mo.disconnect();
  }, []);

  return (
    <div
      ref={wrapperRef}
      className="rounded-xl overflow-hidden border border-slate-200 dark:border-slate-800 min-w-0"
      style={{ width: '100%' }}
    >
      <Editor
        height={height}
        language={monacoLang}
        theme={isDark ? 'taskforge-blue-dark' : 'taskforge-blue-light'}
        value={value}
        onChange={(v) => onChange?.(v ?? '')}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        options={{
          // Номера строк и «зазор» после них
          lineNumbers,
          lineNumbersMinChars: 2,
          lineDecorationsWidth: 12,
          glyphMargin: false,
          folding: false,

          // Стиль
          fontSize: 14,
          lineHeight: 20,
          letterSpacing: 0.2,
          padding: { top: 8, bottom: 8 },

          // UX
          minimap: { enabled: false },
          automaticLayout: false, // мы делаем свой layout — стабильнее
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
