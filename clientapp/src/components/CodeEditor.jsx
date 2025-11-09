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

  /** Регистрируем темы ДО создания редактора */
  const handleBeforeMount = useCallback((monaco) => {
    // Тёмная синяя
    monaco.editor.defineTheme('taskforge-blue-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: '', foreground: 'D8DEE9' },
        { token: 'comment', foreground: '7B8794' },
        { token: 'string', foreground: 'A3E635' },
        { token: 'number', foreground: 'F59E0B' },
        { token: 'keyword', foreground: '60A5FA', fontStyle: 'bold' },
        { token: 'type', foreground: '8BBAFF' },
        { token: 'function', foreground: 'F8FAFC' },
        { token: 'identifier', foreground: 'D8DEE9' },
      ],
      colors: {
        'editor.background': '#0b1422',
        'editorGutter.background': '#0b1422',
        'editor.foreground': '#D8DEE9',
        'editorLineNumber.foreground': '#5d6b7e',
        'editorLineNumber.activeForeground': '#a7b4c6',
        'editor.selectionBackground': '#1d2a41',
        'editor.inactiveSelectionBackground': '#1d2a41',
        'editorWidget.background': '#0f1a2b',
        'editorCursor.foreground': '#93c5fd',
        'editorBracketMatch.background': '#1e293b',
        'editorBracketMatch.border': '#334155',
        'editor.findMatchBackground': '#283b5f',
        'editor.findMatchHighlightBackground': '#20324f',
      },
    });

    // Светлая синяя
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
        'editor.inactiveSelectionBackground': '#E2E8F0',
        'editorWidget.background': '#FFFFFF',
        'editorCursor.foreground': '#1d4ed8',
        'editorBracketMatch.background': '#E0E7FF',
        'editorBracketMatch.border': '#C7D2FE',
        'editor.findMatchBackground': '#FDE68A',
        'editor.findMatchHighlightBackground': '#FEF3C7',
      },
    });

    // Светлая розовая (для темы html.pink)
    monaco.editor.defineTheme('taskforge-rose-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: 'A8A3AD' },
        { token: 'string', foreground: '16A34A' },
        { token: 'number', foreground: 'BE185D' },
        { token: 'keyword', foreground: 'DB2777', fontStyle: 'bold' },
        { token: 'type', foreground: 'EA580C' },
      ],
      colors: {
        'editor.background': '#FFF1F6',
        'editorGutter.background': '#FFF1F6',
        'editor.foreground': '#111827',
        'editorLineNumber.foreground': '#C08497',
        'editorLineNumber.activeForeground': '#8B5D75',
        'editor.selectionBackground': '#FBCFE8',
        'editor.inactiveSelectionBackground': '#FFE4F1',
        'editorWidget.background': '#FFFFFF',
        'editorCursor.foreground': '#DB2777',
        'editorBracketMatch.background': '#FCE7F3',
        'editorBracketMatch.border': '#F9A8D4',
        'editor.findMatchBackground': '#FDE68A',
        'editor.findMatchHighlightBackground': '#FEF3C7',
      },
    });
  }, []);

  /** Возвращает актуальное имя темы по классу на <html> */
  const pickTheme = useCallback(() => {
    const cls = document.documentElement.classList;
    if (cls.contains('dark')) return 'taskforge-blue-dark';
    if (cls.contains('pink')) return 'taskforge-rose-light';
    return 'taskforge-blue-light';
  }, []);

  /** Лэйаут по размеру контейнера */
  const layout = useCallback(() => {
    const ed = editorRef.current;
    const el = wrapperRef.current;
    if (!ed || !el) return;
    const w = Math.max(0, el.clientWidth);
    const h = typeof height === 'number' ? height : el.clientHeight || 0;
    requestAnimationFrame(() => ed.layout({ width: w, height: h }));
  }, [height]);

  /** mount: ссылки, тема, ResizeObserver */
  const handleMount = useCallback((editor, monaco) => {
    editorRef.current = editor;
    monacoRef.current = monaco;
    monaco.editor.setTheme(pickTheme());

    // ResizeObserver следит за контейнером
    roRef.current = new ResizeObserver(() => layout());
    roRef.current.observe(wrapperRef.current);

    // Следим за сменой темы (переключатель темы меняет классы на <html>)
    const mo = new MutationObserver(() => {
      monacoRef.current?.editor?.setTheme(pickTheme());
      layout();
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });

    // первый layout
    layout();

    return () => {
      roRef.current?.disconnect();
      mo.disconnect();
    };
  }, [layout, pickTheme]);

  return (
    <div
      ref={wrapperRef}
      className="rounded-xl overflow-hidden border border-slate-200 dark:border-slate-800 min-w-0"
      style={{ width: '100%' }}
    >
      <Editor
        height={height}
        language={monacoLang}
        theme={pickTheme()}
        value={value}
        onChange={(val) => onChange?.(val ?? '')}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        loading={<div className="p-4 text-sm text-slate-500">Загружаем редактор…</div>}
        options={{
          // типографика
          fontFamily: 'Cascadia Code, Fira Code, JetBrains Mono, Consolas, Menlo, Monaco, ui-monospace, monospace',
          fontLigatures: true,
          fontSize: 14,
          lineHeight: 22,

          // UI
          minimap: { enabled: false },
          scrollbar: { verticalScrollbarSize: 12, horizontalScrollbarSize: 12 },
          lineNumbers,
          automaticLayout: false, // свой layout стабильнее
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
