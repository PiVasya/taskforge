import React, { useEffect, useMemo, useRef, useState, useCallback } from 'react';
import Editor from '@monaco-editor/react';

function cssRgbToHex(value, fallback) {
  const text = String(value || '').trim();
  const parts = text.match(/\d+(?:\.\d+)?/g)?.slice(0, 3).map((n) => Math.max(0, Math.min(255, Number(n))));
  if (!parts || parts.length < 3 || parts.some((n) => Number.isNaN(n))) return fallback;
  return parts.map((n) => Math.round(n).toString(16).padStart(2, '0')).join('').toUpperCase();
}

function readThemeHex(name, fallback) {
  const styles = getComputedStyle(document.documentElement);
  return cssRgbToHex(styles.getPropertyValue(name), fallback);
}

function withAlpha(hex, alpha) {
  const clean = String(hex || '').replace('#', '').slice(0, 6).padEnd(6, '0');
  const a = Math.round(Math.max(0, Math.min(1, alpha)) * 255).toString(16).padStart(2, '0');
  return `${clean}${a}`.toUpperCase();
}

function defineDynamicMonacoThemes(monaco) {
  if (!monaco || typeof document === 'undefined') return;

  const accent = readThemeHex('--accent', '2563EB');
  const accent2 = readThemeHex('--accent2', accent);
  const accent3 = readThemeHex('--accent3', accent2);
  const text = readThemeHex('--text', '0F172A');
  const muted = readThemeHex('--text-muted', '64748B');
  const card = readThemeHex('--card', 'FFFFFF');
  const page = readThemeHex('--page-bg', 'F8FAFC');
  const border = readThemeHex('--border', 'CBD5E1');

  monaco.editor.defineTheme('taskforge-dynamic-dark', {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: '', foreground: 'D8DEE9' },
      { token: 'comment', foreground: muted },
      { token: 'string', foreground: accent3 },
      { token: 'number', foreground: accent2 },
      { token: 'keyword', foreground: accent, fontStyle: 'bold' },
      { token: 'type', foreground: accent2 },
      { token: 'function', foreground: 'F8FAFC' },
      { token: 'identifier', foreground: 'D8DEE9' },
    ],
    colors: {
      'editor.background': '#0f1115',
      'editorGutter.background': '#0f1115',
      'editor.foreground': '#D8DEE9',
      'editorLineNumber.foreground': `#${withAlpha(muted, 0.72)}`,
      'editorLineNumber.activeForeground': `#${accent2}`,
      'editor.selectionBackground': `#${withAlpha(accent, 0.30)}`,
      'editor.inactiveSelectionBackground': `#${withAlpha(accent, 0.18)}`,
      'editor.lineHighlightBackground': `#${withAlpha(accent, 0.10)}`,
      'editorCursor.foreground': `#${accent2}`,
      'scrollbarSlider.background': `#${withAlpha(border, 0.40)}`,
      'scrollbarSlider.hoverBackground': `#${withAlpha(accent, 0.38)}`,
      'scrollbarSlider.activeBackground': `#${withAlpha(accent, 0.55)}`,
      'editorIndentGuide.background': `#${withAlpha(border, 0.38)}`,
      'editorIndentGuide.activeBackground': `#${withAlpha(accent, 0.55)}`,
      'editorWidget.background': '#12151b',
      'editorWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.background': '#12151b',
      'editorSuggestWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.selectedBackground': `#${withAlpha(accent, 0.25)}`,
      'list.hoverBackground': `#${withAlpha(accent, 0.14)}`,
      'focusBorder': `#${accent}`,
    },
  });

  monaco.editor.defineTheme('taskforge-dynamic-light', {
    base: 'vs',
    inherit: true,
    rules: [
      { token: 'comment', foreground: muted },
      { token: 'string', foreground: accent2 },
      { token: 'number', foreground: accent },
      { token: 'keyword', foreground: accent, fontStyle: 'bold' },
      { token: 'type', foreground: accent2 },
      { token: 'function', foreground: text },
      { token: 'identifier', foreground: text },
    ],
    colors: {
      'editor.background': `#${card}`,
      'editorGutter.background': `#${card}`,
      'editor.foreground': `#${text}`,
      'editorLineNumber.foreground': `#${muted}`,
      'editorLineNumber.activeForeground': `#${accent}`,
      'editor.selectionBackground': `#${withAlpha(accent, 0.24)}`,
      'editor.inactiveSelectionBackground': `#${withAlpha(accent, 0.14)}`,
      'editor.lineHighlightBackground': `#${withAlpha(page, 0.92)}`,
      'editorIndentGuide.background': `#${withAlpha(border, 0.60)}`,
      'editorIndentGuide.activeBackground': `#${withAlpha(accent, 0.45)}`,
      'focusBorder': `#${accent}`,
    },
  });
}


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
  const editorRef = useRef(null);   
  const monacoRef = useRef(null);   
  const roRef = useRef(null);       

  
  const monacoLang = useMemo(() => {
    switch (String(language || '').toLowerCase()) {
      case 'c++':
      case 'cpp':
        return 'cpp';
      case 'c#':
      case 'cs':
      case 'csharp':
        return 'csharp';
      case 'python':
        return 'python';
      case 'py':
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
        return 'plaintext';
    }
  }, [language]);

  
  const pickThemeName = useCallback(() => {
    const dark = document.documentElement.classList.contains('dark');
    return dark ? 'taskforge-dynamic-dark' : 'taskforge-dynamic-light';
  }, []);

  
  const handleBeforeMount = useCallback((monaco) => {
    defineDynamicMonacoThemes(monaco);
    
    monaco.editor.defineTheme('taskforge-brand-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: '', foreground: 'D8DEE9' },
        { token: 'comment', foreground: '7B8794' },
        { token: 'string', foreground: 'A3E635' },
        { token: 'number', foreground: 'F59E0B' },
        { token: 'keyword', foreground: '60A5FA', fontStyle: 'bold' }, 
        { token: 'type', foreground: '38BDF8' },                       
        { token: 'function', foreground: 'F8FAFC' },
        { token: 'identifier', foreground: 'D8DEE9' },
      ],
      colors: {
        'editor.background': '#0f1115',
        'editorGutter.background': '#0f1115',
        'editor.foreground': '#D8DEE9',
        'editorLineNumber.foreground': '#5d6b7e',
        'editorLineNumber.activeForeground': '#a7b4c6',
        'editor.selectionBackground': '#1d2a41',
        'editor.inactiveSelectionBackground': '#172338',
        'editor.lineHighlightBackground': '#141821',
        'editorCursor.foreground': '#E5E7EB',
        'scrollbarSlider.background': '#2a3a5266',
        'scrollbarSlider.hoverBackground': '#2a3a5299',
        'scrollbarSlider.activeBackground': '#2a3a52cc',
        'editorIndentGuide.background': '#2a2f3a',
        'editorIndentGuide.activeBackground': '#3a4150',
        'editorWidget.background': '#12151b',
        'editorWidget.border': '#2a2f3a',
        'editorSuggestWidget.background': '#12151b',
        'editorSuggestWidget.border': '#2a2f3a',
        'editorSuggestWidget.selectedBackground': '#16243a',
        'list.hoverBackground': '#1a1f28',
        'focusBorder': '#60A5FA', 
      },
    });

    monaco.editor.defineTheme('taskforge-brand-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '94A3B8' },
        { token: 'string', foreground: '10B981' },
        { token: 'number', foreground: '2563EB' }, 
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
        'focusBorder': '#2563EB', 
      },
    });

    
    monaco.editor.defineTheme('taskforge-pink-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: '', foreground: 'D8DEE9' },
        { token: 'comment', foreground: '7B8794' },
        { token: 'string', foreground: 'A3E635' },
        { token: 'number', foreground: 'F59E0B' },
        { token: 'keyword', foreground: 'DB2777', fontStyle: 'bold' }, 
        { token: 'type', foreground: 'F0ABFC' },                       
        { token: 'function', foreground: 'F8FAFC' },
        { token: 'identifier', foreground: 'D8DEE9' },
      ],
      colors: {
        'editor.background': '#0f1115',
        'editorGutter.background': '#0f1115',
        'editor.foreground': '#D8DEE9',
        'editorLineNumber.foreground': '#7f5d6b',
        'editorLineNumber.activeForeground': '#d4a7b4',
        'editor.selectionBackground': '#3b143033',
        'editor.inactiveSelectionBackground': '#3b143022',
        'editor.lineHighlightBackground': '#141821',
        'editorCursor.foreground': '#E5E7EB',
        'scrollbarSlider.background': '#2a3a5266',
        'scrollbarSlider.hoverBackground': '#2a3a5299',
        'scrollbarSlider.activeBackground': '#2a3a52cc',
        'editorIndentGuide.background': '#2a2f3a',
        'editorIndentGuide.activeBackground': '#3a4150',
        'editorWidget.background': '#12151b',
        'editorWidget.border': '#2a2f3a',
        'editorSuggestWidget.background': '#12151b',
        'editorSuggestWidget.border': '#2a2f3a',
        'editorSuggestWidget.selectedBackground': '#16243a',
        'list.hoverBackground': '#1a1f28',
        'focusBorder': '#DB2777', 
      },
    });

    monaco.editor.defineTheme('taskforge-pink-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '94A3B8' },
        { token: 'string', foreground: '10B981' },
        { token: 'number', foreground: 'DB2777' }, 
        { token: 'keyword', foreground: 'BE185D', fontStyle: 'bold' }, 
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

    
    monaco.editor.defineTheme('taskforge-apple-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: '', foreground: 'D8DEE9' },
        { token: 'comment', foreground: '7B8794' },
        { token: 'string', foreground: 'A3E635' },
        { token: 'number', foreground: 'F59E0B' },
        { token: 'keyword', foreground: '84CC16', fontStyle: 'bold' }, 
        { token: 'type', foreground: 'BEF264' },                       
        { token: 'function', foreground: 'F8FAFC' },
        { token: 'identifier', foreground: 'D8DEE9' },
      ],
      colors: {
        'editor.background': '#0f1115',
        'editorGutter.background': '#0f1115',
        'editor.foreground': '#D8DEE9',
        'editorLineNumber.foreground': '#5d6b7e',
        'editorLineNumber.activeForeground': '#a7b4c6',
        'editor.selectionBackground': '#1d2a41',
        'editor.inactiveSelectionBackground': '#172338',
        'editor.lineHighlightBackground': '#141821',
        'editorCursor.foreground': '#E5E7EB',
        'scrollbarSlider.background': '#2a3a5266',
        'scrollbarSlider.hoverBackground': '#2a3a5299',
        'scrollbarSlider.activeBackground': '#2a3a52cc',
        'editorIndentGuide.background': '#2a2f3a',
        'editorIndentGuide.activeBackground': '#3a4150',
        'editorWidget.background': '#12151b',
        'editorWidget.border': '#2a2f3a',
        'editorSuggestWidget.background': '#12151b',
        'editorSuggestWidget.border': '#2a2f3a',
        'editorSuggestWidget.selectedBackground': '#16243a',
        'list.hoverBackground': '#1a1f28',
        'focusBorder': '#84CC16',
      },
    });

    monaco.editor.defineTheme('taskforge-apple-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '94A3B8' },
        { token: 'string', foreground: '10B981' },
        { token: 'number', foreground: '65A30D' }, 
        { token: 'keyword', foreground: '4D7C0F', fontStyle: 'bold' }, 
        { token: 'type', foreground: '65A30D' },
      ],
      colors: {
        'editor.background': '#FFFFFF',
        'editorGutter.background': '#FFFFFF',
        'editor.foreground': '#0F172A',
        'editorLineNumber.foreground': '#94A3B8',
        'editorLineNumber.activeForeground': '#475569',
        'editor.selectionBackground': '#ECFCCB',
        'editor.inactiveSelectionBackground': '#F7FEE7',
        'editor.lineHighlightBackground': '#F6F8FA',
        'editorIndentGuide.background': '#E5E7EB',
        'editorIndentGuide.activeBackground': '#CBD5E1',
        'focusBorder': '#65A30D',
      },
    });
  }, []);

  
  const relayout = useCallback(() => {
    const ed = editorRef.current;
    const el = wrapperRef.current;
    if (!ed || !el) return;
    const w = Math.max(0, el.clientWidth);
    const h = typeof height === 'number' ? height : el.clientHeight || 0;
    requestAnimationFrame(() => ed.layout({ width: w, height: h }));
  }, [height]);

  
  const handleMount = useCallback((editor, monaco) => {
    editorRef.current = editor;
    monacoRef.current = monaco;

    monaco.editor.setTheme(pickThemeName());

    if (wrapperRef.current && !roRef.current) {
      roRef.current = new ResizeObserver(() => relayout());
      roRef.current.observe(wrapperRef.current);
    }
    const onWinResize = () => relayout();
    window.addEventListener('resize', onWinResize);
    window.addEventListener('orientationchange', onWinResize);

    relayout();

    return () => {
      window.removeEventListener('resize', onWinResize);
      window.removeEventListener('orientationchange', onWinResize);
      roRef.current?.disconnect();
      roRef.current = null;
    };
  }, [pickThemeName, relayout]);

  
  useEffect(() => {
    const mo = new MutationObserver(() => {
      const dark = document.documentElement.classList.contains('dark');
      setIsDark(dark);
      try {
        if (monacoRef.current) defineDynamicMonacoThemes(monacoRef.current);
        monacoRef.current?.editor?.setTheme(pickThemeName());
      } catch {}
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => mo.disconnect();
  }, [pickThemeName]);

  return (
    <div
      ref={wrapperRef}
      className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
      style={{ width: '100%' }}
    >
      <Editor
        height={height}
        language={monacoLang}
        theme={isDark ? pickThemeName() : pickThemeName()}
        value={value}
        onChange={(v) => onChange?.(v ?? '')}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        options={{
          lineNumbers,
          lineNumbersMinChars: 2,
          lineDecorationsWidth: 12,
          glyphMargin: false,
          folding: false,

          fontSize: 14,
          lineHeight: 20,
          letterSpacing: 0.2,
          padding: { top: 8, bottom: 8 },

          minimap: { enabled: false },
          automaticLayout: false,
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
